using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Caches;
using PcTidy.Features.Developer.Common;
using PcTidy.Features.Developer.Environment;
using PcTidy.Features.Developer.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Developer;

/// <summary>
/// "Developer" in the left rail (Clean up group, v2; Plan §8 "15 · Developer cleanup" and "Archive old projects"):
/// build folders of idle projects, package caches via each tool's own command, Docker and WSL disks, and archiving old
/// projects to zips. One tabbed page; every change goes through the shell's confirm (IFlowHost).
/// </summary>
public sealed class DeveloperModule : IFeatureModule, IModuleRiskInfo
{
    private readonly IDevEnvironment? _environment;
    private readonly TimeProvider? _clock;

    /// <param name="environment">PATH/WSL/Docker reads override (tests, demos). Default: real PC, or the sandbox's Sim files.</param>
    /// <param name="clock">Clock override for tests.</param>
    public DeveloperModule(IDevEnvironment? environment = null, TimeProvider? clock = null)
    {
        _environment = environment;
        _clock = clock;
        Scanners = new IFeatureScanner[] { new DeveloperScanner() };
    }

    public string Id => DevContext.FeatureId;
    public string Title => "Developer";
    public string Summary => "Old build folders of projects you're not working on, package caches, Docker and WSL disks, and zipping old projects away.";
    public NavGroup Group => NavGroup.CleanUp;
    public string Icon => "Code24";
    /// <summary>Clean up group: Clean · Space map · Duplicates · Developer.</summary>
    public int Order => 40;
    public Release Release => Release.V2;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public object? CreatePage(IServiceProvider services) => new DeveloperPage(services, ScanOptions());

    /// <summary>Extra scan options the page passes to every scan (only the overrides given to this module).</summary>
    internal IReadOnlyDictionary<string, object> ScanOptions()
    {
        var o = new Dictionary<string, object>();
        if (_environment is not null) o[DeveloperOptions.Environment] = _environment;
        if (_clock is not null) o[DeveloperOptions.Clock] = _clock;
        return o;
    }
}

/// <summary>Is there anything for the Package caches and Docker &amp; WSL tabs on this PC? (Read-only checks.)</summary>
public static class DeveloperAvailability
{
    /// <summary>Any package manager or any of their caches exists.</summary>
    public static bool HasPackageTools(ScanContext ctx)
    {
        var dev = DevContext.From(ctx);
        return DevCacheTools.All.Any(t =>
                   CacheToolDetector.FindExecutable(dev, t.Executables, t.KnownInstallFolders) is not null
                   || t.Caches.SelectMany(l => CacheToolDetector.Resolve(dev, l)).Any(FolderMeasure.DirectoryExists))
               || CacheToolDetector.Resolve(dev, DevCacheTools.NuGetGlobalPackages).Any(FolderMeasure.DirectoryExists);
    }

    /// <summary>Docker (its CLI or Desktop's disk) or any WSL distribution exists.</summary>
    public static bool HasDockerOrWsl(ScanContext ctx)
    {
        var dev = DevContext.From(ctx);
        if (CacheToolDetector.FindExecutable(dev, new[] { "docker.exe" }, Containers.DockerWslScanner.DockerCliFolders) is not null)
            return true;
        if (FolderMeasure.DirectoryExists(Path.Combine(dev.Locations.LocalAppData, "Docker", "wsl"))) return true;
        try { return dev.Environment.WslDistros().Count > 0; }
        catch (Exception) { return false; }
    }
}
