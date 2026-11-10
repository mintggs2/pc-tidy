using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Report;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Health report tab: the facts at a glance, and "save the report" rows (HTML or Markdown, redacted).</summary>
public static partial class PcCareResultBuilder
{
    /// <summary>&lt;DataRoot&gt;\reports\health-&lt;date&gt;.&lt;ext&gt; (local date).</summary>
    public static string ReportPath(string dataRoot, DateTime nowUtc, string extension)
        => Path.Combine(DataPaths.Reports(dataRoot), $"health-{nowUtc.ToLocalTime():yyyy-MM-dd}.{extension}");

    /// <summary>The report sections shown on the page at a glance (the rest only go into the saved file).</summary>
    internal static readonly string[] GlanceHeadings = { "This PC", "Security", "Drives", "Temperatures", "Backups" };

    /// <summary>
    /// Adds the "save the report" rows. The facts at a glance are returned rather than added as rows: they're read-only
    /// key/value lines, so the page shows them as a plain list (no tick box, and no Safe badge that would read as "all good").
    /// </summary>
    private static (string Path, IReadOnlyList<ReportSection> Facts) AddReport(PcCareSnapshot s, PcCareBuildContext c, AntivirusVerdict? av,
        List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var model = HealthReportBuilder.Model(s, c, av);
        var facts = model.Sections.Where(x => GlanceHeadings.Contains(x.Heading)).ToList();

        var html = ReportPath(c.DataRoot, c.NowUtc, "html");
        var md = ReportPath(c.DataRoot, c.NowUtc, "md");
        groups.Add(Group(T.Report, "export", "Save the health report",
            "One page you can keep or share when asking for help. User names, emails and the PC's name are left out.", Badge.Safe, new[]
            {
                Pick("Save as a web page (HTML)", "Opens in any browser. Saved in PC Tidy's reports folder; undo deletes it again.", Badge.Safe,
                    new[] { PcCareActions.WriteReport(html, HealthReportBuilder.Html(model, c.RedactTerms)) }, detail: html),
                Pick("Save as Markdown", "Plain text with headings, good for pasting into a forum post.", Badge.Safe,
                    new[] { PcCareActions.WriteReport(md, HealthReportBuilder.Markdown(model, c.RedactTerms)) }, detail: md),
            }));
        notes[T.Report] = "Making the report only reads: nothing on the PC changes. Saving it writes one file into PC Tidy's reports folder.";
        return (html, facts);
    }
}
