using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.UI;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Duplicates;

/// <summary>
/// Left-rail entry for Duplicates and junk (Plan §8.3, v2): custom page with duplicate groups (change which copy
/// to keep), reference folders, duplicate folders, compare-to-master, look-alike pictures side by side and junk tabs.
/// Hashes are remembered in a <see cref="HashCache"/>, read from the data folder on first use and saved there after
/// every scan (<see cref="SaveCache"/>, through <see cref="HashCacheStore"/>), so the next session's scan is quick too.
/// </summary>
public sealed class DuplicatesModule : IFeatureModule, IModuleRiskInfo
{
    public const string FeatureId = "duplicates";

    private readonly object _cacheLock = new();
    private readonly object _saveLock = new();
    private HashCache? _cache;
    private string? _cacheRoot;

    public DuplicatesModule() => Scanners = new IFeatureScanner[] { new DuplicatesScanner(ctx => CacheFor(ctx.Settings.DataRoot), SaveCache) };

    public string Id => FeatureId;
    public string Title => "Duplicates";
    public string Summary => "Find copies of the same file, look-alike pictures, empty folders and broken shortcuts. One copy is always kept.";
    public NavGroup Group => NavGroup.CleanUp;
    public string Icon => "DocumentCopy24";
    /// <summary>After Clean (10) and Space map (20).</summary>
    public int Order => 30;
    public Release Release => Release.V2;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    /// <summary>
    /// The shared hash cache. The first call reads <see cref="HashCache.PathIn"/>(dataRoot) if it exists and remembers
    /// that data folder for <see cref="SaveCache"/>; later calls return the same instance.
    /// </summary>
    public HashCache CacheFor(string? dataRoot)
    {
        lock (_cacheLock)
        {
            if (_cache is null)
            {
                _cacheRoot = string.IsNullOrWhiteSpace(dataRoot) ? null : dataRoot;
                _cache = _cacheRoot is null ? new HashCache() : HashCache.LoadFrom(_cacheRoot);
            }
            return _cache;
        }
    }

    /// <summary>
    /// Saves the hash cache to the data folder it was read from (after a scan, a compare or a full check; also safe to
    /// call on exit). False (nothing written) when nothing changed or there's no data folder. Never throws for I/O.
    /// </summary>
    public bool SaveCache()
    {
        HashCache? cache;
        string? root;
        lock (_cacheLock) { cache = _cache; root = _cacheRoot; }
        if (cache is null || root is null) return false;
        lock (_saveLock) return HashCacheStore.Save(cache, root);
    }

    /// <summary>Needs IKnownLocations, ISafetyGuard, IFileWalker, AppSettings and IFlowHost; ISystemInfo and IInUseChecker are optional.</summary>
    public object? CreatePage(IServiceProvider services) => new DuplicatesView(services, this);
}
