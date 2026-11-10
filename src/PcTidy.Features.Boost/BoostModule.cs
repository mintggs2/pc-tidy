using PcTidy.Features.Boost.UI;

namespace PcTidy.Features.Boost;

/// <summary>
/// "Game boost" in the left rail (Speed up group, v3). Optional and off by default. The shell calls
/// <see cref="Start"/> once the main window is shown (it also puts back anything a crash left boosted) and
/// <see cref="StopAsync"/> on exit.
/// </summary>
public sealed class BoostModule : IFeatureModule
{
    public const string FeatureId = BoostSettings.FeatureKey;

    private readonly object _gate = new();
    private BoostService? _service;

    public string Id => FeatureId;
    public string Title => "Game boost";
    public string Summary => "When a game you pick starts, pause OneDrive and politely close the apps you chose. Everything comes back when the game closes.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "Rocket24";
    public int Order => 65;
    public Release Release => Release.V3;
    /// <summary>No scan: Game boost has its own page and is never part of "Optimize everything".</summary>
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = Array.Empty<IFeatureScanner>();

    public object? CreatePage(IServiceProvider services) => new BoostPage(new BoostViewModel(services, this));

    /// <summary>The one boost service for this app session (it keeps running when the page is closed).</summary>
    public BoostService GetService(IServiceProvider services)
    {
        lock (_gate)
        {
            if (_service is not null) return _service;
            var appSettings = services.Get<AppSettings>();
            _service = BoostService.Create(services, () => BoostSettings.Load(appSettings));
            return _service;
        }
    }

    /// <summary>
    /// Call once at app start. Always puts back a boost a previous session left in effect (crash, power cut);
    /// then keeps watching only if the user switched Game boost on. Returns immediately.
    /// </summary>
    public void Start(IServiceProvider services) => GetService(services).Start();

    /// <summary>Call on app exit. A boost in effect stays recorded and is put back at the next start.</summary>
    public async Task StopAsync()
    {
        BoostService? service;
        lock (_gate) service = _service;
        if (service is not null) await service.StopAsync().ConfigureAwait(false);
    }
}
