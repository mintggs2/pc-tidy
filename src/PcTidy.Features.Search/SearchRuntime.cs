using PcTidy.Core;
using PcTidy.Features.Search.Backends;
using PcTidy.Features.Search.Index;

namespace PcTidy.Features.Search;

/// <summary>
/// The parts of Search that outlive the page: the own index (with its watchers and background refresh) and the
/// backend router. One per data folder for the whole app run, so leaving and re-opening the page is instant.
/// The shell may register its own instance in the service provider; otherwise <see cref="For"/> makes one.
/// </summary>
public sealed class SearchRuntime : IDisposable
{
    private static readonly Dictionary<string, SearchRuntime> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SharedGate = new();
    private Task? _started;

    public SearchRuntime(IKnownLocations locations, ISafetyGuard safety, IFileWalker walker, AppSettings settings,
        TimeProvider? clock = null, IEverythingLocator? everything = null, IChangeHintSource? hints = null)
    {
        clock ??= TimeProvider.System;
        Locations = locations;
        Settings = settings;
        var state = SearchSettings.Load(settings);
        Store = new SearchIndexStore(settings.DataRoot);
        var builder = new IndexBuilder(walker, safety, locations, state.IndexAppData);
        var policy = new RefreshPolicy { PeriodicRescan = TimeSpan.FromMinutes(Math.Clamp(state.RescanMinutes, 5, 24 * 60)) };
        var ignored = state.IndexAppData ? Array.Empty<string>() : new[] { Path.Combine(locations.UserProfile, "AppData") };
        Index = new SearchIndexService(builder, Store, () => IndexRoots.Resolve(locations, SearchSettings.Load(settings)), clock, policy,
            hints ?? new FileSystemWatcherHints(), ignored);
        Everything = new EverythingBackend(everything ?? new EverythingLocator(), clock);
        Router = new SearchRouter(new OwnIndexBackend(() => Index.Current), Everything, () => SearchSettings.Load(settings).UseEverything);
    }

    public IKnownLocations Locations { get; }
    public AppSettings Settings { get; }
    public SearchIndexStore Store { get; }
    public SearchIndexService Index { get; }
    public EverythingBackend Everything { get; }
    public SearchRouter Router { get; }

    /// <summary>Loads the saved index and starts background refresh (once).</summary>
    public Task EnsureStartedAsync() => _started ??= Index.StartAsync();

    /// <summary>The app-wide runtime for this data folder (registered service first, else a shared one).</summary>
    public static SearchRuntime For(IServiceProvider services)
    {
        if (services.GetService(typeof(SearchRuntime)) is SearchRuntime registered) return registered;
        var settings = services.Get<AppSettings>();
        lock (SharedGate)
        {
            if (Shared.TryGetValue(settings.DataRoot, out var existing)) return existing;
            var runtime = new SearchRuntime(services.Get<IKnownLocations>(), services.Get<ISafetyGuard>(),
                services.GetService(typeof(IFileWalker)) as IFileWalker ?? new ManagedFileWalker(), settings);
            Shared[settings.DataRoot] = runtime;
            return runtime;
        }
    }

    public void Dispose() => Index.Dispose();
}
