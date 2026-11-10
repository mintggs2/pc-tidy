using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.Common;

/// <summary>Ids and names shared by every part of the section.</summary>
public static class DriveMoveIds
{
    public const string FeatureId = "drivemove";
    public const string Title = "Move to drive";

    /// <summary>Folder PC Tidy creates on the target drive for folders it moves with a link.</summary>
    public const string MovedFolderName = "Moved by PC Tidy";

    public const string GroupDrives = "drivemove-drives";
    public const string GroupSuggestions = "drivemove-suggestions";
    public const string GroupBiggest = "drivemove-biggest";
    public const string GroupFolders = "drivemove-folders";
    public const string GroupFoldersRefused = "drivemove-folders-refused";
    public const string GroupUserFolders = "drivemove-user-folders";
    public const string GroupGameLaunchers = "drivemove-game-launchers";
    /// <summary>Prefix: one group per drive ("drivemove-games-c").</summary>
    public const string GroupGamesOnDrive = "drivemove-games-";
    public const string GroupCachesConfig = "drivemove-caches-config";
    public const string GroupCachesSteps = "drivemove-caches-steps";
}

/// <summary>Keys of <see cref="ScanContext.Options"/> this section reads (tests use them to pin time and the PC's state).</summary>
public static class DriveMoveOptions
{
    /// <summary><see cref="DateTime"/> (UTC) used as "now" for "not played in 6 months".</summary>
    public const string Now = "drivemove.now";
    /// <summary>An <see cref="IDriveMoveEnvironment"/> (environment variables, OneDrive and WSL registry facts).</summary>
    public const string Environment = "drivemove.environment";
    /// <summary>A <see cref="DriveMoveThresholds"/>.</summary>
    public const string Thresholds = "drivemove.thresholds";
}

/// <summary>Sizes and ages the suggestions use. Defaults suit a real PC; tests shrink them for small fake files.</summary>
public sealed record DriveMoveThresholds
{
    /// <summary>A drive with less than this share free is "full" (15%).</summary>
    public double FullFreeFraction { get; init; } = 0.15;
    /// <summary>A drive with less than this free is "full" whatever its size (20 GB).</summary>
    public long FullFreeBytes { get; init; } = 20L << 30;
    /// <summary>Folders smaller than this aren't listed unless the user picked them (1 GB).</summary>
    public long MinFolderBytes { get; init; } = 1L << 30;
    /// <summary>Things smaller than this aren't suggested (1 GB).</summary>
    public long MinSuggestionBytes { get; init; } = 1L << 30;
    /// <summary>Caches smaller than this aren't listed (100 MB).</summary>
    public long MinCacheBytes { get; init; } = 100L << 20;
    /// <summary>Free space the target needs on top of the size: 10% (Research.md §7 step 1).</summary>
    public double Headroom { get; init; } = 1.10;
    /// <summary>A game not played for this long is "not played in 6 months".</summary>
    public int StaleGameDays { get; init; } = 182;
    public int MaxSuggestions { get; init; } = 6;
    public int MaxBiggest { get; init; } = 10;
    /// <summary>Files of one folder asked about with Restart Manager at scan time (the engine checks the whole folder again before moving).</summary>
    public int MaxInUseFiles { get; init; } = 2_000;

    public static DriveMoveThresholds Default { get; } = new();

    /// <summary>Bytes a target drive needs free for a move of <paramref name="bytes"/>.</summary>
    public long Needed(long bytes) => (long)Math.Ceiling(bytes * Headroom);
}

/// <summary>Everything one scan of this section works from. Built once per scan; read-only.</summary>
internal sealed class DriveMoveContext
{
    public required ScanContext Scan { get; init; }
    public required DriveList Drives { get; init; }
    public required IDriveMoveEnvironment Pc { get; init; }
    public required DriveMoveThresholds Thresholds { get; init; }
    public required DateTime NowUtc { get; init; }

    public IKnownLocations Locations => Scan.Locations;
    public ISafetyGuard Safety => Scan.Safety;
    public IFileWalker Walker => Scan.Walker;

    /// <summary>The account's folder name ("tester", "alex"): the last part of the profile path, never a hard-coded name.</summary>
    public string ProfileFolderName => Path.GetFileName(PathUtil.Normalize(Locations.UserProfile));

    public static DriveMoveContext From(ScanContext ctx) => new()
    {
        Scan = ctx,
        Drives = DriveList.From(ctx.Locations, ctx.System),
        Pc = ctx.Options.TryGetValue(DriveMoveOptions.Environment, out var e) && e is IDriveMoveEnvironment env
            ? env
            : ctx.Locations.IsSandbox ? EmptyDriveMoveEnvironment.Instance : new WindowsDriveMoveEnvironment(),
        Thresholds = ctx.Options.TryGetValue(DriveMoveOptions.Thresholds, out var t) && t is DriveMoveThresholds th ? th : DriveMoveThresholds.Default,
        NowUtc = ctx.Options.TryGetValue(DriveMoveOptions.Now, out var n) && n is DateTime now ? now : DateTime.UtcNow,
    };
}
