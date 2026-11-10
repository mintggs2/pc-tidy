using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Report;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.Hosting;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>
/// The extra-report-sections hook: other sections' headings go into the saved health report during the scan, so the shell
/// no longer patches the file before the confirm.
/// </summary>
public class ReportSectionHookTests
{
    private sealed class FixedSection(string heading, params ReportLine[] lines) : IHealthReportSectionSource
    {
        public string Heading => heading;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ReportLine>> LinesAsync(ScanContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ReportLine>>(lines);
        }
    }

    private sealed class BrokenSection : IHealthReportSectionSource
    {
        public string Heading => "Broken";
        public Task<IReadOnlyList<ReportLine>> LinesAsync(ScanContext ctx, CancellationToken ct) => throw new InvalidOperationException("can't read");
    }

    private static (string Html, string Md) Saved(PcCareScanReport report)
    {
        var export = report.Group(PcCareTabs.Report, "export")!;
        return (export.Items[0].Actions[0].Args!["content"], export.Items[1].Actions[0].Args!["content"]);
    }

    [Fact]
    public void Extra_sections_go_before_the_run_list_in_both_formats_and_are_redacted()
    {
        var c = Scenario.Context(redact: new[] { "tester" }) with
        {
            ExtraReportSections = new[] { new ReportSection("Services", new[] { new ReportLine("Services", "212 (180 from Microsoft, 32 from other companies)"), new ReportLine("Owner", "tester") }) },
        };
        var (html, md) = Saved(Scenario.Build(Scenario.OwnerLike(), c));
        Assert.Contains("<h2>Services</h2>", html);
        Assert.True(html.IndexOf("<h2>Services</h2>", StringComparison.Ordinal) < html.IndexOf("<h2>Recent PC Tidy runs</h2>", StringComparison.Ordinal));
        Assert.Contains("## Services", md);
        Assert.True(md.IndexOf("## Services", StringComparison.Ordinal) < md.IndexOf("## Recent PC Tidy runs", StringComparison.Ordinal));
        Assert.DoesNotContain("tester", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tester", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_extra_section_cannot_replace_one_of_pc_cares_own_headings()
    {
        var c = Scenario.Context() with { ExtraReportSections = new[] { new ReportSection("Security", new[] { new ReportLine("Fake", "All good") }) } };
        var (html, _) = Saved(Scenario.Build(Scenario.OwnerLike(), c));
        Assert.Equal(1, CountOf(html, "<h2>Security</h2>"));
        Assert.DoesNotContain("All good", html);
    }

    [Fact]
    public async Task The_scanner_reads_each_source_once_skips_a_broken_one_and_keeps_them_on_rebuild()
    {
        using var tb = new TestbedFolder();
        tb.InstallSnapshot(Scenario.OwnerLike());
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var services = new FixedSection("Services", new ReportLine("Running now", "97"));
        var scanner = new PcCareScanner(clock: new FixedClock(), reportSections: new IHealthReportSectionSource[] { new BrokenSection(), services, new FixedSection("Empty") });
        var ctx = host.CreateScanContext(ScanDepth.Quick);

        var report = await scanner.ScanReportAsync(ctx, null, CancellationToken.None);
        var section = Assert.Single(report.ExtraReportSections);
        Assert.Equal("Services", section.Heading);
        Assert.Contains("<h2>Services</h2>", Saved(report).Html);
        Assert.DoesNotContain("<h2>Broken</h2>", Saved(report).Html);

        var rebuilt = scanner.Rebuild(report, ctx, new[] { new RecentRun(FixedClock.Now, "Clean", "Completed") });
        Assert.Contains("<h2>Services</h2>", Saved(rebuilt).Html);
        Assert.Equal(1, services.Calls); // a rebuild doesn't ask again
    }

    [Fact]
    public void The_module_passes_its_sections_to_the_scanner()
    {
        var module = new PcCareModule(reportSections: new[] { new FixedSection("Services", new ReportLine("Running now", "1")) });
        Assert.IsType<PcCareScanner>(Assert.Single(module.Scanners));
    }

    private static int CountOf(string text, string part)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }
}
