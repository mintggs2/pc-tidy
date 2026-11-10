using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Scanning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>UI review: nothing reads as "all good" when nothing could be checked, and read-only facts aren't tickable rows.</summary>
public class HonestStateTests
{
    [Fact]
    public void No_security_status_shows_the_unknown_antivirus_row_not_an_empty_tab()
    {
        var r = PcCareResultBuilder.Build(new PcCareSnapshot { TakenUtc = FixedClock.Now }, Scenario.Context(drives: Array.Empty<Core.DriveDetails>()));
        var row = Assert.Single(r.Group(T.Security, "antivirus")!.Items);
        Assert.Equal("Antivirus status unknown", row.Title);
        Assert.Equal(Badge.Check, row.Badge);
        Assert.False(row.DefaultSelected);
        Assert.Null(r.Antivirus);
    }

    [Fact]
    public void Health_report_facts_are_plain_lines_not_rows()
    {
        var r = Scenario.Build();
        Assert.Null(r.Group(T.Report, "facts"));
        Assert.NotEmpty(r.Facts.SelectMany(s => s.Lines));
        Assert.All(r.Facts, s => Assert.Contains(s.Heading, PcCareResultBuilder.GlanceHeadings));
        Assert.NotNull(r.Group(T.Report, "export"));
    }

    [Fact]
    public void Every_tab_is_in_exactly_one_group()
    {
        var grouped = T.Groups.SelectMany(g => g.TabIds).ToList();
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Equal(T.All.Select(t => t.Id).OrderBy(x => x), grouped.OrderBy(x => x));
    }

    [Fact]
    public void Titles_name_what_happens_not_the_command_line()
    {
        var r = Scenario.Build();
        Assert.All(r.Result.AllItems, i => Assert.DoesNotContain("cipher /w", i.Title + " " + i.Reason));
        Assert.All(r.Result.AllItems, i => Assert.DoesNotContain("(DISM", i.Title));
    }
}
