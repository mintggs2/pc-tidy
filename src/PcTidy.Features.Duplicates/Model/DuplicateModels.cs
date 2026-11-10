namespace PcTidy.Features.Duplicates.Model;

/// <summary>Where a copy lives, for keep rule 2 ("prefer library folders over Downloads, Temp or backup-looking folders").</summary>
public enum LocationClass
{
    /// <summary>Desktop, Documents, Pictures, Music, Videos (also a cloud folder's own copies of them).</summary>
    Library = 0,
    /// <summary>Anywhere else (a folder on another drive, the profile...).</summary>
    Other = 1,
    /// <summary>Downloads, temp folders, or a folder that looks like a backup / old copy.</summary>
    DownloadsTempOrBackup = 2,
}

/// <summary>One copy inside a duplicate group (a file, or a folder for duplicate folders).</summary>
public sealed record DuplicateCopy
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public LocationClass Location { get; init; }
    /// <summary>"your Pictures folder", "Downloads", "D:\Photos\Holiday 2025"... for reasons shown to the user.</summary>
    public string LocationLabel { get; init; } = "";
    /// <summary>Inside a reference folder the user marked: never removed.</summary>
    public bool InReferenceFolder { get; init; }
    /// <summary>Named like a copy ("- Copy", "(1)", "Copy of").</summary>
    public bool HasCopyName { get; init; }
    /// <summary>Why PC Tidy may not remove this copy (safety guard), or null when it may.</summary>
    public string? RemovalBlockedReason { get; init; }
    /// <summary>True when the guard's block is permanent ("never touched"), false when the user can allow it in Settings.</summary>
    public bool BlockedForGood { get; init; }
    /// <summary>
    /// Set when this path shares its data with another file (a hard link): removing it frees nothing.
    /// Holds the other path in the group, or "" when the other link is outside the scanned folders.
    /// </summary>
    public string? HardLinkOf { get; init; }
    public bool IsHardLink => HardLinkOf is not null;
    /// <summary>Kept copies, hard links and blocked copies can't be ticked.</summary>
    public bool CanBeRemoved => RemovalBlockedReason is null && !InReferenceFolder && !IsHardLink;
    /// <summary>Space freed by removing this copy (0 for hard links).</summary>
    public long ReclaimableBytes => IsHardLink ? 0 : Size;
}

/// <summary>Files (or folders) with exactly the same content, plus which copy is kept and why.</summary>
public sealed record DuplicateGroup
{
    public required string Id { get; init; }
    /// <summary>XXH3-64 of the content (folders: signature of every file's name, size and hash).</summary>
    public required string ContentHash { get; init; }
    public required long Size { get; init; }
    public bool IsFolderGroup { get; init; }
    public required IReadOnlyList<DuplicateCopy> Copies { get; init; }
    public required string KeptPath { get; init; }
    /// <summary>Plain-English "why this copy is kept".</summary>
    public required string KeepReason { get; init; }
    /// <summary>Paths ticked by default (only the clear cases).</summary>
    public required IReadOnlySet<string> AutoTicked { get; init; }
    /// <summary>The user picked the kept copy by hand.</summary>
    public bool KeptByUser { get; init; }
    /// <summary>File count per copy (folder groups).</summary>
    public int FileCount { get; init; } = 1;
    /// <summary>
    /// Huge files that only matched by samples (start, middle, end): "Probably the same (not fully checked)".
    /// <see cref="ContentHash"/> is then the SAMPLED hash and must never be used to verify a removal; such a group
    /// gives no plan items until a full hash (Check fully) turns it into a normal group.
    /// </summary>
    public bool IsProbable { get; init; }
    /// <summary>Was a probable group; the full hash (Check fully) proved every copy identical.</summary>
    public bool CheckedFully { get; init; }

    public DuplicateCopy Kept => Copies.First(c => PathEquals(c.Path, KeptPath));
    public IEnumerable<DuplicateCopy> Others => Copies.Where(c => !PathEquals(c.Path, KeptPath));
    /// <summary>Space freed if every removable non-kept copy goes.</summary>
    public long ReclaimableBytes => Others.Where(c => c.CanBeRemoved).Sum(c => c.ReclaimableBytes);

    internal static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pictures that look alike (perceptual hash), never byte-identical to each other. Never ticked by default.</summary>
public sealed record SimilarImageGroup
{
    public required string Id { get; init; }
    public required IReadOnlyList<SimilarImage> Images { get; init; }
    /// <summary>The best-looking copy (most pixels, then biggest file): suggested to keep.</summary>
    public required string SuggestedKeep { get; init; }
    public required string KeepReason { get; init; }
}

public sealed record SimilarImage
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>0..100, how alike it is to the suggested keep (100 = looks identical).</summary>
    public int SimilarityPercent { get; init; }
    public string? RemovalBlockedReason { get; init; }
    public bool InReferenceFolder { get; init; }
    public bool CanBeRemoved => RemovalBlockedReason is null && !InReferenceFolder;
}

public enum JunkKind { EmptyFolder, ZeroByteFile, BrokenShortcut }

/// <summary>An empty folder, zero-byte file or broken shortcut.</summary>
public sealed record JunkItem
{
    public required JunkKind Kind { get; init; }
    public required string Path { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public long Size { get; init; }
    /// <summary>Plain-English line shown with the item.</summary>
    public required string Reason { get; init; }
    /// <summary>Broken shortcuts: where it points.</summary>
    public string? Target { get; init; }
    /// <summary>Empty folders: how many empty folders are inside (removed together).</summary>
    public int NestedEmptyFolders { get; init; }
    public string? RemovalBlockedReason { get; init; }
    public bool IsDirectory => Kind == JunkKind.EmptyFolder;
}

/// <summary>Counts for the "what the scan did" line and the vault performance numbers.</summary>
public sealed record DuplicateScanStats
{
    public long FilesSeen { get; init; }
    public long FoldersSeen { get; init; }
    public long BytesSeen { get; init; }
    public long SizeCandidates { get; init; }
    public long PartialHashed { get; init; }
    public long FullHashed { get; init; }
    public long CacheHits { get; init; }
    public long ImagesHashed { get; init; }
    public long PlaceholdersSkipped { get; init; }
    public long LinksSkipped { get; init; }
    public long ProtectedFoldersSkipped { get; init; }
    public long ProjectFoldersSkipped { get; init; }
    public long Unreadable { get; init; }
    public long HardLinks { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Everything one Duplicates-and-junk scan found. Read-only data: nothing has been changed.</summary>
public sealed record DuplicatesReport
{
    public required ScanDepth Depth { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyList<DuplicateGroup> ExactGroups { get; init; }
    public required IReadOnlyList<DuplicateGroup> FolderGroups { get; init; }
    public required IReadOnlyList<SimilarImageGroup> SimilarImages { get; init; }
    public required IReadOnlyList<JunkItem> EmptyFolders { get; init; }
    public required IReadOnlyList<JunkItem> ZeroByteFiles { get; init; }
    public required IReadOnlyList<JunkItem> BrokenShortcuts { get; init; }
    /// <summary>Huge files that are probably the same (sampled only). Never ticked; never removed before a full check.</summary>
    public IReadOnlyList<DuplicateGroup> ProbableGroups { get; init; } = Array.Empty<DuplicateGroup>();
    /// <summary>Files whose content isn't what their extension says.</summary>
    public IReadOnlyList<WrongExtensionItem> WrongExtensions { get; init; } = Array.Empty<WrongExtensionItem>();
    public required IReadOnlyList<string> Notes { get; init; }
    public required DuplicateScanStats Stats { get; init; }
    /// <summary>"Pictures\Holiday 2025"-style spelling of a path for plain-English text.</summary>
    public required Func<string, string> FriendlyPath { get; init; }

    /// <summary>Space freed if every removable non-kept duplicate (file or folder) went.</summary>
    public long ReclaimableBytes => ExactGroups.Sum(g => g.ReclaimableBytes) + FolderGroups.Sum(g => g.ReclaimableBytes);
}

/// <summary>Result of "compare to master": files in the other folder whose content already exists in the master folder.</summary>
public sealed record MasterComparison
{
    public required string MasterFolder { get; init; }
    public required string OtherFolder { get; init; }
    public required IReadOnlyList<MasterMatch> Matches { get; init; }
    /// <summary>Files in the other folder with no copy in the master.</summary>
    public required int OnlyInOther { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public long ReclaimableBytes => Matches.Where(m => m.Copy.CanBeRemoved).Sum(m => m.Copy.ReclaimableBytes);
}

public sealed record MasterMatch(DuplicateCopy Copy, string MasterPath, string ContentHash);

/// <summary>A file whose first bytes say it's something else than its extension ("This .pdf is really a ZIP archive").</summary>
public sealed record WrongExtensionItem
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    /// <summary>Lower-case extension without the dot ("pdf").</summary>
    public required string Extension { get; init; }
    /// <summary>What the content really is, in words ("a ZIP archive").</summary>
    public required string RealType { get; init; }
    /// <summary>A program (or other runnable code) named like a document or picture: a security warning, never renamed.</summary>
    public bool IsSecurityWarning { get; init; }
    /// <summary>The fixed name ("report.zip") when renaming is harmless; null = shown so you know only.</summary>
    public string? SuggestedPath { get; init; }
    public required string Reason { get; init; }
    /// <summary>Why the rename can't be offered (safety guard), or null.</summary>
    public string? RenameBlockedReason { get; init; }
}
