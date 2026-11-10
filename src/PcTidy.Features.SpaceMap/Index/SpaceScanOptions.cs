namespace PcTidy.Features.SpaceMap.Index;

public sealed record SpaceScanOptions
{
    /// <summary>Worker threads walking separate branches in parallel. Default: cores, capped at 8 (disks saturate beyond that).</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);
    /// <summary>Folder levels listed up front before handing whole branches to workers (2 = e.g. C:\Users\name).</summary>
    public int SplitDepth { get; init; } = 2;
    /// <summary>Don't walk inside protected folders (their size then counts as "Unscanned"). Used by the Optimize-all scanner.</summary>
    public bool SkipProtectedContents { get; init; }
    /// <summary>Don't walk inside OneDrive / cloud roots.</summary>
    public bool SkipCloudRoots { get; init; }
    /// <summary>Add the "Unscanned / system" block when the root is a whole drive.</summary>
    public bool IncludeUnscannedNode { get; init; } = true;
    /// <summary>Folders the user excluded from scans (shown, not walked).</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
    /// <summary>Game install folders: their files are coloured as Games.</summary>
    public IReadOnlyList<string> GameRoots { get; init; } = Array.Empty<string>();
    /// <summary>Cluster size override (tests); otherwise read from the volume.</summary>
    public int? ClusterSize { get; init; }
}

public sealed record SpaceScanProgress(long Files, long Folders, long Bytes, string? CurrentFolder);
