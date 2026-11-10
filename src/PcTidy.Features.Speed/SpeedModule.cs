using PcTidy.Features.Speed.ContextMenu;
using PcTidy.Features.Speed.Drives;
using PcTidy.Features.Speed.Locking;
using PcTidy.Features.Speed.Running;
using PcTidy.Features.Speed.Settings;
using PcTidy.Features.Speed.Startup;
using PcTidy.Features.Speed.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Speed;

/// <summary>
/// Section 5 · Speed: startup manager, running apps, speed settings, drive health and optimize, the right-click
/// menu cleaner and "What's locking this?", on one tabbed page. Every change goes through the shell's confirm.
/// </summary>
public sealed class SpeedModule : IFeatureModule, IModuleRiskInfo
{
    public const string ModuleId = "speed";

    /// <param name="environment">Read-only sources for every scanner (tests pass fakes). Default: the real PC, or the sandbox.</param>
    public SpeedModule(Func<ScanContext, SpeedEnvironment>? environment = null)
    {
        Environment = environment ?? SpeedEnvironment.For;
        Scanner = new SpeedScanner(Environment);
        Startup = new StartupScanner(Environment);
        Running = new RunningAppsScanner(Environment);
        SettingsCards = new SettingsScanner(Environment);
        Drives = new DrivesScanner(Environment);
        ContextMenu = new ContextMenuScanner(Environment);
        Locking = new LockScanner(Environment);
        Scanners = new IFeatureScanner[] { Scanner };
    }

    public string Id => ModuleId;
    public string Title => "Speed";
    public string Summary => "What starts with Windows, what's running now, speed settings, drive health, the right-click menu, and what's locking a file. Everything can be undone.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "TopSpeed24";
    public int Order => 5;
    public Release Release => Release.V2;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public Func<ScanContext, SpeedEnvironment> Environment { get; }
    public SpeedScanner Scanner { get; }
    public StartupScanner Startup { get; }
    public RunningAppsScanner Running { get; }
    public SettingsScanner SettingsCards { get; }
    public DrivesScanner Drives { get; }
    public ContextMenuScanner ContextMenu { get; }
    public LockScanner Locking { get; }

    public object? CreatePage(IServiceProvider services) => new SpeedPage(services, this);
}
