using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>A folder and its size, from the Space map.</summary>
public sealed record FolderSize(string Path, long Bytes);

/// <summary>The Space map's biggest folders, when the user has measured a drive this session.</summary>
public sealed record SpaceSnapshot(IReadOnlyList<FolderSize> BiggestFolders, DateTime TakenUtc);

/// <summary>
/// The shell's memory of this session's latest scan results, which "Ask PC Tidy" answers from.
/// In memory only (nothing written to disk). Register ONE instance as a service; feed it by wrapping the
/// scanners the shell runs with <see cref="ScanResultsCache.Track"/> (or call <see cref="Publish"/> after a scan),
/// and call <see cref="PublishSpace"/> when the Space map has measured a drive.
/// </summary>
public interface IScanResultsCache
{
    /// <summary>The newest finished result of each section (feature id), newest first.</summary>
    IReadOnlyList<ScanResult> Latest { get; }
    ScanResult? Get(string featureId);
    SpaceSnapshot? Space { get; }
    /// <summary>Replaces that section's result. Only finished, uncancelled scans belong here.</summary>
    void Publish(ScanResult result);
    void PublishSpace(SpaceSnapshot snapshot);
    event Action? Changed;
}

public sealed class ScanResultsCache : IScanResultsCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScanResult> _byFeature = new(StringComparer.OrdinalIgnoreCase);
    private SpaceSnapshot? _space;

    public event Action? Changed;

    public IReadOnlyList<ScanResult> Latest
    {
        get { lock (_gate) return _byFeature.Values.OrderByDescending(r => r.FinishedUtc).ToList(); }
    }

    public ScanResult? Get(string featureId)
    {
        lock (_gate) return _byFeature.TryGetValue(featureId, out var r) ? r : null;
    }

    public SpaceSnapshot? Space
    {
        get { lock (_gate) return _space; }
    }

    public void Publish(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate) _byFeature[result.FeatureId] = result;
        Changed?.Invoke();
    }

    public void PublishSpace(SpaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _space = snapshot;
        Changed?.Invoke();
    }

    /// <summary>
    /// Wraps a scanner so each finished scan is remembered here. Behaves exactly like the original (same id, title,
    /// release, Optimize-all flag and preview wording), so the shell can hand the wrapped scanners to its flow pages.
    /// </summary>
    public IFeatureScanner Track(IFeatureScanner scanner) => scanner is TrackedScanner t && t.Cache == this ? t : new TrackedScanner(scanner, this);

    private sealed class TrackedScanner : IFeatureScanner, IModuleFlowText
    {
        private readonly IFeatureScanner _inner;

        public TrackedScanner(IFeatureScanner inner, ScanResultsCache cache)
        {
            _inner = inner;
            Cache = cache;
        }

        public ScanResultsCache Cache { get; }
        public string FeatureId => _inner.FeatureId;
        public string Title => _inner.Title;
        public Release Release => _inner.Release;
        public bool IncludedInOptimizeAll => _inner.IncludedInOptimizeAll;
        public string? PreviewNote => (_inner as IModuleFlowText)?.PreviewNote;

        public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            var result = await _inner.ScanAsync(ctx, progress, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) Cache.Publish(result);
            return result;
        }
    }
}
