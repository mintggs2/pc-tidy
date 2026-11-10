using PcTidy.Catalog.Drivers;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Scanning;

namespace PcTidy.Features.Drivers.Tests;

public class GhostDeviceTests
{
    [Theory]
    [InlineData(@"USB\VID_6A0C&PID_1142\6&FIXTURE&3&1", GhostVerdict.Removable)]
    [InlineData(@"USB\VID_4C0F&PID_0201\6&FIXTURE&2&3", GhostVerdict.Removable)]
    [InlineData(@"HID\VID_7B19&PID_00E4\8&FIXTURE&4&0002", GhostVerdict.SeenRecently)]
    [InlineData(@"USB\VID_8D02&PID_3310&MI_00\7&FIXTURE&5&0000", GhostVerdict.UnknownLastSeen)]
    [InlineData(@"USBSTOR\DISK&VEN_FABRIKAM&PROD_POCKETDRIVE&REV_1.00\FIXTURE0007&0", GhostVerdict.ProtectedClass)]
    [InlineData(@"VSSNAP\FIXTURE\3", GhostVerdict.ProtectedClass)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.1.00000000}.{FIXTURE-7C21}", GhostVerdict.SoftwareDevice)]
    [InlineData(@"ROOT\NET\0003", GhostVerdict.SoftwareDevice)]
    [InlineData(@"USB\VID_9F00&PID_0B0B\WEIRD NAME[2]", GhostVerdict.UnsafeId)]
    public async Task Each_ghost_gets_the_researched_verdict(string instanceId, GhostVerdict expected)
    {
        var inv = await Fixtures.InventoryAsync();
        var decision = Assert.Single(GhostRules.Evaluate(inv.Disconnected, FixedClock.ScenarioNowUtc), d => d.Device.InstanceId == instanceId);
        Assert.Equal(expected, decision.Verdict);
    }

    [Fact]
    public async Task Removable_ghosts_are_one_device_per_step_unticked_with_the_settings_warning()
    {
        var report = await Fixtures.ReportAsync();
        var items = report.Result.Group(DriverTabs.Ghosts, "removable")!.Items;

        Assert.Equal(2, items.Count);
        foreach (var i in items)
        {
            Assert.Equal(Badge.Check, i.Badge);
            Assert.False(i.DefaultSelected);
            Assert.Contains("settings", i.Reason, StringComparison.OrdinalIgnoreCase);
            var step = Assert.Single(i.Actions);
            Assert.Equal(ToolIds.PnpRemoveDevice, step.ToolId);
            Assert.Equal(new[] { "instanceId" }, step.Args!.Keys);
            Assert.True(DriverClassRules.IsSafeInstanceId(step.Args["instanceId"]));
            Assert.False(step.Reversible);
            Assert.True(step.NeedsAdmin);
        }
    }

    [Fact]
    public async Task Kept_ghosts_are_shown_but_cannot_be_picked_and_software_devices_are_not_listed()
    {
        var report = await Fixtures.ReportAsync();
        var kept = report.Result.Group(DriverTabs.Ghosts, "kept")!;

        Assert.Equal(5, kept.Items.Count);
        Assert.All(kept.Items, i => { Assert.NotNull(i.DisabledReason); Assert.Empty(i.Actions); });
        Assert.DoesNotContain(kept.Items, i => i.Title.Contains("VPN", StringComparison.Ordinal));
        Assert.Contains("2 software devices", kept.Description);
    }

    [Fact]
    public async Task Present_devices_are_never_ghosts()
    {
        var inv = await Fixtures.InventoryAsync();
        Assert.All(GhostRules.Evaluate(inv.Devices, FixedClock.ScenarioNowUtc), g => Assert.False(g.Device.IsPresent));
        Assert.Single(GhostRules.Evaluate(inv.Devices, FixedClock.ScenarioNowUtc)); // the one disconnected entry in the full list
    }
}

public class ProblemDeviceTests
{
    [Theory]
    [InlineData(10, "can't start", ProblemKind.Problem)]
    [InlineData(28, "No driver", ProblemKind.Problem)]
    [InlineData(43, "reported a problem", ProblemKind.Problem)]
    [InlineData(22, "turned off", ProblemKind.Info)]
    [InlineData(45, "isn't plugged in", ProblemKind.Info)]
    public void Codes_have_a_plain_cause_and_fix(int code, string causeContains, ProblemKind kind)
    {
        var info = ProblemCodes.Describe(code);
        Assert.Contains(causeContains, info.Cause + " " + info.Name, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(info.Fix));
        Assert.Equal(kind, info.Kind);
    }

    [Fact]
    public void Disabled_is_not_a_problem_and_unknown_codes_still_read_plainly()
    {
        Assert.False(ProblemCodes.IsProblem(22));
        Assert.False(ProblemCodes.IsProblem(0));
        Assert.True(ProblemCodes.IsProblem(10));
        var unknown = ProblemCodes.Describe(777);
        Assert.Equal(ProblemKind.Problem, unknown.Kind);
        Assert.Contains("777", unknown.Name);
    }

    [Fact]
    public void Fixes_never_suggest_turning_off_signature_checks_or_editing_the_registry()
    {
        foreach (var p in ProblemCodes.All)
        {
            Assert.DoesNotContain("testsigning", p.Fix, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("regedit", p.Fix, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UpperFilters", p.Fix, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Never turn off", ProblemCodes.Describe(52).Fix);
    }

    [Fact]
    public async Task Problems_tab_splits_problems_missing_drivers_and_info()
    {
        var report = await Fixtures.ReportAsync();
        var found = report.Result.Group(DriverTabs.Problems, "found")!.Items;
        var missing = report.Result.Group(DriverTabs.Problems, "missing")!.Items;
        var info = report.Result.Group(DriverTabs.Problems, "info")!.Items;

        // pnputil's code 37 device (also in WMI: listed once) + a WMI-only code 43 device; code 45 (unplugged) is not a problem.
        Assert.Equal(new[] { "Fabrikam Fingerprint Reader", "Northwind Smart Card Reader" }, found.Select(i => i.Title).OrderBy(t => t));
        var reader = found.Single(i => i.Title.StartsWith("Fabrikam", StringComparison.Ordinal));
        Assert.Contains(ProblemCodes.Describe(37).Fix, reader.Reason);
        Assert.Equal("Code 37: Driver failed", reader.Details!["Problem"]);

        var none = Assert.Single(missing);
        Assert.Equal("Adatum USB Camera", none.Title);
        Assert.Contains(@"USB\VID_2E1F&PID_0C11&REV_0102", none.Details!["Hardware ids"]);
        Assert.Contains("Windows Update has a driver", none.Reason);

        var off = Assert.Single(info);
        Assert.Contains("TV Tuner", off.Title);
        Assert.Equal(Badge.Safe, off.Badge);

        Assert.All(found.Concat(missing).Concat(info), i => Assert.Empty(i.Actions));
        Assert.DoesNotContain(found.Concat(missing).Concat(info), i => i.Title == "HID Keyboard Device");
    }
}
