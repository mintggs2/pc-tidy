using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Features.Drivers.Source;
using PcTidy.Features.Drivers.View;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Drivers;

/// <summary>
/// Left-rail entry for Drivers (Plan §8 "13 · Drivers"): inventory, problem devices, official updates,
/// driver-store cleanup and old devices, on one custom page with five tabs. Every change goes through the
/// standard confirm (IFlowHost), starts with a driver backup, and runs in PC Tidy's admin helper.
/// </summary>
public sealed class DriversModule : IFeatureModule, IModuleRiskInfo
{
    private readonly DriverScanner _scanner;

    /// <param name="sourceFor">Data source override (tests, demos); default: real PC, or fixtures in a sandbox.</param>
    /// <param name="clock">Clock override for tests.</param>
    public DriversModule(Func<IKnownLocations, IDriverSource>? sourceFor = null, TimeProvider? clock = null)
    {
        _scanner = new DriverScanner(sourceFor, clock);
        Scanners = new IFeatureScanner[] { _scanner };
    }

    public string Id => DriverActions.FeatureId;
    public string Title => "Drivers";
    public string Summary => "See every driver, fix problem devices, get official updates and tidy old drivers and devices.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "DeveloperBoard24";
    /// <summary>Speed up group: Speed · Tweaks · Drivers · Games · Apps · PC care.</summary>
    public int Order => 30;
    public Release Release => Release.V2;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>Needs IKnownLocations, AppSettings and IFlowHost; ISafetyGuard etc. come from the same provider.</summary>
    public object? CreatePage(IServiceProvider services) => new DriversView(services, _scanner);
}
