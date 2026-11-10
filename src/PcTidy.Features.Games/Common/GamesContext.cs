using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Clips;
using PcTidy.Features.Games.Environment;

namespace PcTidy.Features.Games.Common;

/// <summary>
/// Keys a caller (page, tests) may put in <see cref="ScanContext.Options"/> to tune the Games scanners.
/// Everything has a sensible default, so the shell never has to pass any of them.
/// </summary>
public static class GamesOptions
{
    /// <summary>DateTime (UTC) used as "now" for ages (tests pin it).</summary>
    public const string Now = "games.now";
    /// <summary><see cref="IGamesEnvironment"/>: registry/Store/NVIDIA reads (sandbox and tests use a fake).</summary>
    public const string Environment = "games.environment";
    /// <summary><see cref="IMediaProbe"/>: clip duration, resolution and thumbnail hashes.</summary>
    public const string Media = "games.media";
    /// <summary>int: months without playing before a game is suggested for uninstall (default 6).</summary>
    public const string UnplayedMonths = "games.unplayedMonths";
    /// <summary>int: days before a clip counts as old (default 90).</summary>
    public const string OldClipDays = "games.oldClipDays";
}

/// <summary>Per-scan settings resolved from the <see cref="ScanContext"/>.</summary>
internal sealed class GamesContext
{
    public const string FeatureId = "games";

    private GamesContext(ScanContext scan)
    {
        Scan = scan;
        NowUtc = scan.Options.TryGetValue(GamesOptions.Now, out var n) && n is DateTime dt ? dt.ToUniversalTime() : DateTime.UtcNow;
        Environment = scan.Options.TryGetValue(GamesOptions.Environment, out var e) && e is IGamesEnvironment env
            ? env
            : scan.Locations.IsSandbox ? new SandboxGamesEnvironment(scan.Locations) : new WindowsGamesEnvironment();
        Media = scan.Options.TryGetValue(GamesOptions.Media, out var m) && m is IMediaProbe probe
            ? probe
            : scan.Locations.IsSandbox ? NullMediaProbe.Instance : new ShellMediaProbe();
        UnplayedMonths = scan.Options.TryGetValue(GamesOptions.UnplayedMonths, out var u) && u is int months && months > 0 ? months : 6;
        OldClipDays = scan.Options.TryGetValue(GamesOptions.OldClipDays, out var o) && o is int days && days > 0 ? days : 90;
    }

    public static GamesContext From(ScanContext scan) => new(scan);

    public ScanContext Scan { get; }
    public IKnownLocations Locations => Scan.Locations;
    public ISafetyGuard Safety => Scan.Safety;
    public DateTime NowUtc { get; }
    public IGamesEnvironment Environment { get; }
    public IMediaProbe Media { get; }
    public int UnplayedMonths { get; }
    public int OldClipDays { get; }

    public bool IsRunning(string process) => Scan.RunningProcesses.Contains(process.ToLowerInvariant());
}
