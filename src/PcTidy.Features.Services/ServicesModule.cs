using PcTidy.Features.Services.Scanning;
using PcTidy.Features.Services.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Services;

/// <summary>
/// Services (v3): every Windows service with what it does, who made it, whether its file is signed, how it starts,
/// whether it's running and what it costs, plus a "safe to change?" verdict from PC Tidy's service catalog. Offers
/// only catalog-approved changes (Manual / Disabled / back to the Windows default), Manual for other companies'
/// services, profiles built from proven entries, and a repair for Windows services something switched off.
/// Everything is a reviewed, reversible system change (restore point first; undo restores the exact old settings).
/// v3.1: a rail counter, "N changed" (from Windows default; <see cref="INavBadgeSource"/>).
/// </summary>
public sealed class ServicesModule : IFeatureModule, IModuleRiskInfo, IModuleFlowText, INavBadgeSource
{
    private Func<ServicesEnvironment?> _badgeEnvironment;
    private readonly string _badgeWindowsDir;

    /// <param name="environment">Read-only sources (tests pass fakes). Default: the real PC, or the sandbox's practice PC.</param>
    /// <param name="badgeEnvironment">
    /// What the rail counter reads (it has no scan context). Default: the real PC when <paramref name="environment"/> is the
    /// default too, else nothing (null), so a test or practice module never counts the real PC's services.
    /// </param>
    /// <param name="badgeWindowsDir">The Windows folder the counter uses to tell Windows' services from others. Default: this PC's.</param>
    public ServicesModule(Func<ScanContext, ServicesEnvironment>? environment = null, Func<ServicesEnvironment?>? badgeEnvironment = null, string? badgeWindowsDir = null)
    {
        Environment = environment ?? ServicesEnvironment.For;
        Scanner = new ServicesScanner(Environment);
        Repair = new ServiceRepairScanner(Environment);
        Scanners = new IFeatureScanner[] { Scanner, Repair };
        _badgeEnvironment = badgeEnvironment ?? (environment is null ? () => ServicesEnvironment.Real : () => null);
        _badgeWindowsDir = badgeWindowsDir ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
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
    public object? CreatePage(IServiceProvider services)
    {
        AttachServices(services);
        return new ServicesPage { DataContext = new ServicesViewModel(services, this) };
    }

    /// <summary>
    /// In a sandbox or demo the rail counter shows nothing: it has no scan context, and counting the real PC's services
    /// there would be misleading. Called at start-up by the shell and by <see cref="CreatePage"/>.
    /// </summary>
    public void AttachServices(IServiceProvider services)
    {
        if (services.GetService(typeof(IKnownLocations)) is IKnownLocations { IsSandbox: true }) _badgeEnvironment = () => null;
    }

    /// <summary>
    /// "N changed from Windows default": Windows services with a catalog default that start differently now. Read from the
    /// Service Control Manager's list only (query rights, no file or process checks), off the calling thread. Null when
    /// nothing changed, or when the list can't be read.
    /// </summary>
    public Task<string?> GetNavBadgeAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (_badgeEnvironment() is not { } env) return null;
            var records = env.Services.ReadAll(new List<string>(), ct);
            if (records.Count == 0) return null;
            return ServiceDefaultsCounter.BadgeText(ServiceDefaultsCounter.ChangedFromDefault(records, _badgeWindowsDir).Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }, ct);
}
