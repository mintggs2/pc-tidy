using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.UI;
using PcTidy.Features.Organize.Watch;

namespace PcTidy.Features.Organize;

/// <summary>Section 6 · Organize files: rules, before/after preview, rename helper, optional watch-folder mode.</summary>
public sealed class OrganizeModule : IFeatureModule
{
    public const string FeatureId = "organize";

    private readonly object _gate = new();
    private WatchService? _watch;

    public string Id => FeatureId;
    public string Title => "Organize";
    public string Summary => "Sort loose files into tidy folders, fix messy names, and keep Downloads tidy. Every move can be put back.";
    public NavGroup Group => NavGroup.Organize;
    public string Icon => "FolderArrowRight24";
    public int Order => 6;
    public Release Release => Release.V1;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new OrganizeScanner() };

    public object? CreatePage(IServiceProvider services)
        => new OrganizeView { DataContext = new OrganizeViewModel(services, this) };

    /// <summary>The one watch service for this app session (it keeps running when the page is closed).</summary>
    public WatchService GetWatchService(IServiceProvider services)
    {
        lock (_gate)
        {
            if (_watch is not null) return _watch;
            var appSettings = services.Get<AppSettings>();
            var planner = new OrganizePlanner(services.Get<ISafetyGuard>(), services.Get<IInUseChecker>(), services.Get<IFileWalker>(), appSettings);
            _watch = new WatchService(planner, services.Get<IFlowHost>(), () => OrganizeSettings.Load(appSettings),
                runFinished: run => OrganizeLedger.RecordInto(appSettings, run, DateTime.UtcNow));
            return _watch;
        }
    }

    /// <summary>Call once at app start: resumes watch mode if the user switched it on. Returns immediately.</summary>
    public void StartWatchIfEnabled(IServiceProvider services)
    {
        var appSettings = services.Get<AppSettings>();
        var settings = OrganizeSettings.Load(appSettings);
        if (!settings.Watch.Enabled) return;
        GetWatchService(services).Start(WatchFolders(settings, services.Get<IKnownLocations>()));
    }

    /// <summary>Call on app exit.</summary>
    public async Task StopWatchAsync()
    {
        WatchService? watch;
        lock (_gate) watch = _watch;
        if (watch is not null) await watch.StopAsync();
    }

    /// <summary>Watched folders: the user's list, or Downloads when they haven't picked any.</summary>
    public static IReadOnlyList<string> WatchFolders(OrganizeSettings settings, IKnownLocations locations)
        => settings.Watch.Folders.Count > 0 ? settings.Watch.Folders : new[] { locations.Downloads };
}
