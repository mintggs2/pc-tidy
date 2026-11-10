using PcTidy.Core.Model;

namespace PcTidy.Features.PcCare.Report;

/// <summary>
/// An extra heading for PC care's health report, supplied by another section (PC care can't reference other features).
/// The shell passes these to <see cref="PcCareModule"/>; PC care asks each one for its lines during the scan, read-only,
/// and puts them in the saved report before "Recent PC Tidy runs", so nothing has to patch the file afterwards.
/// A source that fails or takes too long is left out: the report is saved without it.
/// </summary>
public interface IHealthReportSectionSource
{
    /// <summary>The heading ("Services"). A heading PC care already uses is ignored.</summary>
    string Heading { get; }

    /// <summary>The lines for this heading. Read-only; counts and states only, nothing personal (the report is redacted too).</summary>
    Task<IReadOnlyList<ReportLine>> LinesAsync(ScanContext ctx, CancellationToken ct);
}
