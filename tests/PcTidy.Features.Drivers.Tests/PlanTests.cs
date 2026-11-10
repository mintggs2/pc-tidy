using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Scanning;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>Plan order (backup first), merging, one-device-at-a-time, and "no forbidden switches anywhere".</summary>
public class PlanTests
{
    private static readonly string Export = Path.Combine(Fixtures.DataRoot, "backups", "drivers", "2026-11-10");

    [Fact]
    public async Task Every_driver_change_plan_starts_with_one_export()
    {
        var r = (await Fixtures.ReportAsync(options: new DriverScanOptions { IncludeImportantClasses = true })).Result;
        var picks = r.Group(DriverTabs.Store, "old")!.Items
            .Concat(r.Group(DriverTabs.Store, "important")!.Items)
            .Concat(r.Group(DriverTabs.Devices, "rollback")!.Items)
            .Concat(r.Group(DriverTabs.Updates, "windows-update")!.Items.Where(i => i.DisabledReason is null))
            .ToList();

        var plan = DriverPlanBuilder.Build("Drivers", picks, Export).Plan!;

        Assert.True(DriverActions.IsExport(plan.Items[0]));
        Assert.Single(plan.Items, DriverActions.IsExport);
        Assert.Equal(DriverActions.FeatureId, plan.FeatureId);
        var order = plan.Items.Skip(1).Select(i => i.ToolId).ToList();
        Assert.Equal(order.OrderBy(Rank).ToList(), order);
        Assert.Single(plan.Items, DriverActions.IsWindowsUpdateInstall);
        Assert.Equal("8d41e0b6-2c7f-4a59-9e13-6b0fa2d4c871,c25f7a09-4e1b-4d83-b6a2-0f9e3d5c1b74",
            plan.Items.Single(DriverActions.IsWindowsUpdateInstall).Args!["updateIds"]);
        Assert.Equal(4, plan.Items.Count(i => i.ToolId == ToolIds.PnpDeleteDriver));
        Assert.Equal(2, plan.Items.Count(i => i.ToolId == ToolIds.DriverRollback));
        Assert.Equal(plan.Items.Count, plan.Items.Select(i => i.Id).Distinct().Count());
    }

    private static int Rank(string? toolId) => toolId switch
    {
        ToolIds.DriverRollback => 1,
        ToolIds.WindowsUpdateInstallDrivers => 2,
        ToolIds.PnpAddDrivers => 3,
        ToolIds.PnpDeleteDriver => 4,
        ToolIds.PnpRemoveDevice => 5,
        _ => 6,
    };

    [Fact]
    public async Task Each_change_row_carries_its_own_backup_step_first()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        foreach (var item in r.AllItems.Where(i => i.Actions.Any(DriverActions.IsDriverChange)))
        {
            Assert.True(DriverActions.IsExport(item.Actions[0]), item.Title);
            Assert.Matches(@"\\backups\\drivers\\\d{4}-\d{2}-\d{2}$", item.Actions[0].Args!["folder"]);
            Assert.StartsWith(Fixtures.DataRoot, item.Actions[0].Args!["folder"]);
        }
    }

    [Fact]
    public async Task Old_devices_are_removed_one_at_a_time_without_an_export()
    {
        var ghosts = (await Fixtures.ReportAsync()).Result.Group(DriverTabs.Ghosts, "removable")!.Items;

        var two = DriverPlanBuilder.Build("Old devices", ghosts, Export);
        Assert.Null(two.Plan);
        Assert.Contains("one at a time", two.Problem);

        var one = DriverPlanBuilder.Build("Old devices", ghosts.Take(1), Export).Plan!;
        var step = Assert.Single(one.Items);
        Assert.Equal(ToolIds.PnpRemoveDevice, step.ToolId);
    }

    [Fact]
    public async Task Opening_pages_needs_no_backup_and_disabled_rows_never_count()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        var pages = r.Group(DriverTabs.Updates, "official-pages")!.Items;
        var plan = DriverPlanBuilder.Build("Pages", pages, Export).Plan!;
        Assert.All(plan.Items, i => Assert.Equal(ActionKind.OpenExternal, i.Kind));

        var disabled = r.Group(DriverTabs.Store, "important")!.Items;
        Assert.Null(DriverPlanBuilder.Build("Nothing", disabled, Export).Plan);
    }

    [Fact]
    public void Restoring_todays_backup_backs_up_to_a_separate_folder_first()
    {
        var restore = new ScanItem
        {
            Title = "Put back", Reason = "test", Badge = Badge.Check,
            Actions = new[] { DriverActions.ExportAll(Export), DriverActions.RestoreFromBackup(Export, "today") },
        };
        var plan = DriverPlanBuilder.Build("Restore", new[] { restore }, Export).Plan!;

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(Export + "-2", plan.Items[0].Args!["folder"]);
        Assert.Equal(Export, plan.Items[1].Args!["folder"]);
        Assert.Equal(ToolIds.PnpAddDrivers, plan.Items[1].ToolId);
    }

    [Fact]
    public async Task Backup_now_is_a_plan_of_just_the_export()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        var backup = r.Group(DriverTabs.Devices, "backup")!.Items.Single(i => i.Title.StartsWith("Back up", StringComparison.Ordinal));
        var plan = DriverPlanBuilder.Build("Backup", new[] { backup }, Export).Plan!;
        Assert.True(DriverActions.IsExport(Assert.Single(plan.Items)));
    }

    [Fact]
    public async Task No_action_ever_uses_force_uninstall_or_a_bulk_filter()
    {
        var r = (await Fixtures.ReportAsync(options: new DriverScanOptions { IncludeImportantClasses = true })).Result;
        var all = r.AllActions().ToList();
        Assert.NotEmpty(all);
        string[] banned = { "/force", "/uninstall", "/subtree", "/deviceid", "/class", "/bus", "/reboot", "resetbase" };
        foreach (var a in all)
        {
            foreach (var value in (a.Args ?? new()).Values.Append(a.Description))
                foreach (var b in banned)
                    Assert.DoesNotContain(b, value, StringComparison.OrdinalIgnoreCase);
            if (a.Kind == ActionKind.RunSystemTool) Assert.True(a.NeedsAdmin, a.Description);
        }
    }

    [Fact]
    public async Task Change_rows_are_check_unticked_and_explained()
    {
        var r = (await Fixtures.ReportAsync()).Result;
        Assert.All(r.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description), g.Id));
        Assert.All(r.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Title));
        Assert.All(r.AllItems, i => Assert.False(i.DefaultSelected, i.Title));
        foreach (var i in r.AllItems.Where(i => i.Actions.Any(a => !a.Reversible)))
            Assert.Equal(Badge.Check, i.Badge);
        Assert.All(r.AllActions().Where(DriverActions.IsDriverChange), a => Assert.True(a.IsSystemChange));
    }

    [Fact]
    public async Task Rollback_is_offered_only_where_the_previous_package_is_still_there()
    {
        var rows = (await Fixtures.ReportAsync()).Result.Group(DriverTabs.Devices, "rollback")!.Items;
        Assert.Equal(new[] { "Roll back Litware Studio Webcam", "Roll back NVIDIA Example Graphics 7700" }, rows.Select(i => i.Title).OrderBy(t => t));
        var gpu = rows.Single(i => i.Title.Contains("NVIDIA", StringComparison.Ordinal));
        Assert.Contains("oem117.inf", gpu.Details!["Previous driver"]);
        Assert.Equal(ToolIds.DriverRollback, gpu.Actions[1].ToolId);
        var cam = rows.Single(i => i.Title.Contains("Webcam", StringComparison.Ordinal));
        Assert.Contains("oem289.inf", cam.Details!["Previous driver"]);

        // Without oem117.inf in the store there is nothing to go back to.
        var gone = await Fixtures.ReportAsync(tweak: inv => inv with { Packages = inv.Packages.Where(p => p.PublishedName != "oem117.inf").ToList() });
        Assert.DoesNotContain(gone.Result.Group(DriverTabs.Devices, "rollback")!.Items, i => i.Title.Contains("NVIDIA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_with_an_unsafe_id_is_shown_but_not_pickable()
    {
        var wu = (await Fixtures.ReportAsync()).Result.Group(DriverTabs.Updates, "windows-update")!.Items;
        Assert.Equal(3, wu.Count);
        var odd = wu.Single(i => i.Title == "Strange update");
        Assert.NotNull(odd.DisabledReason);
        Assert.Empty(odd.Actions);
        Assert.All(wu.Where(i => i.DisabledReason is null), i => Assert.Equal(Badge.Check, i.Badge));
    }

    [Theory]
    [InlineData(ToolIds.PnpDeleteDriver, "Remove 1 old package")]
    [InlineData(ToolIds.PnpRemoveDevice, "Remove this device")]
    [InlineData(ToolIds.DriverRollback, "Roll back 1 device")]
    [InlineData(ToolIds.PnpExportDrivers, "Back up drivers")]
    public void Button_text_names_the_change(string tool, string expected)
    {
        var item = new PlanItem { Kind = ActionKind.RunSystemTool, FeatureId = "drivers", Description = "x", ToolId = tool, Args = new() { ["inf"] = "oem1.inf" } };
        Assert.Equal(expected, View.DriversView.VerbFor(new[] { item }));
    }
}
