using System.Globalization;
using PcTidy.Features.Duplicates.Model;

namespace PcTidy.Features.Duplicates.Keep;

/// <summary>
/// Which copy to keep (Plan §8.3, exactly):
/// 1. a copy in a protected or reference folder is never removed (so it is the one kept);
/// 2. prefer library folders (Desktop, Documents, Pictures, Music, Videos) over Downloads, temp and backup-looking folders;
/// 3. prefer the clean name over "- Copy", "(1)", "Copy of";
/// 4. then the oldest modified date; 5. then the shortest path; 6. always keep one.
/// Only clear cases are ticked: a Downloads/temp/backup copy of a file kept in a library folder. A tie at step 2
/// (the kept copy and the other copy are in the same kind of place) ticks nothing: the user decides.
/// </summary>
public static class KeepRules
{
    /// <summary>Copies in the order the rules prefer them (first = kept).</summary>
    public static IReadOnlyList<DuplicateCopy> Rank(IEnumerable<DuplicateCopy> copies)
        => copies
            .OrderByDescending(IsProtected)
            .ThenBy(c => c.Location)
            .ThenBy(c => c.HasCopyName)
            .ThenBy(c => c.LastWriteUtc)
            .ThenBy(c => c.Path.Length)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Step 1: a reference folder, or a place the safety guard won't let PC Tidy remove from.</summary>
    public static bool IsProtected(DuplicateCopy c) => c.InReferenceFolder || c.RemovalBlockedReason is not null;

    /// <summary>Builds a group with the rules' choice of kept copy, the reason, and the clear cases ticked.</summary>
    public static DuplicateGroup Decide(string id, string hash, long size, IReadOnlyList<DuplicateCopy> copies, bool isFolderGroup = false, int fileCount = 1)
    {
        var ranked = Rank(copies);
        var kept = ranked[0];
        var reason = Explain(kept, ranked.Count > 1 ? ranked[1] : null, isFolderGroup);
        return new DuplicateGroup
        {
            Id = id, ContentHash = hash, Size = size, IsFolderGroup = isFolderGroup, FileCount = fileCount,
            Copies = ranked, KeptPath = kept.Path, KeepReason = reason,
            AutoTicked = isFolderGroup ? new HashSet<string>() : ClearCases(kept, ranked),
        };
    }

    /// <summary>The user picked another copy to keep. Nothing is ticked for them except the same clear cases.</summary>
    public static DuplicateGroup WithKeeper(DuplicateGroup group, string keepPath)
    {
        var kept = group.Copies.FirstOrDefault(c => DuplicateGroup.PathEquals(c.Path, keepPath))
                   ?? throw new ArgumentException("That file isn't part of this group.", nameof(keepPath));
        if (kept.IsHardLink && kept.HardLinkOf is { Length: > 0 }) kept = group.Copies.First(c => DuplicateGroup.PathEquals(c.Path, kept.HardLinkOf));
        var others = Rank(group.Copies.Where(c => !ReferenceEquals(c, kept)));
        var ordered = new[] { kept }.Concat(others).ToList();
        return group with
        {
            Copies = ordered,
            KeptPath = kept.Path,
            KeptByUser = true,
            KeepReason = "You chose to keep this copy.",
            AutoTicked = group.IsFolderGroup ? new HashSet<string>() : ClearCases(kept, ordered),
        };
    }

    private static HashSet<string> ClearCases(DuplicateCopy kept, IReadOnlyList<DuplicateCopy> ranked)
    {
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kept.Location != LocationClass.Library) return ticked;
        foreach (var c in ranked.Skip(1))
            if (c.CanBeRemoved && c.Location == LocationClass.DownloadsTempOrBackup) ticked.Add(c.Path);
        return ticked;
    }

    /// <summary>"Why this copy is kept": the first rule that set it apart from the next-best copy.</summary>
    public static string Explain(DuplicateCopy kept, DuplicateCopy? next, bool folder = false)
    {
        var thing = folder ? "folder" : "copy";
        if (next is null) return $"It's the only {thing} left.";
        if (kept.InReferenceFolder && !next.InReferenceFolder)
            return $"It's in a reference folder you marked, so this {thing} is never removed.";
        if (kept.Location < next.Location)
            return kept.Location == LocationClass.Library
                ? $"It's in {kept.LocationLabel}. Copies in Downloads, temp or backup folders go first."
                : $"It's in {kept.LocationLabel}, a better home than {next.LocationLabel}.";
        if (IsProtected(kept) && !IsProtected(next))
            return $"It's in {kept.LocationLabel}, which PC Tidy doesn't change without your OK, so this {thing} stays.";
        if (!kept.HasCopyName && next.HasCopyName)
            return "It has the original name. The others are named like copies (\"- Copy\", \"(1)\").";
        if (kept.LastWriteUtc < next.LastWriteUtc)
            return $"It's the oldest {thing} (last changed {kept.LastWriteUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}).";
        if (kept.Path.Length < next.Path.Length)
            return "It has the shortest path.";
        return $"All {thing}s look equally good, so PC Tidy keeps this one. One {thing} is always kept.";
    }
}
