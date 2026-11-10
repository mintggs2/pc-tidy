using System.Diagnostics;
using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>
/// AIC-2: the AI chats section's "remove originals" re-checked when each step runs (the guard checks every item right
/// before it runs), not only when the plan was built. A Claude Code transcript stays put while a running Claude Code
/// process says, in its <c>&lt;ClaudeDir&gt;\sessions\&lt;pid&gt;.json</c> marker, that it is writing that session.
/// Read-only (a directory listing, small JSON files, "is this process running?"); anything unclear counts as live.
/// </summary>
public sealed partial class SafetyGuard
{
    private const string AiChatsFeature = "aichats";
    private const int MaxMarkerBytes = 64 * 1024;
    private const int MaxMarkers = 256;

    /// <summary>A block when <paramref name="item"/> would quarantine a Claude Code transcript that is in use now; null otherwise.</summary>
    private SafetyVerdict? LiveClaudeTranscript(PlanItem item)
    {
        if (!string.Equals(item.FeatureId, AiChatsFeature, StringComparison.OrdinalIgnoreCase) || item.SourcePath is not { } raw) return null;
        var path = SafePath.TryNormalize(raw);
        if (path is null || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var claude in new[] { Map.ClaudeDir, Map.DefaultClaudeDir }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var projects = Path.Combine(claude, "projects");
            if (!PathUtil.IsUnder(path, projects)) continue;
            // <projects>\<project>\<session>.jsonl, or a subagent chat under <projects>\<project>\<session>\…
            var parts = Path.GetRelativePath(projects, path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var sessionId = parts.Length == 2 ? Path.GetFileNameWithoutExtension(parts[1]) : parts[1];
            return InUse(Path.Combine(claude, "sessions"), sessionId, path) ? SafetyVerdict.Block(Reasons.AiChatInUse) : null;
        }
        return null;
    }

    /// <summary>
    /// True when a running process's marker names <paramref name="sessionId"/>, or names no session while the transcript
    /// changed in the last day, or the markers can't be listed at all.
    /// </summary>
    private static bool InUse(string sessionsDir, string sessionId, string transcript)
    {
        if (!Directory.Exists(sessionsDir)) return false;
        List<string> markers;
        try
        {
            markers = Directory.EnumerateFiles(sessionsDir, "*.json", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                .Take(MaxMarkers + 1).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        if (markers.Count > MaxMarkers) return true;
        foreach (var marker in markers)
        {
            var (pid, markerSession) = ReadMarker(marker);
            if (pid is not { } p || !IsRunning(p)) continue;
            if (markerSession is null)
            {
                if (RecentlyWritten(transcript)) return true;
                continue;
            }
            if (string.Equals(markerSession, sessionId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static (int? Pid, string? SessionId) ReadMarker(string file)
    {
        int? fromName = int.TryParse(Path.GetFileNameWithoutExtension(file), out var n) && n > 0 ? n : null;
        try
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length > MaxMarkerBytes) return (fromName, null);
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (fromName, null);
            int? pid = root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v > 0 ? v : fromName;
            string? id = null;
            foreach (var name in new[] { "sessionId", "session_id" })
                if (root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } x) { id = x; break; }
            return (pid, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (fromName, null); }
    }

    /// <summary>Is the process running? Access problems count as "running" (fail safe).</summary>
    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
    }

    private static bool RecentlyWritten(string file)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
