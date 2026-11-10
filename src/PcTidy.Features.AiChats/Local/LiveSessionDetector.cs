using System.Diagnostics;
using System.Text.Json;
using PcTidy.Core;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Local;

/// <summary>Is a process running, and when did it start? (Read-only. Tests use a fake.)</summary>
public interface IProcessProbe
{
    /// <summary>Null = no such process. <see cref="ProcessState.StartUtc"/> null = running, start time unknown (access denied).</summary>
    ProcessState? Find(int pid);
}

public sealed record ProcessState(DateTime? StartUtc);

public sealed class SystemProcessProbe : IProcessProbe
{
    public ProcessState? Find(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            try { return new ProcessState(p.StartTime.ToUniversalTime()); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return new ProcessState(null); }
        }
        catch (ArgumentException) { return null; } // not running
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Which agent chats may still be in use, so they are never moved or removed (copying is still fine):
/// a Claude Code session named by &lt;ClaudeDir&gt;\sessions\&lt;pid&gt;.json whose process is running with a matching start time;
/// a Codex session with a &lt;CodexDir&gt;\thread-writer-locks\&lt;id&gt;.lock file; or any transcript changed in the last 10 minutes.
/// When in doubt (a running PC Tidy can't read a start time, a session file names no session) it counts as live.
/// </summary>
public sealed class LiveSessionDetector
{
    /// <summary>A recorded start time this close to the process's own start time is the same process (not a reused PID).</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(10);

    private readonly IProcessProbe _processes;
    private readonly TimeProvider _clock;

    public LiveSessionDetector(IProcessProbe? processes = null, TimeProvider? clock = null)
    {
        _processes = processes ?? new SystemProcessProbe();
        _clock = clock ?? TimeProvider.System;
    }

    public LiveSessions Detect(IKnownLocations k)
    {
        var claudeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claudeCwds = new List<string>();
        var sessionsDir = Path.Combine(k.ClaudeDir, "sessions");
        foreach (var file in Files(sessionsDir, "*.json"))
        {
            var marker = ReadMarker(file);
            if (marker.Pid is not { } pid || !IsRunning(pid, marker.StartedUtc)) continue;
            if (marker.SessionId is { } id) claudeIds.Add(id);
            else if (marker.Cwd is { } cwd) claudeCwds.Add(cwd);
            else claudeCwds.Add("*"); // a running session we can't place: every recent Claude Code chat counts as live
        }
        var codexIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(Path.Combine(k.CodexDir, "thread-writer-locks"), "*.lock"))
            codexIds.Add(Path.GetFileNameWithoutExtension(file));
        return new LiveSessions(claudeIds, claudeCwds, codexIds, _clock.GetUtcNow().UtcDateTime);
    }

    private bool IsRunning(int pid, DateTime? recordedStartUtc)
    {
        var state = _processes.Find(pid);
        if (state is null) return false;
        if (recordedStartUtc is null || state.StartUtc is null) return true;
        return (state.StartUtc.Value - recordedStartUtc.Value).Duration() <= StartTolerance;
    }

    private sealed record Marker(int? Pid, DateTime? StartedUtc, string? SessionId, string? Cwd);

    /// <summary>Reads one small running-session marker. Its exact fields aren't documented, so several names are accepted.</summary>
    private static Marker ReadMarker(string file)
    {
        int? pidFromName = int.TryParse(Path.GetFileNameWithoutExtension(file), out var n) ? n : null;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 64 * 1024) return new Marker(pidFromName, null, null, null);
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(s, JsonUtil.Lenient);
            var r = doc.RootElement;
            int? pid = r.Long("pid") is { } p and > 0 and < int.MaxValue ? (int)p : pidFromName;
            var started = r.Time("startedAt") ?? r.Time("startTime") ?? r.Time("started_at") ?? r.Time("start_time");
            return new Marker(pid, started, r.Str("sessionId") ?? r.Str("session_id"), r.Str("cwd"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Marker(pidFromName, null, null, null);
        }
    }

    private static IEnumerable<string> Files(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}

/// <summary>The live-session facts at one moment.</summary>
public sealed record LiveSessions(IReadOnlySet<string> ClaudeSessionIds, IReadOnlyList<string> ClaudeCwds, IReadOnlySet<string> CodexSessionIds, DateTime NowUtc)
{
    public static LiveSessions None(DateTime nowUtc) => new(new HashSet<string>(), Array.Empty<string>(), new HashSet<string>(), nowUtc);

    /// <summary>Why this session is treated as live, or null when it's safe to move.</summary>
    public string? Reason(AgentSession s)
    {
        if (NowUtc - s.LastWriteUtc < AiChatsIds.LiveWindow)
            return "It changed in the last 10 minutes, so it may still be in use.";
        if (s.Source == ChatSource.ClaudeCode)
        {
            if (ClaudeSessionIds.Contains(s.SessionId)) return "Claude Code is running this session right now.";
            if (ClaudeCwds.Contains("*") && NowUtc - s.LastWriteUtc < TimeSpan.FromDays(1))
                return "A Claude Code session is running and this one is from the last day, so it's left alone to be safe.";
            if (ClaudeCwds.Any(c => AgentProjects.SameCwd(c, s.Cwd)) && NowUtc - s.LastWriteUtc < TimeSpan.FromDays(1))
                return "Claude Code is running in this project right now.";
        }
        if (s.Source == ChatSource.Codex && CodexSessionIds.Contains(s.SessionId))
            return "Codex is writing this session right now.";
        return null;
    }

    public bool IsLive(AgentSession s) => Reason(s) is not null;
}
