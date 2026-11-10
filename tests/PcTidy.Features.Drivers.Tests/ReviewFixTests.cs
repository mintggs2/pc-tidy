using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Model;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.Features.Drivers.Source;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>v2 review: DRV-1 (backup really comes first) and DRV-2 (no localized dates or status words decide anything).</summary>
public class ReviewFixTests
{
    private static readonly string Export = Path.Combine(Fixtures.DataRoot, "backups", "drivers", "2026-11-10");

    [Fact]
    public async Task Every_driver_change_in_a_plan_needs_the_backup_step()
    {
        var r = (await Fixtures.ReportAsync(options: new DriverScanOptions { IncludeImportantClasses = true })).Result;
        var picks = r.Group(DriverTabs.Store, "old")!.Items
            .Concat(r.Group(DriverTabs.Devices, "rollback")!.Items)
            .Concat(r.Group(DriverTabs.Updates, "windows-update")!.Items.Where(i => i.DisabledReason is null))
            .Concat(r.Group(DriverTabs.Ghosts, "removable")!.Items.Take(1))
            .ToList();

        var plan = DriverPlanBuilder.Build("Drivers", picks, Export).Plan!;

        var backup = plan.Items[0];
        Assert.True(DriverActions.IsExport(backup));
        Assert.Null(backup.DependsOn);
        var changes = plan.Items.Where(i => DriverActions.IsDriverChange(i) || DriverActions.IsDeviceRemoval(i)).ToList();
        Assert.NotEmpty(changes);
        Assert.Contains(changes, DriverActions.IsDeviceRemoval);
        Assert.All(changes, c => Assert.Equal(new[] { backup.Id }, c.DependsOn));
        // Every dependency points at a step earlier in the plan.
        var ids = plan.Items.Select(i => i.Id).ToList();
        Assert.All(plan.Items.Where(i => i.DependsOn is not null), i =>
            Assert.All(i.DependsOn!, d => Assert.True(ids.IndexOf(d) >= 0 && ids.IndexOf(d) < ids.IndexOf(i.Id))));
    }

    [Fact]
    public async Task Each_change_row_needs_its_own_backup_step()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        foreach (var item in r.AllItems.Where(i => i.Actions.Any(DriverActions.IsDriverChange)))
        {
            var backup = item.Actions[0];
            Assert.True(DriverActions.IsExport(backup), item.Title);
            Assert.All(item.Actions.Skip(1), a => Assert.Equal(new[] { backup.Id }, a.DependsOn));
        }
    }

    [Fact]
    public async Task Opening_pages_and_a_backup_alone_have_no_dependencies()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        var picks = r.AllItems.Where(i => i.DisabledReason is null && i.Actions.All(a => a.Kind == ActionKind.OpenExternal || DriverActions.IsExport(a)))
            .ToList();
        var plan = DriverPlanBuilder.Build("Drivers", picks, Export).Plan!;
        Assert.All(plan.Items, i => Assert.Null(i.DependsOn));
    }

    [Fact]
    public async Task Without_typed_dates_no_old_device_is_offered()
    {
        // The sample pnputil output has regional-text dates only and no machine.json (no typed facts).
        var inv = await Fixtures.InventoryAsync("sample");
        Assert.NotEmpty(inv.Disconnected);
        Assert.DoesNotContain(GhostRules.Evaluate(inv.Disconnected, FixedClock.ScenarioNowUtc), g => g.Verdict == GhostVerdict.Removable);
    }

    [Fact]
    public void A_day_month_date_can_no_longer_skip_the_30_day_wait()
    {
        // en-GB pnputil text for 3 Nov 2026 (9 days before "now"); the old parser read it as 11 Mar and offered the device.
        var device = new DeviceRecord
        {
            InstanceId = @"USB\VID_1234&PID_9999\5&FIXTURE&0&9",
            ClassName = "MEDIA",
            State = DeviceState.Disconnected,
            LastRemovalUtc = Parsing.PnpUtilXml.ParseFileTimeText("03/11/2026 10:00:00"),
        };
        Assert.Equal(GhostVerdict.UnknownLastSeen, GhostRules.Decide(device, FixedClock.ScenarioNowUtc));

        var typed = DriverInventory.WithFacts(device, new DeviceFacts(false, null, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc), null));
        Assert.Equal(GhostVerdict.SeenRecently, GhostRules.Decide(typed, FixedClock.ScenarioNowUtc));
    }

    [Fact]
    public void Presence_comes_from_windows_not_the_translated_status_word()
    {
        var german = new DeviceRecord { InstanceId = @"USB\VID_4C0F&PID_0201\6&FIXTURE&2&3", State = Parsing.PnpUtilXml.ParseState("Getrennt") };
        Assert.True(german.IsPresent); // unknown word: treated as present, so never a ghost on its own

        var gone = DriverInventory.WithFacts(german, new DeviceFacts(false, null, null, null));
        Assert.False(gone.IsPresent);

        var disconnectedText = german with { State = DeviceState.Disconnected };
        var actuallyHere = DriverInventory.WithFacts(disconnectedText, new DeviceFacts(true, null, null, null));
        Assert.True(actuallyHere.IsPresent);
    }

    [Fact]
    public void Same_version_packages_are_never_ordered_by_their_text_date()
    {
        var a = new DriverVersionInfo(new DateTime(2025, 3, 11), new Version(1, 2, 3, 4), "03/11/2025 1.2.3.4");
        var b = new DriverVersionInfo(new DateTime(2025, 11, 3), new Version(1, 2, 3, 4), "11/03/2025 1.2.3.4");
        Assert.Equal(0, StoreCleanupRules.Compare(a, b));
    }

    [Fact]
    public void The_configuration_manager_reader_fails_soft_for_unknown_ids()
    {
        // Read-only: cfgmgr32 lookups of ids that can't exist on any PC.
        var facts = DevNodeReader.Read(new[] { @"PCTIDY\NO_SUCH_DEVICE\0", "" }, CancellationToken.None);
        Assert.Empty(facts);
    }
}
