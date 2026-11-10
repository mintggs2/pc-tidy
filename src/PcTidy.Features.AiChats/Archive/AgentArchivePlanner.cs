using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Archive;

public sealed record ArchivePlan(IReadOnlyList<PlanItem> Items, int Sessions, long Bytes, int SkippedLive, int AlreadyDone, IReadOnlyList<string> Notes)
{
    public ActionPlan ToActionPlan(string title) => new() { FeatureId = AiChatsIds.FeatureId, Title = title, Items = PlanBuilder.Normalize(Items) };
}

/// <summary>Is there a verified copy of a session in the archive folder?</summary>
public sealed record CopyCheck(bool Verified, string? CopyPath, string? Problem);

/// <summary>
/// "Archive a copy" of local agent chats: each transcript (and its subagent transcripts) is COPIED into
/// &lt;archive&gt;\&lt;Claude Code|Codex&gt;\&lt;project&gt;\ (CopyPath: copy, verify by hash, original stays), with an optional Markdown
/// copy beside it. Moving would break Claude Code's --resume, so originals are only ever removed in a separate, explicit
/// step, after their copy is verified here, never for live sessions, and only into quarantine (restorable).
/// Read-only: it plans; the engine copies.
/// </summary>
public static class AgentArchivePlanner
{
    public const string ResumeNote = "Claude Code's --resume won't find a session while it's in quarantine; restore it from Quarantine to resume it again.";

    public static string DestinationFolder(string archiveRoot, AgentSession s)
        => Path.Combine(archiveRoot, ChatSources.Display(s.Source), AgentProjects.FolderName(s.ProjectName));

    public static string CopyPathFor(string archiveRoot, AgentSession s)
        => Path.Combine(DestinationFolder(archiveRoot, s), Path.GetFileName(s.TranscriptPath));

    /// <summary>Subagent transcripts keep their place relative to the session's sidecar folder.</summary>
    public static string SidecarCopyPath(string archiveRoot, AgentSession s, SidecarFile f)
        => Path.Combine(DestinationFolder(archiveRoot, s), s.SessionId, Path.GetRelativePath(s.SidecarDir!, f.Path));

    public static string MarkdownPathFor(string archiveRoot, AgentSession s)
        => Path.Combine(DestinationFolder(archiveRoot, s), StemOf(s.TranscriptPath) + ".md");

    /// <param name="markdown">Optional Markdown copy per session (library id → note text, already masked).</param>
    public static ArchivePlan PlanCopy(IReadOnlyList<AgentSession> sessions, string archiveRoot, LiveSessions live,
        IReadOnlyDictionary<string, string>? markdown, CancellationToken ct)
    {
        archiveRoot = PathUtil.Normalize(archiveRoot);
        var items = new List<PlanItem>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int planned = 0, skippedLive = 0, already = 0;
        long bytes = 0;
        bool sidecarExtras = false;
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (live.IsLive(s)) { skippedLive++; continue; }
            var files = new List<(string Source, string Target, long Size, DateTime LastWrite)> { (s.TranscriptPath, CopyPathFor(archiveRoot, s), s.TranscriptBytes, s.TranscriptWriteUtc) };
            foreach (var f in s.SidecarTranscripts) files.Add((f.Path, SidecarCopyPath(archiveRoot, s, f), f.Size, f.LastWriteUtc));
            if (s.SidecarDir is not null && s.TotalBytes > s.TranscriptBytes + s.SidecarTranscripts.Sum(f => f.Size)) sidecarExtras = true;

            var todo = files.Where(f => !SameFile(f.Source, f.Target, ct)).ToList();
            if (todo.Count == 0) { already++; continue; }
            planned++;
            foreach (var f in todo)
            {
                var dir = Path.GetDirectoryName(f.Target)!;
                if (folders.Add(dir) && !Directory.Exists(dir)) items.Add(Plans.CreateFolder(dir));
                items.Add(Plans.Copy(f.Source, f.Target, f.Size, f.LastWrite,
                    $"Copy the {ChatSources.Display(s.Source)} chat {Plans.Quote(f.Source)} ({s.ProjectName}, {Plans.Size(f.Size)}) to the archive. The original stays."));
                bytes += f.Size;
            }
            if (markdown is not null && markdown.TryGetValue(s.LibraryId, out var md))
            {
                var mdPath = MarkdownPathFor(archiveRoot, s);
                if (!File.Exists(mdPath)) items.Add(Plans.WriteText(mdPath, md, $"Save a readable copy {Plans.Quote(mdPath)} beside it"));
            }
        }
        var notes = new List<string>();
        if (skippedLive > 0) notes.Add($"{skippedLive} chat{(skippedLive == 1 ? " is" : "s are")} in use right now (or changed in the last 10 minutes), so {(skippedLive == 1 ? "it was" : "they were")} skipped.");
        if (already > 0) notes.Add($"{already} chat{(already == 1 ? " is" : "s are")} already in the archive, unchanged.");
        if (sidecarExtras) notes.Add("Big tool outputs Claude Code keeps beside a session aren't copied; the conversation and its subagent chats are.");
        return new ArchivePlan(items, planned, bytes, skippedLive, already, notes);
    }

    /// <summary>
    /// The separate "remove originals" step: only sessions whose copy is verified (same bytes, by hash) and that aren't
    /// live; each goes to quarantine (restorable). Never ticked by default; the page asks for an explicit OK every run.
    /// </summary>
    public static ArchivePlan PlanRemoveOriginals(IReadOnlyList<AgentSession> sessions, string archiveRoot, LiveSessions live, CancellationToken ct)
    {
        archiveRoot = PathUtil.Normalize(archiveRoot);
        var items = new List<PlanItem>();
        int planned = 0, skippedLive = 0, notCopied = 0;
        long bytes = 0;
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (live.IsLive(s)) { skippedLive++; continue; }
            var check = VerifyCopy(archiveRoot, s, ct);
            if (!check.Verified) { notCopied++; continue; }
            planned++;
            items.Add(Plans.Quarantine(s.TranscriptPath, s.TranscriptBytes, s.TranscriptWriteUtc,
                $"Move the original {ChatSources.Display(s.Source)} chat {Plans.Quote(s.TranscriptPath)} ({s.ProjectName}) to quarantine. Its copy in the archive was checked." +
                (s.Source == ChatSource.ClaudeCode ? " " + ResumeNote : "")));
            bytes += s.TranscriptBytes;
            foreach (var f in s.SidecarTranscripts)
            {
                items.Add(Plans.Quarantine(f.Path, f.Size, f.LastWriteUtc, $"Move its subagent chat {Plans.Quote(f.Path)} to quarantine (copy checked)"));
                bytes += f.Size;
            }
        }
        var notes = new List<string>();
        if (skippedLive > 0) notes.Add($"{skippedLive} chat{(skippedLive == 1 ? " is" : "s are")} in use right now, so {(skippedLive == 1 ? "it stays" : "they stay")}.");
        if (notCopied > 0) notes.Add($"{notCopied} chat{(notCopied == 1 ? " has" : "s have")} no checked copy in the archive yet, so {(notCopied == 1 ? "it stays" : "they stay")}. Archive a copy first.");
        return new ArchivePlan(items, planned, bytes, skippedLive, notCopied, notes);
    }

    /// <summary>The transcript and every subagent transcript have a byte-identical copy in the archive folder.</summary>
    public static CopyCheck VerifyCopy(string archiveRoot, AgentSession s, CancellationToken ct)
    {
        var copy = FindCopy(s.TranscriptPath, CopyPathFor(archiveRoot, s), ct);
        if (copy is null) return new CopyCheck(false, null, "No matching copy in the archive.");
        foreach (var f in s.SidecarTranscripts)
            if (FindCopy(f.Path, SidecarCopyPath(archiveRoot, s, f), ct) is null)
                return new CopyCheck(false, copy, "A subagent chat of this session isn't in the archive yet.");
        return new CopyCheck(true, copy, null);
    }

    /// <summary>The planned copy, or a numbered one ("x (2).jsonl") from an earlier archive run, with the same content.</summary>
    private static string? FindCopy(string source, string expected, CancellationToken ct)
    {
        if (SameFile(source, expected, ct)) return expected;
        var dir = Path.GetDirectoryName(expected)!;
        if (!Directory.Exists(dir)) return null;
        var stem = StemOf(expected);
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(dir, stem + " (*)*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (SameFile(source, candidate, ct)) return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Same length and same XXH3 hash (read-only).</summary>
    internal static bool SameFile(string a, string b, CancellationToken ct)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            if (CloudFile.IsPlaceholder(fb.Attributes)) return false;
            return FileHash.Full(a, ct) == FileHash.Full(b, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string StemOf(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)) return name[..^10];
        return Path.GetFileNameWithoutExtension(name);
    }
}
