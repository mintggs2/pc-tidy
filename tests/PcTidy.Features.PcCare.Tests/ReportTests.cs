using PcTidy.Features.PcCare.Report;
using PcTidy.Features.PcCare.Scanning;

namespace PcTidy.Features.PcCare.Tests;

public class ReportTests
{
    [Fact]
    public void Emails_user_names_profile_folders_and_the_pc_name_are_hidden()
    {
        var text = "Signed in as someone.person@example.com on DESKTOP-ABC123. Path C:\\Users\\jdoe\\AppData and C:\\Users\\Public\\Documents. jdoe's PC.";
        var r = Redactor.Redact(text, new[] { "jdoe", "DESKTOP-ABC123" });
        Assert.DoesNotContain("someone.person@example.com", r);
        Assert.DoesNotContain("jdoe", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-ABC123", r);
        Assert.Contains(@"C:\Users\[hidden]\AppData", r);
        Assert.Contains(@"C:\Users\Public\Documents", r);
    }

    [Fact]
    public void Redaction_matches_whole_words_only()
    {
        // A short user name inside other words stays (only whole words are hidden).
        var r = Redactor.Redact("Ann uses Annotate. ann.", new[] { "Ann" });
        Assert.Equal("[hidden] uses Annotate. [hidden].", r);
    }

    [Fact]
    public void The_report_covers_every_section_and_contains_no_personal_details()
    {
        var s = Scenario.OwnerLike();
        var c = Scenario.Context(redact: new[] { "tester", "TEST-PC" }) with
        {
            RecentRuns = new[] { new RecentRun(FixedClock.Now.AddDays(-1), "Clean", "Completed") },
        };
        var report = Scenario.Build(s, c);
        var html = report.Group(PcCareTabs.Report, "export")!.Items[0].Actions[0].Args!["content"];
        var md = report.Group(PcCareTabs.Report, "export")!.Items[1].Actions[0].Args!["content"];

        foreach (var heading in new[] { "This PC", "Security", "Windows Update", "Drives", "Temperatures", "Apps", "Browsers", "Crashes", "Backups", "Restore points", "Recent PC Tidy runs" })
        {
            Assert.Contains(heading, html);
            Assert.Contains("## " + heading, md);
        }
        Assert.Contains("turned off by a policy", html);
        Assert.Contains("54 °C", md);
        Assert.Contains("54 &#176;C", html);
        Assert.Contains("the last pause ended on", html, StringComparison.Ordinal); // the scenario's pause expired 48 days ago
        foreach (var text in new[] { html, md })
        {
            Assert.DoesNotContain("tester", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TEST-PC", text);
            Assert.DoesNotContain("@", text.Replace("@media", "", StringComparison.Ordinal));
            Assert.DoesNotContain("Person 1", text); // browser profile names stay out of the report
        }
    }

    [Fact]
    public void Html_is_encoded()
    {
        var m = new HealthReportModel("<b>x</b>", FixedClock.Now, new[] { new ReportSection("S", new[] { new ReportLine("<script>", "a & b") }) });
        var html = HealthReportBuilder.Html(m, Array.Empty<string>());
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &amp; b", html);
    }
}
