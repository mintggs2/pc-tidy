using PcTidy.Features.Duplicates.Model;

namespace PcTidy.Features.Duplicates.Planning;

/// <summary>
/// Turns findings into PlanItems and ScanItems. Removal is always <see cref="ActionKind.QuarantinePath"/> with the
/// size and modified time seen at scan time. Duplicate copies also carry what the engine must check right before
/// it moves them: <c>Args["verifyHash"]</c> (XXH3-64 hex of the content, as <c>FileHash.Full</c> writes it) and
/// <c>Args["keepPath"]</c> (the copy that stays, which must still exist with that same hash).
/// </summary>
public static class DuplicatePlanner
{
    public const string ArgVerifyHash = "verifyHash";
    public const string ArgKeepPath = "keepPath";
    /// <summary>Folder groups: "folder" = compare every file of the two folders (names, sizes, hashes) before moving.</summary>
    public const string ArgVerifyMode = "verifyMode";
    public const string VerifyModeFile = "file";
    public const string VerifyModeFolder = "folder";

    public static PlanItem Quarantine(string path, bool isDirectory, long size, DateTime writeUtc, string description, Dictionary<string, string>? args = null)
        => new()
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = DuplicatesModule.FeatureId,
            Description = description,
            SourcePath = path,
            IsDirectory = isDirectory,
            ExpectedSize = size,
            ExpectedLastWriteUtc = writeUtc,
            Args = args,
        };

    /// <summary>
    /// The plan item that moves one non-kept copy to quarantine (verified against the kept copy first).
    /// Never for a probable group: its hash comes from samples, and a sampled match is never acted on.
    /// </summary>
    public static PlanItem ForCopy(DuplicateGroup group, DuplicateCopy copy, Func<string, string> friendly)
        => group.IsProbable
            ? throw new InvalidOperationException("Huge files that only matched by samples must be checked fully before anything is removed.")
            : Quarantine(copy.Path, group.IsFolderGroup, copy.Size, copy.LastWriteUtc,
            group.IsFolderGroup
                ? $"Move duplicate folder '{Name(copy.Path)}' to quarantine (the same folder stays in {friendly(group.KeptPath)})"
                : $"Move duplicate '{Name(copy.Path)}' to quarantine (a copy stays in {friendly(Folder(group.KeptPath))})",
            new Dictionary<string, string>
            {
                [ArgVerifyHash] = group.ContentHash,
                [ArgKeepPath] = group.KeptPath,
                [ArgVerifyMode] = group.IsFolderGroup ? VerifyModeFolder : VerifyModeFile,
            });

    public static PlanItem ForJunk(JunkItem j, Func<string, string> friendly) => Quarantine(j.Path, j.IsDirectory, j.Size, j.LastWriteUtc, j.Kind switch
    {
        JunkKind.EmptyFolder => $"Move empty folder '{Name(j.Path)}' in {friendly(Folder(j.Path))} to quarantine",
        JunkKind.ZeroByteFile => $"Move empty file '{Name(j.Path)}' in {friendly(Folder(j.Path))} to quarantine",
        _ => $"Move broken shortcut '{Name(j.Path)}' in {friendly(Folder(j.Path))} to quarantine",
    });

    public static PlanItem ForSimilar(SimilarImageGroup g, SimilarImage img, Func<string, string> friendly)
        => Quarantine(img.Path, false, img.Size, img.LastWriteUtc,
            $"Move look-alike picture '{Name(img.Path)}' to quarantine (keeping '{Name(g.SuggestedKeep)}' in {friendly(Folder(g.SuggestedKeep))})");

    /// <summary>Plan item for a "compare to master" match: the other copy goes, the master's copy stays.</summary>
    public static PlanItem ForMasterMatch(MasterMatch m, Func<string, string> friendly)
        => Quarantine(m.Copy.Path, false, m.Copy.Size, m.Copy.LastWriteUtc,
            $"Move '{Name(m.Copy.Path)}' to quarantine (the same file is in your master folder: {friendly(Folder(m.MasterPath))})",
            new Dictionary<string, string> { [ArgVerifyHash] = m.ContentHash, [ArgKeepPath] = m.MasterPath, [ArgVerifyMode] = VerifyModeFile });

    /// <summary>Gives a file the extension its content really has (same folder; never overwrites).</summary>
    public static PlanItem ForRename(string path, string newPath, long size, DateTime writeUtc)
        => new()
        {
            Kind = ActionKind.RenamePath,
            FeatureId = DuplicatesModule.FeatureId,
            Description = $"Rename '{Name(path)}' to '{Name(newPath)}' (its real file type)",
            SourcePath = path,
            DestinationPath = newPath,
            ExpectedSize = size,
            ExpectedLastWriteUtc = writeUtc,
            ClashPolicy = NameClashPolicy.Skip,
        };

    public static string Name(string path) => Path.GetFileName(path.TrimEnd('\\'));
    public static string Folder(string path) => Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;

    // ------------------------------------------------------------------ scan items (standard flow / Optimize-all)

    public static ScanItem CopyItem(DuplicateGroup g, DuplicateCopy c, Func<string, string> friendly)
    {
        bool ticked = g.AutoTicked.Contains(c.Path);
        var kept = g.Kept;
        var details = new Dictionary<string, string>
        {
            ["Kept copy"] = kept.Path,
            ["Why this copy is kept"] = g.KeepReason,
            ["Copies"] = g.Copies.Count.ToString("N0"),
            ["Checked"] = g.IsFolderGroup ? "Every file's name, size and full content match" : "Full content match (byte for byte, XXH3)",
        };
        if (c.IsHardLink) details["Hard link"] = c.HardLinkOf is { Length: > 0 } other ? $"Same data as {other}" : "Shares its data with a file elsewhere";
        var what = g.IsFolderGroup ? $"Same {g.FileCount:N0} file{(g.FileCount == 1 ? "" : "s")} as the folder kept in {friendly(kept.Path)}"
                                   : $"Same content as the copy kept in {friendly(Folder(kept.Path))}";
        return new ScanItem
        {
            Id = $"{g.Id}|{c.Path}",
            Title = Name(c.Path),
            Detail = c.Path,
            Reason = $"{what}. Kept because: {LowerFirst(g.KeepReason)}",
            Badge = ticked ? Badge.Safe : Badge.Check,
            DefaultSelected = ticked,
            SizeBytes = c.ReclaimableBytes,
            SizeOnDiskBytes = c.ReclaimableBytes,
            FileCount = g.IsFolderGroup ? g.FileCount : 1,
            LastModifiedUtc = c.LastWriteUtc,
            CurrentState = c.IsHardLink ? "Hard link" : null,
            Details = details,
            Actions = new[] { ForCopy(g, c, friendly) },
            DisabledReason = DisabledReasonFor(c),
        };
    }

    /// <summary>Why a copy can't be ticked, in plain English (null = it can).</summary>
    public static string? DisabledReasonFor(DuplicateCopy c)
    {
        if (c.IsHardLink)
            return "This is a hard link: it shares its data with another file, so removing it frees no space.";
        if (c.InReferenceFolder) return "It's in a reference folder you marked, so it's never removed.";
        return c.RemovalBlockedReason;
    }

    public static ScanItem JunkScanItem(JunkItem j, Func<string, string> friendly) => new()
    {
        Id = $"junk|{j.Kind}|{j.Path}",
        Title = Name(j.Path),
        Detail = j.Path,
        Reason = j.Reason,
        Badge = Badge.Check,
        SizeBytes = j.Size,
        SizeOnDiskBytes = j.Size,
        FileCount = j.IsDirectory ? 0 : 1,
        LastModifiedUtc = j.LastWriteUtc,
        Details = j.Target is null ? null : new Dictionary<string, string> { ["Points to"] = j.Target },
        Actions = new[] { ForJunk(j, friendly) },
        DisabledReason = j.RemovalBlockedReason,
    };

    public static ScanItem SimilarScanItem(SimilarImageGroup g, SimilarImage img, Func<string, string> friendly) => new()
    {
        Id = $"{g.Id}|{img.Path}",
        Title = Name(img.Path),
        Detail = img.Path,
        Reason = $"Looks {img.SimilarityPercent}% like '{Name(g.SuggestedKeep)}' in {friendly(Folder(g.SuggestedKeep))}, which is kept ({LowerFirst(g.KeepReason).TrimEnd('.')}). Compare them before removing.",
        Badge = Badge.Check,
        SizeBytes = img.Size,
        SizeOnDiskBytes = img.Size,
        FileCount = 1,
        LastModifiedUtc = img.LastWriteUtc,
        PreviewPath = img.Path,
        Details = new Dictionary<string, string>
        {
            ["Kept picture"] = g.SuggestedKeep,
            ["Size"] = $"{img.Width} × {img.Height}",
        },
        Actions = new[] { ForSimilar(g, img, friendly) },
        DisabledReason = img.InReferenceFolder ? "It's in a reference folder you marked, so it's never removed." : img.RemovalBlockedReason,
    };

    /// <summary>
    /// A huge copy that only matched by samples: shown so you know, never tickable here and with no action. The page's
    /// "Check fully" turns it into a normal, verified duplicate.
    /// </summary>
    public static ScanItem ProbableItem(DuplicateGroup g, DuplicateCopy c, Func<string, string> friendly) => new()
    {
        Id = $"{g.Id}|{c.Path}",
        Title = Name(c.Path),
        Detail = c.Path,
        Reason = $"Probably the same as the copy in {friendly(Folder(g.KeptPath))}: same size, and the start, middle and end match. Not fully checked, so nothing is removed yet.",
        Badge = Badge.Check,
        DefaultSelected = false,
        SizeBytes = c.ReclaimableBytes,
        SizeOnDiskBytes = c.ReclaimableBytes,
        FileCount = 1,
        LastModifiedUtc = c.LastWriteUtc,
        CurrentState = "Probably the same",
        Details = new Dictionary<string, string>
        {
            ["Compared to"] = g.KeptPath,
            ["Checked"] = "Samples only (start, middle and end, 1 MB each)",
        },
        DisabledReason = DisabledReasonFor(c) ?? "Probably the same, but only samples were compared. Open Duplicates and choose Check fully before removing it.",
    };

    /// <summary>A file whose content doesn't match its extension: a rename suggestion, or "shown so you know".</summary>
    public static ScanItem ExtensionItem(WrongExtensionItem w, Func<string, string> friendly) => new()
    {
        Id = $"extension|{w.Path}",
        Title = Name(w.Path),
        Detail = w.Path,
        Reason = w.Reason,
        Badge = Badge.Check,
        DefaultSelected = false,
        SizeBytes = 0,
        FileCount = 1,
        LastModifiedUtc = w.LastWriteUtc,
        CurrentState = w.IsSecurityWarning ? "Security warning" : null,
        Details = new Dictionary<string, string>
        {
            ["Named as"] = "." + w.Extension,
            ["Really is"] = w.RealType,
            ["Where"] = friendly(Folder(w.Path)),
        },
        Actions = w.SuggestedPath is { } to && w.RenameBlockedReason is null && !w.IsSecurityWarning
            ? new[] { ForRename(w.Path, to, w.Size, w.LastWriteUtc) }
            : Array.Empty<PlanItem>(),
        DisabledReason = w.IsSecurityWarning
            ? "Shown so you know. PC Tidy never renames a program into something that runs."
            : w.SuggestedPath is null ? "Shown so you know. PC Tidy can't tell which name is right."
            : w.RenameBlockedReason,
    };

    public static string LowerFirst(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
