using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Source;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>
/// Read-only scanner for PC care (Plan §8 "16 · PC care" and the approved v2 extras). Not part of "Optimize everything":
/// every PC care action is picked by hand on its own tab.
/// </summary>
public sealed class PcCareScanner : IFeatureScanner
{
    /// <summary>How long one extra report section may take before the report is made without it.</summary>
    public static readonly TimeSpan ReportSectionTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<IKnownLocations, IPcCareSource> _sourceFor;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<Report.IHealthReportSectionSource> _reportSections;

    /// <param name="sourceFor">Where the facts come from; default <see cref="PcCareSourceFactory.For"/> (real PC, or the sandbox snapshot).</param>
    /// <param name="clock">Clock for "last 30 days" and dates; default system time.</param>
    /// <param name="reportSections">Headings other sections add to the health report (the shell supplies them).</param>
    public PcCareScanner(Func<IKnownLocations, IPcCareSource>? sourceFor = null, TimeProvider? clock = null,
        IEnumerable<Report.IHealthReportSectionSource>? reportSections = null)
    {
        _sourceFor = sourceFor ?? PcCareSourceFactory.For;
        _clock = clock ?? TimeProvider.System;
        _reportSections = reportSections?.ToList() ?? new List<Report.IHealthReportSectionSource>();
    }

    public string FeatureId => PcCareActions.FeatureId;
    public string Title => "PC care";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => (await ScanReportAsync(ctx, progress, ct).ConfigureAwait(false)).Result;

    public async Task<PcCareScanReport> ScanReportAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct, IReadOnlyList<RecentRun>? runs = null)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var options = PcCareScanOptions.From(ctx.Options);
        var steps = new Progress<string>(m => progress?.Report(new ScanProgress(m)));
        var snapshot = await _sourceFor(ctx.Locations).ReadAsync(options, steps, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        snapshot = WithSystemFacts(snapshot, ctx.System);
        var extra = await ReadReportSectionsAsync(ctx, ct).ConfigureAwait(false);
        return PcCareResultBuilder.Build(snapshot, BuildContext(ctx, options, runs, extra), sw.Elapsed);
    }

    /// <summary>Runs only the DNS speed test and rebuilds the rows from an earlier snapshot.</summary>
    public async Task<PcCareScanReport> RunDnsTestAsync(PcCareScanReport earlier, ScanContext ctx, CancellationToken ct, IReadOnlyList<RecentRun>? runs = null)
    {
        var network = await _sourceFor(ctx.Locations).TestDnsAsync(ct).ConfigureAwait(false);
        var snapshot = earlier.Snapshot with { Network = network ?? earlier.Snapshot.Network };
        var options = PcCareScanOptions.From(ctx.Options) with { RunDnsTest = true };
        return PcCareResultBuilder.Build(snapshot, BuildContext(ctx, options, runs, earlier.ExtraReportSections));
    }

    /// <summary>Rebuilds the rows from an earlier snapshot (e.g. with the run history for the report).</summary>
    public PcCareScanReport Rebuild(PcCareScanReport earlier, ScanContext ctx, IReadOnlyList<RecentRun>? runs = null)
        => PcCareResultBuilder.Build(earlier.Snapshot, BuildContext(ctx, PcCareScanOptions.From(ctx.Options), runs, earlier.ExtraReportSections));

    /// <summary>
    /// Asks each extra report source for its lines, one at a time. A source that throws, times out or returns nothing is
    /// left out (the report is still saved); cancelling the scan cancels this too.
    /// </summary>
    internal async Task<IReadOnlyList<Report.ReportSection>> ReadReportSectionsAsync(ScanContext ctx, CancellationToken ct)
    {
        var sections = new List<Report.ReportSection>();
        foreach (var source in _reportSections)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReportSectionTimeout);
            try
            {
                var lines = await source.LinesAsync(ctx, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(source.Heading) && lines.Count > 0) sections.Add(new Report.ReportSection(source.Heading.Trim(), lines.ToList()));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Too slow: the report goes without this heading.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Health report section '{source.Heading}' failed: {ex.Message}");
            }
        }
        return sections;
    }

    private PcCareBuildContext BuildContext(ScanContext ctx, PcCareScanOptions options, IReadOnlyList<RecentRun>? runs,
        IReadOnlyList<Report.ReportSection> extra) => new()
    {
        ExtraReportSections = extra,
        NowUtc = _clock.GetUtcNow().UtcDateTime,
        DataRoot = ctx.Locations.DataRoot,
        SystemDrive = ctx.Locations.SystemDrive,
        Drives = ctx.System?.Drives ?? ctx.Locations.FixedDrives.Select(d => new DriveDetails(d, "", DriveMedia.Unknown, 0, 0, false)).ToList(),
        RecentRuns = runs ?? Array.Empty<RecentRun>(),
        RedactTerms = RedactTerms(ctx.Locations),
        Options = options,
    };

    /// <summary>The user's name, profile folder name and the PC's name are kept out of the exported report.</summary>
    internal static IReadOnlyList<string> RedactTerms(IKnownLocations locations)
    {
        var terms = new List<string> { Path.GetFileName(locations.UserProfile.TrimEnd('\\')) };
        if (!locations.IsSandbox)
        {
            terms.Add(Environment.UserName);
            terms.Add(Environment.MachineName);
            try { terms.Add(Environment.UserDomainName); } catch (InvalidOperationException) { }
        }
        return terms.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PcCareSnapshot WithSystemFacts(PcCareSnapshot s, ISystemInfo? system)
    {
        if (system is null) return s;
        var health = s.Health ?? new HealthFacts { Build = system.WindowsBuild };
        return s with { IsAdmin = s.IsAdmin || system.IsAdmin, Health = health with { RebootPending = health.RebootPending || system.RebootPending } };
    }
}
