using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Structure;
using PcTidy.Features.Search.UI;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>v3 UI review fixes for Search and "Suggest a folder structure" (UI3-04, UI3-19).</summary>
[Collection("search-testbed")]
public class UiReviewFixTests
{
    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"X:\testbed\pc\D\", "D:")] // a practice PC's drive is a folder named after its letter
    [InlineData(@"X:\testbed\pc\D", "D:")]
    [InlineData(@"X:\Data", "Data")]
    public void Drive_labels_are_short(string drive, string label) => Assert.Equal(label, StructurePlanner.DriveLabel(drive));

    [Fact]
    public void Blocked_reasons_read_as_one_sentence()
    {
        Assert.Equal("PC Tidy can't do this here because it's in a cloud-synced folder (like OneDrive).",
            StructurePlanner.CantDoHere("This is in a cloud-synced folder (like OneDrive)."));
        Assert.Equal("PC Tidy can't do this here because it's a protected place.", StructurePlanner.CantDoHere(null));
        // Names keep their capital letter.
        Assert.Equal("PC Tidy can't do this here because OneDrive needs your OK.", StructurePlanner.CantDoHere("OneDrive needs your OK."));
    }

    [Fact]
    public async Task Structure_groups_say_what_they_are_before_where()
    {
        using var od = new SearchSandbox("structure-titles", SandboxProfile.Default);
        od.Put(Path.Combine(od.K.Documents, "Taxes", "2025.pdf"), new byte[2000], SearchSandbox.Now.AddDays(-30));
        var result = await new SearchScanner(new ManualClock(new DateTimeOffset(SearchSandbox.Now))).ScanAsync(od.Host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var kept = Assert.Single(result.Groups, g => g.Id.EndsWith("-kept") && g.Items.Any(i => i.Title == "Taxes"));
        Assert.StartsWith("Folders left where they are in Documents (", kept.Title);
        Assert.DoesNotContain(od.Root, kept.Title, StringComparison.OrdinalIgnoreCase); // no practice-PC path in titles
        // Documents is in OneDrive here: one plain note says so.
        Assert.Contains(result.Notes, n => n.Contains("synced by OneDrive"));
        Assert.All(result.AllItems.Where(i => i.DisabledReason is not null), i => Assert.DoesNotContain("here: ", i.DisabledReason));
    }

    [Fact]
    public void Index_freshness_is_plain()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("", SearchPage.Freshness(null, now));
        Assert.Equal(" · updated just now", SearchPage.Freshness(now.AddSeconds(-30), now));
        Assert.Equal(" · updated 5 min ago", SearchPage.Freshness(now.AddMinutes(-5), now));
        Assert.Equal(" · updated 3 h ago", SearchPage.Freshness(now.AddHours(-3), now));
    }
}
