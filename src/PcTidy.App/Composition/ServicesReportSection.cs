using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Report;
using PcTidy.Features.Services;
using PcTidy.Features.Services.Scanning;

namespace PcTidy.App.Composition;

/// <summary>
/// The "Services" heading of PC care's health report. PC care can't reference another feature, so the shell hands it
/// this source (<see cref="FeatureCatalog"/>): during PC care's scan it reads Services' read-only inventory and
/// summarises it (counts only, nothing personal). If the inventory can't be read, PC care saves the report without it.
/// </summary>
public sealed class ServicesReportSection : IHealthReportSectionSource
{
    private readonly ServicesModule _module;

    public ServicesReportSection(ServicesModule module) => _module = module;

    public string Heading => ServicesHealthSummary.Heading;

    public async Task<IReadOnlyList<ReportLine>> LinesAsync(ScanContext ctx, CancellationToken ct)
    {
        var inventory = await _module.Scanner.InventoryAsync(ctx, null, ct).ConfigureAwait(false);
        return ToReportLines(ServicesHealthSummary.Lines(inventory));
    }

    /// <summary>Pure part (tests): Services' summary lines as report lines, in the same order.</summary>
    public static IReadOnlyList<ReportLine> ToReportLines(IReadOnlyList<(string Label, string Value)> lines)
        => lines.Select(l => new ReportLine(l.Label, l.Value)).ToList();
}
