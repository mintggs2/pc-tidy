using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow.Resume;

namespace PcTidy.UI.Flow;

/// <summary>
/// Marker for pages that fill the window and scroll their own regions (sticky bottom bars).
/// The shell disables the NavigationView's outer scrolling for them (WPF-UI hosts pages in a ScrollViewer
/// that measures with infinite height, which would break "*" rows and list virtualization).
/// </summary>
public interface IFixedHeightPage { }

/// <summary>Per-scanner outcome: a result, or a friendly reason it couldn't finish.</summary>
public sealed record ScannerOutcome(IFeatureScanner Scanner, ScanResult? Result, string? Problem, bool Cancelled)
{
    /// <summary>Set when the result was reused from a scan saved part way: when that section finished.</summary>
    public DateTime? FoundAtUtc { get; init; }
}

/// <summary>Builds scan contexts from the service provider and runs read-only scanners off the UI thread.</summary>
public static class ScanRunner
{
    public static ScanContext CreateContext(IServiceProvider services, ScanDepth depth, string? preset = null)
        => CreateContext(services, depth, preset, roots: null);

    /// <param name="roots">Only these folders, for sections whose scanner takes roots (Explorer's "Tidy this folder").</param>
    public static ScanContext CreateContext(IServiceProvider services, ScanDepth depth, string? preset, IReadOnlyList<string>? roots)
    {
        var system = services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        IReadOnlySet<string> running;
        try { running = system?.RunningProcesses() ?? new HashSet<string>(); }
        catch { running = new HashSet<string>(); }
        return new ScanContext
        {
            Locations = services.Get<IKnownLocations>(),
            Safety = services.Get<ISafetyGuard>(),
            InUse = services.Get<IInUseChecker>(),
            Settings = services.Get<AppSettings>(),
            Walker = services.GetService(typeof(IFileWalker)) as IFileWalker ?? new ManagedFileWalker(),
            Depth = depth,
            Preset = preset,
            Roots = roots,
            RunningProcesses = running,
            System = system,
        };
    }

    /// <summary>
    /// Runs scanners one after another on a background thread. A scanner that throws is reported with a
    /// plain reason and the rest still run. Progress callbacks arrive on the caller's synchronization context.
    /// </summary>
    public static Task<IReadOnlyList<ScannerOutcome>> RunAsync(
        IReadOnlyList<IFeatureScanner> scanners, ScanContext ctx,
        Action<int, IFeatureScanner, ScanProgress> onProgress, CancellationToken ct)
        => RunAsync(scanners, ctx, onProgress, ct, checkpoint: null);

    /// <summary>
    /// As above, saving a checkpoint after each finished section (<paramref name="checkpoint"/>, null = none). Sections the
    /// checkpoint reuses aren't scanned again: their saved result is returned with <see cref="ScannerOutcome.FoundAtUtc"/>.
    /// The checkpoint is deleted when every section has run, and kept (marked stopped) when the user stops.
    /// </summary>
    public static async Task<IReadOnlyList<ScannerOutcome>> RunAsync(
        IReadOnlyList<IFeatureScanner> scanners, ScanContext ctx,
        Action<int, IFeatureScanner, ScanProgress> onProgress, CancellationToken ct, ScanCheckpointSession? checkpoint)
    {
        var outcomes = new List<ScannerOutcome>();
        for (int i = 0; i < scanners.Count; i++)
        {
            var scanner = scanners[i];
            int index = i;
            if (checkpoint?.Reused.TryGetValue(index, out var saved) == true && saved.Result is { } reused)
            {
                outcomes.Add(new ScannerOutcome(scanner, reused, null, false) { FoundAtUtc = saved.FinishedUtc });
                onProgress(index, scanner, new ScanProgress("Done", 1));
                continue;
            }
            var progress = new Progress<ScanProgress>(p => onProgress(index, scanner, p));
            onProgress(index, scanner, new ScanProgress("Starting…", 0));
            try
            {
                var result = await Task.Run(() => scanner.ScanAsync(ctx, progress, ct), ct).ConfigureAwait(true);
                outcomes.Add(new ScannerOutcome(scanner, result, null, false));
                if (checkpoint is not null && !ct.IsCancellationRequested) await checkpoint.SectionFinishedAsync(index, scanner, result).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcomes.Add(new ScannerOutcome(scanner, null, "Stopped", true));
                checkpoint?.Stopped();
                break;
            }
            catch (Exception ex)
            {
                outcomes.Add(new ScannerOutcome(scanner, null, $"{scanner.Title} couldn't finish its scan ({ex.Message}). The other results are still fine.", false));
            }
            onProgress(index, scanner, new ScanProgress("Done", 1));
        }
        if (checkpoint is not null && !outcomes.Any(o => o.Cancelled)) checkpoint.Completed();
        return outcomes;
    }
}
