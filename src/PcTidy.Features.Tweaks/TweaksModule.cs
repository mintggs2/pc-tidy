using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.Features.Tweaks.UI;

namespace PcTidy.Features.Tweaks;

/// <summary>Section 12 · Tweaks: proven tweaks with their live state, presets, Expert mode, and "Find bad tweaks".</summary>
public sealed class TweaksModule : IFeatureModule, INavBadgeSource
{
    public const string ModuleId = "tweaks";

    private Func<TweakEnvironment>? _badgeEnvironment;
    private readonly bool _badgeInjected;

    /// <param name="environment">Readers for both scanners (tests pass redirected ones). Default: real PC, or the sandbox.</param>
    /// <param name="badgeEnvironment">Readers for the rail counter (tests pass fakes). Default: see <see cref="AttachServices"/>.</param>
    public TweaksModule(Func<ScanContext, TweakEnvironment>? environment = null, Func<TweakEnvironment>? badgeEnvironment = null)
    {
        TweaksScanner = new TweaksScanner(environment);
        BadTweakScanner = new BadTweakScanner(environment);
        Scanners = new IFeatureScanner[] { TweaksScanner, BadTweakScanner };
        _badgeEnvironment = badgeEnvironment;
        _badgeInjected = badgeEnvironment is not null;
    }

    public string Id => ModuleId;
    public string Title => "Tweaks";
    public string Summary => "Windows settings that really help, with what each one does and its source. Find bad tweaks puts back what old optimizers broke. Everything can be undone.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "Wrench24";
    public int Order => 12;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public TweaksScanner TweaksScanner { get; }
    public BadTweakScanner BadTweakScanner { get; }

    public object? CreatePage(IServiceProvider services)
    {
        AttachServices(services);
        return new TweaksPage { DataContext = new TweaksViewModel(services, this) };
    }

    /// <summary>
    /// Points the rail counter at the right PC: the sandbox's redirected registry in a sandbox or demo, the real PC otherwise.
    /// Called by <see cref="CreatePage"/>; the shell may also call it at start-up so the counter is right before the page opens.
    /// An injected badge environment (tests) is kept.
    /// </summary>
    public void AttachServices(IServiceProvider services)
    {
        if (_badgeInjected) return;
        if (services.GetService(typeof(IKnownLocations)) is not IKnownLocations locations) return;
        var system = services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        _badgeEnvironment = locations.IsSandbox
            ? () => TweakEnvironment.Sandbox(locations, system)
            : () => TweakEnvironment.Real(locations, system);
    }

    /// <summary>
    /// "N of M on" (v3.1 rail counter): the same count as the page header, read from the real settings (never from what
    /// PC Tidy remembers). Read-only and registry-only, so it's cheap. Null when nothing could be read. Before services are
    /// attached it reads this PC's own registry, read-only, without the GPU and laptop details.
    /// </summary>
    public Task<string?> GetNavBadgeAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var env = (_badgeEnvironment ?? DefaultBadgeEnvironment)();
                return TweakCounter.Read(env, ct).Text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                return null;
            }
        }, ct);

    private static TweakEnvironment DefaultBadgeEnvironment()
    {
        var registry = new RegistryReader(RegistryRedirect.None);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new TweakEnvironment
        {
            Registry = registry,
            Probe = new WindowsSystemProbe(registry),
            Boot = new FixedBootConfigReader(BootConfigSnapshot.Empty),
            Apps = new FixedAppxInventory(),
            Tasks = new FixedTaskReader(),
            HostsPath = TweakEnvironment.HostsFileIn(windows),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            WindowsDir = windows,
            IsRealPc = true,
        };
    }
}
