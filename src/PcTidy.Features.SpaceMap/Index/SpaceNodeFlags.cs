namespace PcTidy.Features.SpaceMap.Index;

/// <summary>Per-node bit flags, stored as one ushort per node.</summary>
[Flags]
public enum SpaceNodeFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    /// <summary>Junction / symbolic link (name-surrogate reparse point). Shown as a 0-byte node, never followed.</summary>
    Link = 1 << 1,
    /// <summary>OneDrive (or other cloud) online-only file: full logical size, ~0 bytes on disk.</summary>
    CloudPlaceholder = 1 << 2,
    /// <summary>Protected by the SafetyGuard (PathProtection.Never), or inside such a folder. Shown with a lock; no actions.</summary>
    Protected = 1 << 3,
    /// <summary>PathProtection.AskFirst (OneDrive, code projects...). Actions allowed only after the user grants the folder.</summary>
    AskFirst = 1 << 4,
    Hidden = 1 << 5,
    System = 1 << 6,
    /// <summary>Synthetic node (the "Unscanned / system" block). Has no real path.</summary>
    Pseudo = 1 << 7,
    /// <summary>Folder that was not walked (scan exclusion, or skipped protected contents in a quick scan).</summary>
    NotWalked = 1 << 8,
    /// <summary>Compressed or sparse file: size on disk is an upper-bound estimate.</summary>
    EstimatedOnDisk = 1 << 9,
    /// <summary>Inside a detected game library (coloured as Games).</summary>
    InGameLibrary = 1 << 10,
    /// <summary>Full path is longer than 260 characters (some apps and old tools can't open it).</summary>
    LongPath = 1 << 11,
    /// <summary>
    /// The folder itself stays put (a drive root, a main user folder, or it holds protected folders), but what's inside
    /// follows the normal rules. Not inherited, and not shown with a lock.
    /// </summary>
    SelfLocked = 1 << 12,
    /// <summary>Some folder somewhere inside is <see cref="Protected"/> (set when the index is assembled).</summary>
    ContainsProtected = 1 << 13,
    /// <summary>
    /// Scan-time only: protected, but its direct sub-folders are still asked about (Users blocks other accounts,
    /// not yours). Cleared when the index is assembled.
    /// </summary>
    CheckInside = 1 << 14,
}
