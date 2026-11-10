using System.Text.Json;
using PcTidy.Core;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Local;

/// <summary>
/// Lists the agent chat transcripts on this PC. Looks ONLY in &lt;ClaudeDir&gt;\projects and &lt;CodexDir&gt;\sessions and opens
/// only transcript files (*.jsonl / *.jsonl.zst) plus Codex's session_index.jsonl (titles). Settings, logins, memory
/// and config files of those tools are never opened. Never follows links; skips online-only files.
/// </summary>
public static class AgentChatInventory
{
    private const int PeekLines = 60;

    private static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00400000 | (FileAttributes)0x00040000,
    };

    public static string ClaudeProjectsDir(IKnownLocations k) => Path.Combine(k.ClaudeDir, "projects");
    public static string CodexSessionsDir(IKnownLocations k) => Path.Combine(k.CodexDir, "sessions");

    public static async Task<IReadOnlyList<AgentSession>> ListAsync(IKnownLocations k, CancellationToken ct)
    {
        var list = new List<AgentSession>();
        list.AddRange(await ClaudeAsync(k, ct).ConfigureAwait(false));
        list.AddRange(await CodexAsync(k, ct).ConfigureAwait(false));
        return list;
    }

    public static async Task<IReadOnlyList<AgentSession>> ClaudeAsync(IKnownLocations k, CancellationToken ct)
    {
        var sessions = new List<AgentSession>();
        var root = ClaudeProjectsDir(k);
        if (!SafeDirectory(root)) return sessions;
        foreach (var projectDir in Safe(() => Directory.EnumerateDirectories(root, "*", Flat)))
        {
            foreach (var file in Safe(() => Directory.EnumerateFiles(projectDir, "*.jsonl", Flat)))
            {
                ct.ThrowIfCancellationRequested();
                var info = Info(file);
                if (info is null) continue;
                var sessionId = Path.GetFileNameWithoutExtension(file);
                var sidecar = Path.Combine(projectDir, sessionId);
                var (sideBytes, sideTranscripts) = SafeDirectory(sidecar) ? Sidecar(sidecar, ct) : (0L, (IReadOnlyList<SidecarFile>)Array.Empty<SidecarFile>());
                var cwd = await PeekAsync(file, "cwd", ct).ConfigureAwait(false);
                var folder = Path.GetFileName(projectDir);
                sessions.Add(new AgentSession
                {
                    Source = ChatSource.ClaudeCode,
                    TranscriptPath = file,
                    SessionId = sessionId,
                    ProjectFolder = folder,
                    Cwd = cwd,
                    // No cwd in the transcript: decode the folder name, and never show the user-name part of it.
                    DecodedProjectPath = cwd is null ? AgentProjects.Decode(folder, k.UserProfile) : null,
                    ProjectFolderLabel = AgentProjects.StripProfile(folder, k.UserProfile),
                    SidecarDir = SafeDirectory(sidecar) ? sidecar : null,
                    SidecarTranscripts = sideTranscripts,
                    TranscriptBytes = info.Length,
                    TotalBytes = info.Length + sideBytes,
                    LastWriteUtc = Max(info.LastWriteTimeUtc, sideTranscripts.Select(s => s.LastWriteUtc)),
                    TranscriptLastWriteUtc = info.LastWriteTimeUtc,
                    CreatedUtc = info.CreationTimeUtc,
                });
            }
        }
        return sessions;
    }

    public static async Task<IReadOnlyList<AgentSession>> CodexAsync(IKnownLocations k, CancellationToken ct)
    {
        var sessions = new List<AgentSession>();
        var root = CodexSessionsDir(k);
        if (!SafeDirectory(root)) return sessions;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4, // YYYY\MM\DD
            IgnoreInaccessible = true,
            AttributesToSkip = Flat.AttributesToSkip,
        };
        foreach (var file in Safe(() => Directory.EnumerateFiles(root, "rollout-*", options)))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCodexTranscript(file)) continue;
            var info = Info(file);
            if (info is null) continue;
            var meta = await PeekCodexAsync(file, ct).ConfigureAwait(false);
            sessions.Add(new AgentSession
            {
                Source = ChatSource.Codex,
                TranscriptPath = file,
                SessionId = meta.Id ?? CodexIdFromName(file),
                Cwd = meta.Cwd,
                TranscriptBytes = info.Length,
                TotalBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                CreatedUtc = info.CreationTimeUtc,
            });
        }
        return sessions;
    }

    public static bool IsCodexTranscript(string path)
        => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    /// <summary>"rollout-2025-05-07T17-24-21-5973b6c0-94b8-487b-a530-2aeb6098ae0e.jsonl.zst" → the trailing uuid.</summary>
    public static string CodexIdFromName(string path)
    {
        var name = Path.GetFileName(path);
        var stem = name.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase) ? name[..^10]
            : name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
        return stem.Length >= 36 && Guid.TryParse(stem[^36..], out _) ? stem[^36..] : stem;
    }

    /// <summary>Titles from Codex's session_index.jsonl (id → title). Missing or unreadable → empty.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> CodexTitlesAsync(IKnownLocations k, CancellationToken ct)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = Path.Combine(k.CodexDir, "session_index.jsonl");
        if (Info(index) is null) return titles;
        var bad = new JsonlReader.Counter();
        try
        {
            await foreach (var e in JsonlReader.ReadAsync(index, bad, ct).ConfigureAwait(false))
            {
                var id = e.Str("id") ?? e.Str("session_id") ?? e.Str("thread_id");
                var title = e.Str("thread_name") ?? e.Str("title") ?? e.Str("name") ?? e.Str("summary");
                if (id is not null && !string.IsNullOrWhiteSpace(title)) titles[id] = title.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return titles;
    }

    private static (long Bytes, IReadOnlyList<SidecarFile> Transcripts) Sidecar(string dir, CancellationToken ct)
    {
        long bytes = 0;
        var transcripts = new List<SidecarFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = Flat.AttributesToSkip };
        foreach (var f in Safe(() => new DirectoryInfo(dir).EnumerateFiles("*", options)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bytes += f.Length;
                if (f.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) transcripts.Add(new SidecarFile(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
            catch (IOException) { /* vanished */ }
        }
        return (bytes, transcripts);
    }

    private static async Task<string?> PeekAsync(string file, string property, CancellationToken ct)
    {
        var bad = new JsonlReader.Counter();
        try
        {
            await foreach (var e in JsonlReader.ReadAsync(file, bad, ct, PeekLines).ConfigureAwait(false))
                if (e.Str(property) is { Length: > 0 } v) return v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static async Task<(string? Id, string? Cwd)> PeekCodexAsync(string file, CancellationToken ct)
    {
        var bad = new JsonlReader.Counter();
        string? id = null, cwd = null;
        try
        {
            await foreach (var e in JsonlReader.ReadAsync(file, bad, ct, PeekLines).ConfigureAwait(false))
            {
                var rec = e.Prop("payload") is { ValueKind: JsonValueKind.Object } p ? p : e;
                id ??= e.Str("type") is "session_meta" or null ? rec.Str("id") : null;
                cwd ??= rec.Str("cwd");
                if (id is not null && cwd is not null) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (id is not null && Guid.TryParse(id, out _) ? id : null, cwd);
    }

    private static FileInfo? Info(string path)
    {
        try
        {
            var f = new FileInfo(path);
            if (!f.Exists || (f.Attributes & FileAttributes.ReparsePoint) != 0 || CloudFile.IsPlaceholder(f.Attributes)) return null;
            return f;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool SafeDirectory(string path)
    {
        try
        {
            var d = new DirectoryInfo(path);
            return d.Exists && (d.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> source)
    {
        IEnumerator<T> e;
        try { e = source().GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        using (e)
        {
            while (true)
            {
                bool more;
                try { more = e.MoveNext(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
                if (!more) yield break;
                yield return e.Current;
            }
        }
    }

    private static DateTime Max(DateTime first, IEnumerable<DateTime> rest)
    {
        var max = first;
        foreach (var d in rest) if (d > max) max = d;
        return max;
    }
}
