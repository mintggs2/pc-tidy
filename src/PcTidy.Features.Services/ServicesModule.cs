using PcTidy.Features.Services.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Services;

/// <summary>
/// Services (v3): every Windows service with what it does, who made it, whether its file is signed, how it starts,
/// whether it's running and what it costs, plus a "safe to change?" verdict from PC Tidy's service catalog. Offers
/// only catalog-approved changes (Manual / Disabled / back to the Windows default), Manual for other companies'
/// services, profiles built from proven entries, and a repair for Windows services something switched off.
/// Everything is a reviewed, reversible system change (restore point first; undo restores the exact old settings).
/// </summary>
public sealed class ServicesModule : IFeatureModule, IModuleRiskInfo, IModuleFlowText
{
    /// <param name="environment">Read-only sources (tests pass fakes). Default: the real PC, or the sandbox's practice PC.</param>
    public ServicesModule(Func<ScanContext, ServicesEnvironment>? environment = null)
    {
        Environment = environment ?? ServicesEnvironment.For;
        Scanner = new ServicesScanner(Environment);
        Repair = new ServiceRepairScanner(Environment);
        Scanners = new IFeatureScanner[] { Scanner, Repair };
    }

    public string Id => ServicesIds.Module;
    public string Title => "Services";
    public string Summary => "What each Windows service does, who made it and whether it's safe to change. Only proven changes are offered, and everything can be undone.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "WindowWrench24";
    public int Order => 13;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public Func<ScanContext, ServicesEnvironment> Environment { get; }
    public ServicesScanner Scanner { get; }
    public ServiceRepairScanner Repair { get; }

    public string? PreviewNote => "Nothing is ticked. Ticked services change how they start; PC Tidy makes a restore point first and saves each one's old settings, so Undo in History puts them back exactly.";

    /// <summary>Needs IKnownLocations, ISafetyGuard, IInUseChecker, AppSettings and IFlowHost (IShellFlowHost for one confirm and the inline Done card).</summary>
    public object? CreatePage(IServiceProvider services) => new ServicesPage { DataContext = new ServicesViewModel(services, this) };
}
