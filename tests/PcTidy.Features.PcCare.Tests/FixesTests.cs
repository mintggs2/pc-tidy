using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Planning;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>The Fixes tab's recipes: one-item plans with the right kinds, tools, args and flags, and honest text.</summary>
public class FixesTests
{
    [Fact]
    public void Every_recipe_builds_a_one_item_plan_with_a_fresh_item_and_full_card_text()
    {
        Assert.Equal(6, FixRecipes.All.Count);
        Assert.Equal(FixRecipes.All.Count, FixRecipes.All.Select(r => r.Id).Distinct().Count());
        foreach (var r in FixRecipes.All)
        {
            var plan = FixRecipes.PlanFor(r);
            var item = Assert.Single(plan.Items);
            Assert.Equal(PcCareActions.FeatureId, plan.FeatureId);
            Assert.Equal(PcCareActions.FeatureId, item.FeatureId);
            Assert.Equal(r.Title, plan.Title);
            Assert.NotEqual(item.Id, FixRecipes.PlanFor(r).Items[0].Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Description), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.WhatItChanges), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Undo), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.RunLabel), r.Id);
        }
    }

    [Theory]
    [InlineData(FixRecipes.WifiReset, "WlanSvc")]
    [InlineData(FixRecipes.BluetoothReset, "bthserv")]
    public void Wifi_and_Bluetooth_resets_restart_one_service_as_admin(string id, string service)
    {
        var i = FixRecipes.PlanFor(FixRecipes.Get(id)).Items[0];
        Assert.Equal(ActionKind.RunSystemTool, i.Kind);
        Assert.Equal(ToolIds.ServiceRestart, i.ToolId);
        Assert.Equal(new Dictionary<string, string> { ["name"] = service }, i.Args);
        Assert.True(i.NeedsAdmin);
        Assert.False(i.IsSystemChange);
        Assert.False(i.Reversible);
    }

    [Fact]
    public void Wifi_reset_carries_forget_and_router_guidance()
    {
        var r = FixRecipes.Get(FixRecipes.WifiReset);
        Assert.Contains(r.Guidance, g => g.Contains("Forget the network"));
        Assert.Contains(r.Guidance, g => g.Contains("router", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_Update_reset_is_an_admin_system_change_that_keeps_the_old_folders()
    {
        var r = FixRecipes.Get(FixRecipes.WindowsUpdateReset);
        var i = FixRecipes.PlanFor(r).Items[0];
        Assert.Equal(ToolIds.WindowsUpdateReset, i.ToolId);
        Assert.True(i.NeedsAdmin && i.IsSystemChange);
        Assert.False(i.Reversible);
        Assert.Contains("renames", r.WhatItChanges);
        Assert.Contains("kept", r.WhatItChanges);
        Assert.Contains("slower", r.WhatItChanges);
        Assert.Contains("renamed and kept", i.Description);
        Assert.Equal(new[] { "Needs admin", "Restore point first" }, FixRecipes.Flags(r));
    }

    [Fact]
    public void Store_reset_is_the_existing_wsreset_item()
    {
        var i = FixRecipes.PlanFor(FixRecipes.Get(FixRecipes.StoreReset)).Items[0];
        Assert.Equal(ActionKind.StoreRepair, i.Kind);
        Assert.Equal("true", i.Args!["wsreset"]);
        Assert.False(i.NeedsAdmin);
        Assert.Empty(FixRecipes.Flags(FixRecipes.Get(FixRecipes.StoreReset)));
    }

    [Fact]
    public void Installer_errors_reregister_msi_as_admin_and_say_run_as_admin()
    {
        var r = FixRecipes.Get(FixRecipes.InstallerErrors);
        var i = FixRecipes.PlanFor(r).Items[0];
        Assert.Equal(ToolIds.MsiReregister, i.ToolId);
        Assert.True(i.NeedsAdmin);
        Assert.False(i.IsSystemChange);
        Assert.Contains("2502", r.Title);
        Assert.Contains("2503", r.Title);
        Assert.Contains(r.Guidance, g => g.Contains("Run as administrator"));
    }

    [Fact]
    public void Game_launcher_repair_only_opens_an_https_help_page_and_covers_Epic_in_words()
    {
        var r = FixRecipes.Get(FixRecipes.GameLauncherRepair);
        var i = FixRecipes.PlanFor(r).Items[0];
        Assert.Equal(ActionKind.OpenExternal, i.Kind);
        Assert.StartsWith("https://help.steampowered.com/", i.Args!["target"]);
        Assert.True(i.Reversible);
        Assert.False(i.NeedsAdmin);
        Assert.Contains(r.Guidance, g => g.StartsWith("Steam:"));
        Assert.Contains(r.Guidance, g => g.StartsWith("Epic"));
        Assert.DoesNotContain("steam://", i.Args["target"]);
    }

    [Fact]
    public void Network_reset_isnt_duplicated_because_the_Network_tab_already_offers_it()
    {
        Assert.DoesNotContain(FixRecipes.All.Select(r => r.Preview), i => i.ToolId is ToolIds.WinsockReset or ToolIds.FlushDns);
        Assert.Contains("Network tab", FixRecipes.NetworkResetNote);
        var network = Scenario.Build().Group(Scanning.PcCareTabs.Network, "fixes")!;
        Assert.Contains(network.Items.SelectMany(x => x.Actions), a => a.ToolId == ToolIds.WinsockReset);
        Assert.Contains(network.Items.SelectMany(x => x.Actions), a => a.ToolId == ToolIds.FlushDns);
    }

    [Fact]
    public void No_recipe_makes_a_speed_or_FPS_claim()
    {
        foreach (var r in FixRecipes.All)
        {
            var text = string.Join(" ", new[] { r.Title, r.Description, r.WhatItChanges, r.Undo, r.Preview.Description }.Concat(r.Guidance));
            Assert.DoesNotContain("FPS", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("faster", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("boost", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_Fixes_and_Lag_check_tabs_are_panels_in_the_Fix_group()
    {
        Assert.True(Scanning.PcCareTabs.IsPanel(Scanning.PcCareTabs.Fixes));
        Assert.True(Scanning.PcCareTabs.IsPanel(Scanning.PcCareTabs.LagCheck));
        Assert.False(Scanning.PcCareTabs.IsPanel(Scanning.PcCareTabs.Network));
        var fix = Scanning.PcCareTabs.Groups.Single(g => g.Title == "Fix").TabIds;
        Assert.Contains(Scanning.PcCareTabs.LagCheck, fix);
        Assert.Contains(Scanning.PcCareTabs.Fixes, fix);
        // The lag check never adds rows (and so never plan items) to the scan.
        Assert.DoesNotContain(Scenario.Build().Result.Groups, g => Scanning.PcCareTabs.Of(g.Id) is Scanning.PcCareTabs.LagCheck or Scanning.PcCareTabs.Fixes);
    }

    [Fact]
    public void The_long_task_gate_also_waits_for_another_pages_run_but_never_blocks_a_scan()
    {
        // Folded into the app-wide RunGate (UI review M1); a private one here so no other test is affected.
        var shared = new PcTidy.UI.Flow.RunGate();
        var gate = new View.LongTaskGate(shared);
        int changes = 0;
        gate.Changed += () => changes++;
        using (shared.Begin("Clean: working on it"))
        {
            Assert.True(gate.IsBusy);
            Assert.False(gate.IsLocalBusy);
            Assert.Equal(View.LongTaskGate.BusyText, gate.Reason);
            Assert.False(gate.TryEnter("run"));
            Assert.False(gate.TryEnter("fix"));
            Assert.True(gate.TryEnter("scan", waitForShared: false)); // scanning only reads
            Assert.Equal("Available when the scan finishes", gate.Reason);
            gate.Exit("scan");
        }
        Assert.False(gate.IsBusy);
        Assert.True(changes >= 4); // the shared gate's flips reach the page too
        Assert.True(gate.TryEnter("lagcheck"));
        Assert.Equal("Available when the lag check finishes", gate.Reason);
        gate.Exit("lagcheck");
    }

    [Fact]
    public void The_long_task_gate_lets_one_task_run_at_a_time()
    {
        var gate = new View.LongTaskGate(new PcTidy.UI.Flow.RunGate());
        int changes = 0;
        gate.Changed += () => changes++;
        Assert.True(gate.TryEnter("fix"));
        Assert.False(gate.TryEnter("lagcheck"));
        Assert.Equal("fix", gate.Current);
        gate.Exit("lagcheck"); // not the owner: ignored
        Assert.True(gate.IsBusy);
        gate.Exit("fix");
        Assert.False(gate.IsBusy);
        Assert.Equal(2, changes);
        Assert.Equal("Available when the current task finishes", View.LongTaskGate.BusyText);
    }
}
