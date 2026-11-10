using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Environment;

namespace PcTidy.Features.Developer.Common;

/// <summary>
/// Keys a caller (page, tests) may put in <see cref="ScanContext.Options"/> to tune the Developer scanners.
/// Everything has a sensible default (from <see cref="DeveloperSettings"/>), so the shell never has to pass any.
/// </summary>
public static class DeveloperOptions
{
    /// <summary><see cref="TimeProvider"/> used as the clock for every age rule (tests pin it).</summary>
    public const string Clock = "developer.clock";
    /// <summary><see cref="IDevEnvironment"/>: PATH, variables, WSL and Docker reads (sandbox and tests use a fake).</summary>
    public const string Environment = "developer.environment";
    /// <summary>int: days without activity before a project's build folders are offered (default 90).</summary>
    public const string IdleDays = "developer.idleDays";
    /// <summary>int: months without activity before a project is suggested for archiving (default 12).</summary>
    public const string ArchiveMonths = "developer.archiveMonths";
    /// <summary>string: folder the archive zips go to.</summary>
    public const string ArchiveFolder = "developer.archiveFolder";
}

/// <summary>Per-scan settings resolved from the <see cref="ScanContext"/> and the saved <see cref="DeveloperSettings"/>.</summary>
internal sealed class DevContext
{
    public const string FeatureId = "developer";
    public const string ArchiveFolderName = "Archived projects";

    private DevContext(ScanContext scan)
    {
        Scan = scan;
        var saved = DeveloperSettings.From(scan.Settings);
        var clock = scan.Options.TryGetValue(DeveloperOptions.Clock, out var c) && c is TimeProvider tp ? tp : TimeProvider.System;
        NowUtc = clock.GetUtcNow().UtcDateTime;
        Environment = scan.Options.TryGetValue(DeveloperOptions.Environment, out var e) && e is IDevEnvironment env
            ? env
            : scan.Locations.IsSandbox ? new SandboxDevEnvironment(scan.Locations) : new WindowsDevEnvironment();
        IdleDays = Positive(scan, DeveloperOptions.IdleDays) ?? (saved.IdleDays > 0 ? saved.IdleDays : 90);
        ArchiveMonths = Positive(scan, DeveloperOptions.ArchiveMonths) ?? (saved.ArchiveMonths > 0 ? saved.ArchiveMonths : 12);
        ArchiveFolder = scan.Options.TryGetValue(DeveloperOptions.ArchiveFolder, out var a) && a is string s && Path.IsPathFullyQualified(s)
            ? s
            : saved.ArchiveFolder is { } f && Path.IsPathFullyQualified(f) ? f : DefaultArchiveFolder(scan.Locations);
        Roots = scan.Roots is { Count: > 0 } r ? r : saved.Roots.Count > 0 ? saved.Roots : DefaultRoots(scan.Locations);
    }

    private static int? Positive(ScanContext scan, string key)
        => scan.Options.TryGetValue(key, out var v) && v is int n && n > 0 ? n : null;

    public static DevContext From(ScanContext scan) => new(scan);

    public ScanContext Scan { get; }
    public IKnownLocations Locations => Scan.Locations;
    public ISafetyGuard Safety => Scan.Safety;
    public IFileWalker Walker => Scan.Walker;
    public DateTime NowUtc { get; }
    public IDevEnvironment Environment { get; }
    public int IdleDays { get; }
    public int ArchiveMonths { get; }
    public string ArchiveFolder { get; }
    public IReadOnlyList<string> Roots { get; }

    public bool IsRunning(string process) => Scan.RunningProcesses.Contains(process.ToLowerInvariant());

    /// <summary>
    /// Your user folder, plus each other fixed drive (its top level and below). Windows, Program Files, game libraries
    /// and other protected places are skipped while walking.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(IKnownLocations k)
    {
        var roots = new List<string> { k.UserProfile };
        roots.AddRange(k.FixedDrives.Where(d => !PathUtil.Normalize(d).Equals(PathUtil.Normalize(k.SystemDrive), StringComparison.OrdinalIgnoreCase)));
        return roots;
    }

    /// <summary>"Archived projects" on the first non-system drive, or in your user folder when there's only one drive.</summary>
    public static string DefaultArchiveFolder(IKnownLocations k)
    {
        var other = k.FixedDrives.FirstOrDefault(d => !PathUtil.Normalize(d).Equals(PathUtil.Normalize(k.SystemDrive), StringComparison.OrdinalIgnoreCase));
        return Path.Combine(other ?? k.UserProfile, ArchiveFolderName);
    }
}
