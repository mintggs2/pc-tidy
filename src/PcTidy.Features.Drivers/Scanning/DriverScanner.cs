using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Source;

namespace PcTidy.Features.Drivers.Scanning;

/// <summary>
/// Read-only scanner for the Drivers section (Plan §8 "13 · Drivers"). Not part of "Optimize everything":
/// every driver change is Check, admin and picked by hand on the Drivers page.
/// </summary>
public sealed class DriverScanner : IFeatureScanner
{
    private readonly Func<IKnownLocations, IDriverSource> _sourceFor;
    private readonly TimeProvider _clock;

    /// <param name="sourceFor">Where the driver data comes from; default <see cref="DriverSources.For"/>.</param>
    /// <param name="clock">Clock for the 14-day and 30-day rules; default system time.</param>
    public DriverScanner(Func<IKnownLocations, IDriverSource>? sourceFor = null, TimeProvider? clock = null)
    {
        _sourceFor = sourceFor ?? DriverSources.For;
        _clock = clock ?? TimeProvider.System;
    }

    public string FeatureId => DriverActions.FeatureId;
    public string Title => "Drivers";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => (await ScanReportAsync(ctx, progress, ct).ConfigureAwait(false)).Result;

    /// <summary>The full report (scan result + per-tab notes + the inventory, so the page can re-analyse without rescanning).</summary>
    public async Task<DriverScanReport> ScanReportAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var options = DriverScanOptions.From(ctx.Options);
        var source = _sourceFor(ctx.Locations);
        var steps = new Progress<string>(m => progress?.Report(new ScanProgress(m)));
        var inventory = await DriverInventory.LoadAsync(source, BackupFolders.Root(ctx.Locations.DataRoot), options.SearchWindowsUpdate, steps, ct)
            .ConfigureAwait(false);
        return DriverResultBuilder.Build(inventory, ctx.Locations.DataRoot, _clock.GetUtcNow().UtcDateTime, options, sw.Elapsed);
    }

    /// <summary>Rebuilds the rows from an earlier inventory with new options (e.g. "Let me pick important drivers").</summary>
    public DriverScanReport Rebuild(DriverInventory inventory, string dataRoot, DriverScanOptions options)
        => DriverResultBuilder.Build(inventory, dataRoot, _clock.GetUtcNow().UtcDateTime, options);
}

/// <summary>Picks the data source for a PC: the real one, or fixtures in a sandbox (real tools never run there).</summary>
public static class DriverSources
{
    /// <summary>Sandbox fixtures live in &lt;sandbox root&gt;\Sim\drivers (missing folder = nothing available).</summary>
    public static IDriverSource For(IKnownLocations locations)
    {
        if (!locations.IsSandbox) return new WindowsDriverSource();
        var root = locations is SandboxKnownLocations s ? s.Root : locations.DataRoot;
        return new FixtureDriverSource(Path.Combine(root, "Sim", "drivers"));
    }
}
