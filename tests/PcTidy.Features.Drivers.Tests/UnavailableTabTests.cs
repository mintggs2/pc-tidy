using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Scanning;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>A tab PC Tidy couldn't check must never read as "all good" (UI review: a green tick over 0 devices read).</summary>
public class UnavailableTabTests
{
    [Fact]
    public void Nothing_read_marks_every_list_tab_unavailable_and_never_says_no_problems()
    {
        var report = DriverResultBuilder.Build(new DriverInventory(), Fixtures.DataRoot, FixedClock.ScenarioNowUtc, new DriverScanOptions());

        Assert.Contains(DriverTabs.Devices, report.UnavailableTabs);
        Assert.Contains(DriverTabs.Problems, report.UnavailableTabs);
        Assert.Contains(DriverTabs.Store, report.UnavailableTabs);
        Assert.Contains(DriverTabs.Ghosts, report.UnavailableTabs);
        Assert.Contains(DriverTabs.Updates, report.UnavailableTabs); // Windows Update wasn't checked
        Assert.DoesNotContain("No device has a problem", report.TabNotes[DriverTabs.Problems]);
        Assert.Contains("can't be checked", report.TabNotes[DriverTabs.Problems]);
    }

    [Fact]
    public async Task A_full_inventory_has_no_unavailable_list_tabs()
    {
        var report = await Fixtures.ReportAsync();
        Assert.DoesNotContain(DriverTabs.Problems, report.UnavailableTabs);
        Assert.DoesNotContain(DriverTabs.Store, report.UnavailableTabs);
        Assert.DoesNotContain(DriverTabs.Devices, report.UnavailableTabs);
    }
}
