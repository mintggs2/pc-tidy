using PcTidy.Core;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Local;

/// <summary>
/// One local agent chat on this PC: a Claude Code transcript (&lt;ClaudeDir&gt;\projects\&lt;folder&gt;\&lt;session&gt;.jsonl, plus
/// its sidecar folder) or a Codex rollout (&lt;CodexDir&gt;\sessions\YYYY\MM\DD\rollout-….jsonl[.zst]).
/// </summary>
public sealed record AgentSession
{
    public required ChatSource Source { get; init; }
    public required string TranscriptPath { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Claude Code: the (lossy) encoded project folder name under projects\. Codex: null.</summary>
    public string? ProjectFolder { get; init; }
    /// <summary>The working folder the agent ran in (from the records' cwd), when known.</summary>
    public string? Cwd { get; init; }
    /// <summary>
    /// Claude Code without a cwd: the project folder decoded from <see cref="ProjectFolder"/> by matching it against the
    /// folders that exist (null when it couldn't be matched).
    /// </summary>
    public string? DecodedProjectPath { get; init; }
    /// <summary>The encoded folder name with the user-profile part removed ("source-repos-my-app"), for when decoding failed.</summary>
    public string? ProjectFolderLabel { get; init; }
    /// <summary>Claude Code's per-session sidecar folder (subagent transcripts, big tool outputs), if there is one.</summary>
    public string? SidecarDir { get; init; }
    /// <summary>Transcript files inside the sidecar folder (subagents). Only these sidecar files are ever copied or moved.</summary>
    public IReadOnlyList<SidecarFile> SidecarTranscripts { get; init; } = Array.Empty<SidecarFile>();
    public long TranscriptBytes { get; init; }
    /// <summary>Transcript + everything in its sidecar folder.</summary>
    public long TotalBytes { get; init; }
    /// <summary>Newest change of the session: the transcript or any of its subagent transcripts (used for "is it live?").</summary>
    public DateTime LastWriteUtc { get; init; }
    /// <summary>
    /// The transcript file's own last-write time (null = same as <see cref="LastWriteUtc"/>). Plan items that copy or move
    /// the transcript expect THIS time: the session's newest time can come from a subagent file, and the engine would then
    /// always skip the transcript as "changed since the scan".
    /// </summary>
    public DateTime? TranscriptLastWriteUtc { get; init; }
    public DateTime CreatedUtc { get; init; }

    /// <summary>What a plan item for the transcript itself expects its last-write time to be.</summary>
    public DateTime TranscriptWriteUtc => TranscriptLastWriteUtc ?? LastWriteUtc;

    /// <summary>Short project name for lists ("my-app"): from cwd, the decoded folder, or the encoded folder without the user name.</summary>
    public string ProjectName => AgentProjects.ShortName(Cwd ?? DecodedProjectPath, ProjectFolderLabel ?? ProjectFolder);

    /// <summary>The project folder when it's known (cwd, or decoded from Claude Code's folder name).</summary>
    public string? ProjectPath => Cwd ?? DecodedProjectPath;

    public string LibraryId => ChatIds.For(Source, SessionId);
}

public sealed record SidecarFile(string Path, long Size, DateTime LastWriteUtc);

public static class AgentProjects
{
    public static string ShortName(string? cwd, string? encodedFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.TrimEnd('\\', '/');
            var name = trimmed[(Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/')) + 1)..];
            if (name.Length > 0) return name;
            return trimmed.Length > 0 ? trimmed : cwd;
        }
        if (!string.IsNullOrWhiteSpace(encodedFolder))
        {
            // "C--Users-me-source-my-app" → best effort "my-app" is impossible (dashes are ambiguous): show it as is.
            return encodedFolder;
        }
        return "No project";
    }

    /// <summary>
    /// Claude Code names each project folder after its path with every character that isn't a letter or digit turned
    /// into '-' ("C:\Users\me\source\my-app" → "C--Users-me-source-my-app"). The same encoding, to compare against it.
    /// </summary>
    public static string Encode(string path) => new(path.TrimEnd('\\', '/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>
    /// The real folder behind an encoded Claude Code project name, found by matching the encoded parts against folders
    /// that exist under the user folder (or a drive root). Read-only: only folder names are listed, links are never
    /// followed. Null when nothing matches (the folder moved, or it was on another PC).
    /// </summary>
    public static string? Decode(string? encoded, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        string? baseDir = null, rest = null;
        if (StripProfile(encoded, userProfile) is { } afterProfile) { baseDir = userProfile; rest = afterProfile; }
        else if (encoded.Length > 3 && char.IsAsciiLetter(encoded[0]) && encoded[1] == '-' && encoded[2] == '-')
        {
            baseDir = encoded[0] + @":\";
            rest = encoded[3..];
        }
        if (baseDir is null || string.IsNullOrEmpty(rest)) return null;
        var parts = Resolve(baseDir, rest.Split('-'), 0, depth: 0);
        return parts is null ? null : Path.Combine(new[] { baseDir }.Concat(parts).ToArray());
    }

    /// <summary>The encoded name after the user-profile part ("source-repos-my-app"), or null when it isn't in the user folder.</summary>
    public static string? StripProfile(string? encoded, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(encoded) || string.IsNullOrWhiteSpace(userProfile)) return null;
        // The profile as Claude Code wrote it, and the usual "C--Users-<name>" (a practice PC's profile lives elsewhere).
        var user = Encode(Path.GetFileName(userProfile.TrimEnd('\\', '/')));
        var usual = encoded.Length > 3 && encoded[1] == '-' && encoded[2] == '-' ? encoded[..3] + "Users-" + user : null;
        foreach (var prefix in new[] { Encode(userProfile), usual })
            if (prefix is not null && encoded.Length > prefix.Length + 1 && encoded.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                return encoded[(prefix.Length + 1)..];
        return null;
    }

    /// <summary>Where a project lives, without the user name: "source\repos", "your user folder", "D:\Projects".</summary>
    public static string? Hint(string? projectPath, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try
        {
            var parent = Path.GetDirectoryName(projectPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent)) return null;
            if (!string.IsNullOrWhiteSpace(userProfile) && PathUtil.IsUnder(parent, userProfile))
            {
                var relative = Path.GetRelativePath(userProfile, parent);
                return relative == "." ? "your user folder" : relative;
            }
            return parent;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static readonly EnumerationOptions FoldersOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>Folder names below <paramref name="dir"/> whose encoded names spell tokens[start..] (longest match first).</summary>
    private static IReadOnlyList<string>? Resolve(string dir, string[] tokens, int start, int depth)
    {
        if (start == tokens.Length) return Array.Empty<string>();
        if (depth > 16) return null;
        Dictionary<string, List<string>> byEncoded;
        try
        {
            byEncoded = Directory.EnumerateDirectories(dir, "*", FoldersOnly).Select(Path.GetFileName).OfType<string>()
                .GroupBy(Encode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        for (int end = tokens.Length; end > start; end--)
        {
            if (!byEncoded.TryGetValue(string.Join('-', tokens[start..end]), out var names)) continue;
            foreach (var name in names)
                if (Resolve(Path.Combine(dir, name), tokens, end, depth + 1) is { } restParts)
                    return new[] { name }.Concat(restParts).ToList();
        }
        return null;
    }

    /// <summary>Safe folder name for an archive copy ("my-app"), never empty, never "." or "..".</summary>
    public static string FolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        return clean is "" or "." or ".." ? "No project" : clean.Length > 80 ? clean[..80].TrimEnd('.', ' ') : clean;
    }

    public static bool SameCwd(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
