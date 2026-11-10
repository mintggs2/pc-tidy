using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Scanning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>What the scan offers: the right ToolIds and Args, honest flags, and nothing ever ticked for you.</summary>
public class ResultTests
{
    [Fact]
    public void Nothing_is_ever_ticked_and_every_row_and_group_explains_itself()
    {
        var r = Scenario.Build();
        Assert.NotEmpty(r.Result.Groups);
        Assert.All(r.Result.AllItems, i => Assert.False(i.DefaultSelected, i.Title));
        Assert.All(r.Result.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Title));
        Assert.All(r.Result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description), g.Id));
        Assert.All(r.Result.Groups, g => Assert.Contains(T.Of(g.Id), T.All.Select(t => t.Id)));
        // Every row with an action that can't be undone says so on its badge (Check or Permanent), never Safe.
        Assert.All(r.Result.AllItems.Where(i => i.Actions.Any(a => !a.Reversible)), i => Assert.NotEqual(Badge.Safe, i.Badge));
        Assert.All(r.AllActions(), a => Assert.Equal(PcCareActions.FeatureId, a.FeatureId));
    }

    [Fact]
    public void Winget_updates_use_the_package_id_and_never_accept_agreements_by_themselves()
    {
        var r = Scenario.Build();
        var items = r.Group(T.Updates, "apps")!.Items;
        Assert.Equal(2, items.Count);
        var git = items.Single(i => i.Title == "Git").Actions.Single();
        Assert.Equal(ActionKind.RunSystemTool, git.Kind);
        Assert.Equal(ToolIds.WingetUpgrade, git.ToolId);
        Assert.Equal(new Dictionary<string, string> { ["id"] = "Git.Git" }, git.Args);
        Assert.False(git.Reversible);
        Assert.Contains("couldn't be read safely", r.TabNotes[T.Updates]);
    }

    [Theory]
    [InlineData(WingetState.NotInstalled, "isn't on this PC")]
    [InlineData(WingetState.Unreadable, "winget upgrade")]
    [InlineData(WingetState.Failed, "accept its terms")]
    public void Winget_problems_become_a_friendly_note_without_rows(WingetState state, string text)
    {
        var r = Scenario.Build(Scenario.OwnerLike() with { Winget = new WingetFacts(state, Array.Empty<WingetUpgrade>()) });
        Assert.Null(r.Group(T.Updates, "apps"));
        Assert.Contains(text, r.TabNotes[T.Updates]);
    }

    [Fact]
    public void Windows_repair_runs_DISM_then_SFC_as_an_admin_system_change()
    {
        var repair = Scenario.Build().Group(T.Repair, "windows")!.Items[0];
        Assert.Equal(new[] { ToolIds.DismRestoreHealth, ToolIds.SfcScan }, repair.Actions.Select(a => a.ToolId));
        Assert.All(repair.Actions, a => Assert.True(a.NeedsAdmin && a.IsSystemChange && !a.Reversible));
        Assert.True(repair.NeedsAdmin);
        Assert.Contains("min", repair.Detail);
    }

    [Fact]
    public void Store_fix_offers_wsreset_re_register_and_the_troubleshooter()
    {
        var items = Scenario.Build().Group(T.Repair, "store")!.Items;
        Assert.Equal("true", items[0].Actions[0].Args!["wsreset"]);
        Assert.Equal(ActionKind.StoreRepair, items[1].Actions[0].Kind);
        Assert.Equal("Microsoft.WindowsStore_8wekyb3d8bbwe", items[1].Actions[0].Args!["packageFamilyName"]);
        Assert.Equal("ms-settings:troubleshoot", items[2].Actions[0].Args!["target"]);
    }

    [Fact]
    public void Winsock_reset_needs_admin_and_a_restart_and_warns_about_VPNs()
    {
        var fixes = Scenario.Build().Group(T.Network, "fixes")!.Items;
        Assert.Equal(ToolIds.FlushDns, fixes[0].Actions[0].ToolId);
        Assert.False(fixes[0].Actions[0].NeedsAdmin);
        var winsock = fixes[^1];
        Assert.Equal(ToolIds.WinsockReset, winsock.Actions[0].ToolId);
        Assert.True(winsock.NeedsRestart && winsock.Actions[0].NeedsAdmin);
        Assert.Equal("true", winsock.Actions[0].Args!["needsRestart"]);
        Assert.Contains("VPN", winsock.Reason);
    }

    [Fact]
    public void The_DNS_test_is_a_suggestion_only()
    {
        var r = Scenario.Build();
        var rows = r.Group(T.Network, "dns")!.Items;
        Assert.All(rows, i => Assert.Empty(i.Actions));
        Assert.Equal("Cloudflare (1.1.1.1)", rows[0].Title);
        Assert.Contains("Cloudflare", r.TabNotes[T.Network]);
        Assert.Contains("doesn't change it", r.TabNotes[T.Network]);
    }

    [Fact]
    public void A_fast_current_DNS_needs_no_change()
        => Assert.Contains("No change needed", PcCareResultBuilder.DnsSuggestion(new[]
        {
            new DnsServerResult("192.168.1.1", "Your DNS", true, 11, 10, 10),
            new DnsServerResult("1.1.1.1", "Cloudflare", false, 10, 10, 10),
        }));

    [Fact]
    public void Defender_off_by_policy_is_explained_calmly_points_to_Tweaks_and_offers_no_scan()
    {
        var r = Scenario.Build();
        Assert.Equal(Analysis.AvSituation.DefenderOffByPolicy, r.Antivirus!.Situation);
        var note = r.TabNotes[T.Security];
        Assert.Contains("turned off by a policy", note);
        Assert.Contains("Tweaks", note);
        Assert.DoesNotContain("!", note);
        Assert.DoesNotContain(r.AllActions(), a => a.ToolId == ToolIds.DefenderQuickScan);
        var row = r.Group(T.Security, "antivirus")!.Items.Single();
        Assert.Equal("ms-settings:windowsdefender", row.Actions.Single().Args!["target"]);
        Assert.Equal(ActionKind.OpenExternal, row.Actions.Single().Kind);
    }

    [Fact]
    public void Quick_scan_is_offered_only_when_Defender_is_the_active_antivirus()
    {
        var on = Scenario.OwnerLike() with
        {
            Security = Scenario.OwnerLike().Security! with
            {
                Policy = DefenderPolicy.None,
                Defender = new DefenderStatus { AntivirusEnabled = true, RealTimeProtectionEnabled = true, RunningMode = "Normal", LastQuickScanUtc = FixedClock.Now.AddDays(-2) },
                Products = new[] { new AntivirusProduct("Windows Defender", 0x061100, "MsMpeng.exe") },
            },
        };
        var scan = Scenario.Build(on).AllActions().Single(a => a.ToolId == ToolIds.DefenderQuickScan);
        Assert.False(scan.Reversible);
    }

    [Fact]
    public void Nothing_anywhere_ever_touches_Defender_settings_or_exclusions()
    {
        var all = Scenario.Build().AllActions().ToList();
        Assert.DoesNotContain(all, a => a.Kind is ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue or ActionKind.RegistryDeleteKey or ActionKind.ServiceSetStartType);
        Assert.DoesNotContain(all, a => (a.Description + string.Join(" ", a.Args?.Values ?? Enumerable.Empty<string>())).Contains("exclusion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Vulnerable_drivers_are_shown_with_a_link_to_Apps()
    {
        var row = Assert.Single(Scenario.Build().Group(T.Security, "drivers")!.Items);
        Assert.StartsWith("WinRing0x64.sys", row.Title);
        Assert.Equal("ms-settings:appsfeatures", row.Actions.Single().Args!["target"]);
        Assert.Equal(Badge.Check, row.Badge);
    }

    [Fact]
    public void Browser_fixes_only_open_help_pages_or_Settings()
    {
        var r = Scenario.Build();
        var browserActions = r.Result.Groups.Where(g => T.Of(g.Id) == T.Browsers).SelectMany(g => g.Items).SelectMany(i => i.Actions).ToList();
        Assert.NotEmpty(browserActions);
        Assert.All(browserActions, a =>
        {
            Assert.Equal(ActionKind.OpenExternal, a.Kind);
            Assert.Matches("^(https://|ms-settings:)", a.Args!["target"]);
        });
        Assert.All(r.Group(T.Browsers, "policies")!.Items, i => Assert.NotNull(i.DisabledReason));
        Assert.Contains("chrome://settings/reset", r.Group(T.Browsers, "settings")!.Items[0].Reason);
    }

    [Fact]
    public void Crashes_get_plain_causes_and_point_at_the_graphics_driver()
    {
        var r = Scenario.Build();
        var blue = r.Group(T.Crashes, "bluescreens")!.Items;
        Assert.Equal(2, blue.Count); // BugCheck 1001 and the Kernel-Power 41 that carried the same code
        Assert.All(blue, b => Assert.Equal("0x00000116", b.CurrentState));
        Assert.Contains("graphics driver", blue[0].Reason);
        Assert.Contains(Catalog.PcCare.CrashFixArea.GraphicsDriver, r.CrashFixAreas);
        Assert.Single(r.Group(T.Crashes, "shutdowns")!.Items);
        var game = Assert.Single(r.Group(T.Crashes, "apps")!.Items); // old.exe is outside the 30 days
        Assert.Equal("game.exe", game.Title);
        Assert.Contains("Drivers", game.Reason);
        Assert.Single(r.Group(T.Crashes, "dumps")!.Items);
        Assert.All(r.Result.Groups.Where(g => T.Of(g.Id) == T.Crashes).SelectMany(g => g.Items), i => Assert.Empty(i.Actions));
    }

    [Fact]
    public void Minidumps_that_need_admin_say_so()
    {
        var s = Scenario.OwnerLike();
        s = s with { Crashes = s.Crashes! with { Minidumps = Array.Empty<MinidumpInfo>(), MinidumpAccess = ReadAccess.NeedsAdmin } };
        Assert.Contains("need admin", Scenario.Build(s).TabNotes[T.Crashes]);
    }

    [Fact]
    public void Restore_points_keep_the_newest_and_deleting_others_is_permanent()
    {
        var r = Scenario.Build();
        var rows = r.Group(T.Restore, "points")!.Items;
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[0].DisabledReason); // newest
        Assert.Empty(rows[0].Actions);
        foreach (var row in rows.Skip(1))
        {
            var a = row.Actions.Single();
            Assert.Equal(ActionKind.RestorePointDelete, a.Kind);
            Assert.True(uint.TryParse(a.Args!["sequenceNumber"], out _));
            Assert.False(a.Reversible);
            Assert.True(a.NeedsAdmin);
            Assert.Equal(Badge.Permanent, row.Badge);
        }
        var create = r.Group(T.Restore, "create")!.Items.Single().Actions.Single();
        Assert.Equal(ToolIds.RestorePointCreate, create.ToolId);
        Assert.False(string.IsNullOrWhiteSpace(create.Args!["description"]));
    }

    [Fact]
    public void Restore_space_is_lowered_to_10_percent_and_never_below_5()
    {
        // 100 GB of a 500 GB drive = 20%: suggest 10%; 30 GB used fits in 50 GB, so it's Check, not Permanent.
        var row = Scenario.Build().Group(T.Restore, "space")!.Items.Single(i => i.Actions.Count > 0);
        var a = row.Actions.Single();
        Assert.Equal(ToolIds.ShadowStorageResize, a.ToolId);
        Assert.Equal("10", a.Args!["percent"]);
        Assert.Equal(Badge.Check, row.Badge);
        Assert.Throws<ArgumentOutOfRangeException>(() => PcCareActions.ResizeRestoreSpace(4, "C:\\"));
        Assert.Equal(5, PcCareResultBuilder.SuggestedPercent(new ShadowStorage("C:\\", 0, 10L << 30, 500L << 30)));
        Assert.Null(PcCareResultBuilder.SuggestedPercent(new ShadowStorage("C:\\", 0, 50L << 30, 500L << 30)));
    }

    [Fact]
    public void Shrinking_below_what_restore_points_use_is_permanent()
    {
        var s = Scenario.OwnerLike();
        s = s with { Restore = s.Restore! with { Storage = new ShadowStorage("C:\\", 80L << 30, 100L << 30, 500L << 30) } };
        var row = Scenario.Build(s).Group(T.Restore, "space")!.Items.Single(i => i.Actions.Count > 0);
        Assert.Equal(Badge.Permanent, row.Badge);
        Assert.Contains("oldest restore points are deleted", row.Reason);
    }

    [Fact]
    public void Restore_points_that_need_admin_still_offer_making_one()
    {
        var s = Scenario.OwnerLike() with { Restore = new RestoreFacts(ReadAccess.NeedsAdmin, Array.Empty<RestorePoint>(), null, null) };
        var r = Scenario.Build(s);
        Assert.Null(r.Group(T.Restore, "points"));
        Assert.NotNull(r.Group(T.Restore, "create"));
        Assert.Contains("needs admin", r.TabNotes[T.Restore]);
    }

    [Fact]
    public void Wifi_rows_never_offer_the_connected_network()
    {
        var rows = Scenario.Build().Group(T.Leftovers, "wifi")!.Items;
        var home = rows.Single(i => i.Title == "Home");
        Assert.NotNull(home.DisabledReason);
        Assert.Empty(home.Actions);
        var hotel = rows.Single(i => i.Title == "Hotel Guest").Actions.Single();
        Assert.Equal(ActionKind.WlanProfileDelete, hotel.Kind);
        Assert.Equal("Hotel Guest", hotel.Args!["profileName"]);
        Assert.Equal(Scenario.WifiCard, Guid.Parse(hotel.Args["interfaceGuid"]));
        Assert.All(rows, i => Assert.Equal(Badge.Check, i.Badge));
    }

    [Fact]
    public void Only_the_deleted_accounts_profile_is_offered_by_sid()
    {
        var rows = Scenario.Build().Group(T.Leftovers, "profiles")!.Items;
        Assert.Equal(2, rows.Count); // gone + work account (shown disabled); yours and system profiles hidden
        var gone = rows.Single(i => i.Title == "oldkid");
        var a = gone.Actions.Single();
        Assert.Equal(ActionKind.UserProfileDelete, a.Kind);
        Assert.Equal(Scenario.GoneSid, a.Args!["sid"]);
        Assert.False(a.Reversible);
        Assert.Equal(Badge.Permanent, gone.Badge);
        Assert.NotNull(rows.Single(i => i.Title == "work.user").DisabledReason);
        Assert.DoesNotContain(r(rows), s => s == Scenario.CurrentSid);

        static IEnumerable<string> r(IEnumerable<ScanItem> items) => items.SelectMany(i => i.Actions).Select(x => x.Args!["sid"]);
    }

    [Fact]
    public void Optional_features_are_situational_with_a_way_back()
    {
        var rows = Scenario.Build().Group(T.Leftovers, "features")!.Items;
        var smb = rows[0];
        Assert.Equal("SMB 1.0 file sharing", smb.Title); // security benefit first
        var a = smb.Actions.Single();
        Assert.Equal(ActionKind.OptionalFeatureRemove, a.Kind);
        Assert.Equal(new Dictionary<string, string> { ["name"] = "SMB1Protocol", ["kind"] = "feature" }, a.Args);
        Assert.True(a.Reversible);
        var steps = rows.Single(i => i.Title == "Steps Recorder").Actions.Single();
        Assert.Equal("capability", steps.Args!["kind"]);
        Assert.False(steps.Reversible);
        Assert.All(rows.Where(i => i.Actions.Any(x => x.Kind == ActionKind.OptionalFeatureRemove)), i => Assert.Contains("add it back", i.Reason));
        Assert.DoesNotContain(rows, i => i.Title.Contains("NetFx3") || i.Title.Contains(".NET"));
        Assert.Single(rows, i => i.Title.Contains("de-DE"));
    }

    [Fact]
    public void Free_space_wipe_is_offered_on_hard_drives_only_with_the_SSD_explained()
    {
        var rows = Scenario.Build().Group(T.Shredder, "freespace")!.Items;
        var hdd = rows.Single(i => i.Title.Contains("D:"));
        var a = hdd.Actions.Single();
        Assert.Equal(ToolIds.CipherWipeFreeSpace, a.ToolId);
        Assert.Equal("D:\\", a.Args!["drive"]);
        Assert.False(a.Reversible);
        Assert.Equal(Badge.Permanent, hdd.Badge);
        var ssd = rows.Single(i => i.Title.Contains("C:"));
        Assert.Empty(ssd.Actions);
        Assert.Contains("TRIM", ssd.Reason);
        Assert.NotNull(ssd.DisabledReason);
    }

    [Fact]
    public void The_report_is_written_to_the_reports_folder_as_html_and_markdown()
    {
        var c = Scenario.Context(dataRoot: @"X:\PC Tidy");
        var r = Scenario.Build(c: c);
        var saves = r.Group(T.Report, "export")!.Items.SelectMany(i => i.Actions).ToList();
        Assert.Equal(new[] { @"X:\PC Tidy\reports\health-2026-11-10.html", @"X:\PC Tidy\reports\health-2026-11-10.md" },
            saves.Select(a => a.DestinationPath).Select(p => p!.Replace(FixedClock.Now.ToLocalTime().ToString("yyyy-MM-dd"), "2026-11-10")));
        Assert.All(saves, a => Assert.Equal(ActionKind.WriteTextFile, a.Kind));
        Assert.All(saves, a => Assert.True(a.Reversible));
        Assert.StartsWith("<!doctype html>", saves[0].Args!["content"]);
        Assert.StartsWith("# PC health report", saves[1].Args!["content"]);
    }

    [Fact]
    public void An_empty_pc_gets_friendly_notes_and_no_errors()
    {
        var r = PcCareResultBuilder.Build(new PcCareSnapshot { TakenUtc = FixedClock.Now }, Scenario.Context(drives: Array.Empty<Core.DriveDetails>()));
        foreach (var tab in new[] { T.Updates, T.Security, T.Browsers, T.Crashes, T.Restore })
            Assert.False(string.IsNullOrWhiteSpace(r.TabNotes[tab]), tab);
        Assert.All(r.TabNotes.Values, n => Assert.DoesNotContain("Exception", n));
        Assert.NotNull(r.Group(T.Repair, "windows")); // Windows' own tools are always there
        Assert.NotNull(r.Group(T.Report, "export"));
    }
}

public class PlanBuilderTests
{
    private static ScanItem Row(PcCareScanReport r, string tab, string group, Func<ScanItem, bool>? pick = null)
        => r.Group(tab, group)!.Items.First(pick ?? (i => i.DisabledReason is null && i.Actions.Count > 0));

    [Fact]
    public void Agreements_are_accepted_only_after_the_user_ticked_the_box()
    {
        var r = Scenario.Build();
        var picked = r.Group(T.Updates, "apps")!.Items;
        var without = PcCarePlanBuilder.Build("App updates", picked, new PcCarePageChoices()).Plan!;
        Assert.All(without.Items, i => Assert.False(i.Args!.ContainsKey("acceptAgreements")));
        var with = PcCarePlanBuilder.Build("App updates", picked, new PcCarePageChoices { AcceptAgreements = true }).Plan!;
        Assert.All(with.Items, i => Assert.Equal("true", i.Args!["acceptAgreements"]));
        Assert.All(with.Items, i => Assert.Equal(ToolIds.WingetUpgrade, i.ToolId));
        Assert.Equal("Update 2 apps", PcCarePlanBuilder.VerbFor(with.Items));
        // The scan's own rows are untouched (the flag is added to copies).
        Assert.All(picked.SelectMany(i => i.Actions), a => Assert.False(a.Args!.ContainsKey("acceptAgreements")));
    }

    [Fact]
    public void Repair_and_SFC_only_together_run_SFC_once_after_DISM()
    {
        var r = Scenario.Build();
        var plan = PcCarePlanBuilder.Build("Windows repair", r.Group(T.Repair, "windows")!.Items.Reverse(), new PcCarePageChoices()).Plan!;
        Assert.Equal(new[] { ToolIds.DismRestoreHealth, ToolIds.SfcScan }, plan.Items.Select(i => i.ToolId));
        Assert.True(plan.RequiresRestorePoint);
        Assert.True(plan.RequiresAdmin);
    }

    [Fact]
    public void Winsock_reset_always_runs_last()
    {
        var r = Scenario.Build();
        var plan = PcCarePlanBuilder.Build("Network fixes", r.Group(T.Network, "fixes")!.Items.Reverse(), new PcCarePageChoices()).Plan!;
        Assert.Equal(new[] { ToolIds.FlushDns, ToolIds.WinsockReset }, plan.Items.Select(i => i.ToolId));
    }

    [Fact]
    public void The_same_help_page_opens_once()
    {
        var s = Scenario.OwnerLike();
        var r = Scenario.Build(s);
        var picks = r.Result.Groups.Where(g => T.Of(g.Id) == T.Browsers).SelectMany(g => g.Items).Where(i => i.DisabledReason is null).ToList();
        var plan = PcCarePlanBuilder.Build("Browser check", picks, new PcCarePageChoices()).Plan!;
        var targets = plan.Items.Select(i => i.Args!["target"]).ToList();
        Assert.Equal(targets.Distinct().Count(), targets.Count);
    }

    [Fact]
    public void Shredding_and_wiping_need_the_pages_I_understand_box()
    {
        var r = Scenario.Build();
        var wipe = Row(r, T.Shredder, "freespace");
        var refused = PcCarePlanBuilder.Build("File shredder", new[] { wipe }, new PcCarePageChoices());
        Assert.Null(refused.Plan);
        Assert.Contains("I understand", refused.Problem);
        var ok = PcCarePlanBuilder.Build("File shredder", new[] { wipe }, new PcCarePageChoices { ShredAcknowledged = true });
        Assert.NotNull(ok.Plan);
        Assert.All(ok.Plan!.Items, i => Assert.False(i.Reversible));
    }

    [Fact]
    public void Disabled_rows_never_reach_a_plan()
    {
        var r = Scenario.Build();
        var newest = r.Group(T.Restore, "points")!.Items[0];
        Assert.Equal("Tick something first.", PcCarePlanBuilder.Build("Restore points", new[] { newest }, new PcCarePageChoices()).Problem);
    }

    [Fact]
    public void Only_one_restore_space_size_at_a_time()
    {
        var a = new ScanItem { Title = "a", Reason = "r", Actions = new[] { PcCareActions.ResizeRestoreSpace(5, "C:\\") } };
        var b = new ScanItem { Title = "b", Reason = "r", Actions = new[] { PcCareActions.ResizeRestoreSpace(10, "C:\\") } };
        Assert.NotNull(PcCarePlanBuilder.Build("Restore points", new[] { a, b }, new PcCarePageChoices()).Problem);
    }
}
