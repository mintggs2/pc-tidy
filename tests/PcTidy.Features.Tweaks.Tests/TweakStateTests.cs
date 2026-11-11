using Microsoft.Win32;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.UI.Flow;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>Current-state reading against HKCU\Software\PCTidyTest\tweaks\… (the scanner's registry root is redirected there).</summary>
public class TweakStateTests
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static async Task<TweakScanOutcome> Scan(TestRegistry reg, TestFolder folder, bool expert = false, string? preset = null,
        FixedSystemProbe? probe = null, IReadOnlyDictionary<string, string>? apps = null, int build = 26100, GpuVendor gpu = GpuVendor.Unknown)
    {
        var env = TestEnv.Create(reg, folder.Path, probe, apps: apps, build: build, gpu: gpu);
        var scanner = new TweaksScanner(_ => env);
        return await scanner.ScanRowsAsync(TestEnv.Context(folder.Path, expert, preset), null, CancellationToken.None);
    }

    private static TweakRow Row(TweakScanOutcome o, string id) => Assert.Single(o.Rows, r => r.Tweak.Id == id);

    [Fact]
    public async Task Unset_value_is_available_and_the_plan_writes_the_redirected_key()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        var row = Row(await Scan(reg, folder), "ui.open-this-pc");

        Assert.Equal(TweakStatus.Available, row.State.Status);
        Assert.Equal("Available", row.Item.CurrentState);
        Assert.Null(row.Item.DisabledReason);
        var action = Assert.Single(row.Item.Actions);
        Assert.Equal(ActionKind.RegistrySetValue, action.Kind);
        Assert.Equal(CoreHive.CurrentUser, action.Registry!.Hive);
        Assert.Equal(reg.Physical(CoreHive.CurrentUser, Adv), action.Registry.KeyPath);
        Assert.StartsWith(@"Software\PCTidyTest\tweaks\", action.Registry.KeyPath);
        Assert.Equal("LaunchTo", action.Registry.ValueName);
        Assert.Equal("1", action.Registry.Data);
        Assert.True(action.IsSystemChange && action.Reversible && !action.NeedsAdmin);
        Assert.Equal("tweaks", action.FeatureId);
    }

    [Fact]
    public async Task Three_states_available_applied_and_differs_show_the_exact_key()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        reg.Cu(Adv, "HideFileExt", 1);   // Windows default → Available
        reg.Cu(Adv, "Hidden", 1);        // what the tweak sets → Already applied
        reg.Cu(Adv, "TaskbarAl", 7);     // something else → Differs
        var o = await Scan(reg, folder);

        var ext = Row(o, "ui.file-extensions");
        Assert.Equal(TweakStatus.Available, ext.State.Status);
        Assert.Equal("0", Assert.Single(ext.Item.Actions).Registry!.Data);

        var hidden = Row(o, "ui.hidden-files");
        Assert.Equal(TweakStatus.Applied, hidden.State.Status);
        Assert.Equal("Already applied", hidden.Item.CurrentState);
        Assert.Equal("Already set on this PC.", hidden.Item.DisabledReason);
        Assert.Empty(hidden.Item.Actions);

        var left = Row(o, "ui.taskbar-left");
        Assert.Equal(TweakStatus.Differs, left.State.Status);
        Assert.StartsWith("Differs", left.Item.CurrentState);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced → TaskbarAl", left.Item.Details!["Change"]);
        Assert.Contains("Now: 7", left.Item.Details["Change"]);
        Assert.Single(left.Item.Actions);
    }

    [Fact]
    public async Task Default_that_already_matches_is_applied_and_a_wrong_type_is_left_alone()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        reg.Text(CoreHive.CurrentUser, Adv, "UseCompactMode", "1"); // text instead of a number
        var o = await Scan(reg, folder);

        // Background recording is off by default, so with nothing set the tweak is already applied.
        Assert.Equal(TweakStatus.Applied, Row(o, "gaming.background-recording-off").State.Status);

        var compact = Row(o, "ui.compact-mode");
        Assert.Equal(TweakStatus.Blocked, compact.State.Status);
        Assert.Contains("leaves it alone", compact.Item.DisabledReason);
        Assert.Empty(compact.Item.Actions);
    }

    [Fact]
    public async Task Windowed_games_setting_is_merged_into_the_existing_string()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        reg.Text(CoreHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "VRROptimizeEnable=1;AutoHDREnable=1;");
        var o = await Scan(reg, folder);

        var action = Assert.Single(Row(o, "gaming.windowed-optimizations").Item.Actions);
        Assert.Equal("VRROptimizeEnable=1;AutoHDREnable=1;SwapEffectUpgradeEnable=1;", action.Registry!.Data);
        Assert.Equal(TweakStatus.Applied, Row(o, "gaming.vrr-windowed").State.Status);
        Assert.Equal("SwapEffectUpgradeEnable=1;", TokenList.Merge("SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1"));
    }

    [Fact]
    public async Task Placebo_rows_are_greyed_harmful_rows_never_appear()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        reg.Lm(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26);
        var o = await Scan(reg, folder);

        var placebo = o.Rows.Where(r => r.Tweak.Verdict == TweakVerdict.Placebo).ToList();
        Assert.NotEmpty(placebo);
        Assert.All(placebo, r => { Assert.Matches("^(Doesn't help|No evidence it helps): ", r.Item.DisabledReason!); Assert.Empty(r.Item.Actions); Assert.False(r.Item.DefaultSelected); });
        Assert.DoesNotContain(o.Rows, r => r.Tweak.Verdict == TweakVerdict.Harmful);
        // The placebo row still shows what's on this PC.
        Assert.Contains("38 (0x26)", Row(o, "gaming.priority-separation").Item.Details!["Change"]);
    }

    [Fact]
    public async Task Expert_rows_need_expert_mode_and_never_unlock_in_optimize_all()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        reg.Service("DiagTrack", start: 2);

        var normal = Row(await Scan(reg, folder), "privacy.diagtrack-manual");
        Assert.StartsWith("Expert mode only", normal.Item.DisabledReason);
        Assert.Empty(normal.Item.Actions);

        var expert = Row(await Scan(reg, folder, expert: true), "privacy.diagtrack-manual");
        Assert.Null(expert.Item.DisabledReason);
        var step = Assert.Single(expert.Item.Actions);
        Assert.Equal(ActionKind.ServiceSetStartType, step.Kind);
        Assert.Equal(ServiceStart.Manual, step.ServiceStartType);
        Assert.True(step.NeedsAdmin);
        Assert.Equal(Badge.Check, expert.Item.Badge);
        Assert.False(expert.Item.DefaultSelected);

        var optimizeAll = Row(await Scan(reg, folder, expert: true, preset: OptimizePresets.Gamer), "privacy.diagtrack-manual");
        Assert.NotNull(optimizeAll.Item.DisabledReason);

        // Already Disabled is stricter than Manual: nothing to do (PC Tidy never switches it back on from here).
        reg.Service("DiagTrack", start: 4);
        Assert.Equal(TweakStatus.Applied, Row(await Scan(reg, folder, expert: true), "privacy.diagtrack-manual").State.Status);
    }

    [Fact]
    public async Task Only_proven_preset_rows_start_ticked_and_groups_match_optimize_all()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        var env = TestEnv.Create(reg, folder.Path, gpu: GpuVendor.Nvidia);
        var result = await new TweaksScanner(_ => env).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);

        var ticked = result.AllItems.Where(i => i.DefaultSelected).ToList();
        Assert.NotEmpty(ticked);
        Assert.All(ticked, i =>
        {
            var t = TweakCatalog.Find(i.Id["tweaks.".Length..])!;
            Assert.True(t.Verdict == TweakVerdict.Proven && t.PresetDefault && t.Preset != TweakPreset.None, t.Id);
            Assert.Equal(Badge.Safe, i.Badge);
        });
        Assert.Contains(result.Groups, g => g.Id == "tweaks.safe-gaming");
        Assert.Contains(result.Groups, g => g.Id == "tweaks.privacy-basic");
        Assert.Contains(result.Groups, g => g.Id == "tweaks.clean-explorer");

        var gaming = result.Groups.Single(g => g.Id == "tweaks.safe-gaming");
        var windowed = gaming.Items.Single(i => i.Id == "tweaks.gaming.windowed-optimizations");
        var pointer = gaming.Items.Single(i => i.Id == "tweaks.gaming.pointer-precision-off");
        Assert.True(OptimizePresets.ShouldTick(OptimizePresets.Gamer, "tweaks", gaming, windowed));
        Assert.False(OptimizePresets.ShouldTick(OptimizePresets.Gamer, "tweaks", gaming, pointer)); // opt-in inside the preset
        Assert.False(OptimizePresets.ShouldTick(OptimizePresets.Safe, "tweaks", gaming, windowed));
        Assert.All(result.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
    }

    [Fact]
    public async Task Rows_adapt_to_the_pc_build_gpu_cpu_and_installed_apps()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");

        var win10 = await Scan(reg, folder, build: 19045);
        Assert.DoesNotContain(win10.Rows, r => r.Tweak.Id is "ui.classic-context-menu" or "ui.taskbar-left" or "ui.end-task");
        Assert.DoesNotContain(win10.Rows, r => r.Tweak.Id == "gaming.nvidia-low-latency"); // GPU unknown
        Assert.DoesNotContain(win10.Rows, r => r.Tweak.Id == "privacy.copilot-app");          // not installed
        Assert.DoesNotContain(win10.Rows, r => r.Tweak.Id == "privacy.office-telemetry");     // no Office key

        var x3d = await Scan(reg, folder, probe: new FixedSystemProbe { CpuKind = CpuKind.DualCcdX3D }, gpu: GpuVendor.Nvidia,
            apps: new Dictionary<string, string> { ["Microsoft.Copilot"] = "Microsoft.Copilot_1.0.0.0_x64__8wekyb3d8bbwe" });
        Assert.DoesNotContain(x3d.Rows, r => r.Tweak.Id == "power.core-parking-off");
        Assert.Contains(x3d.Rows, r => r.Tweak.Id == "gaming.nvidia-low-latency");
        var copilot = Row(x3d, "privacy.copilot-app");
        var remove = Assert.Single(copilot.Item.Actions);
        Assert.Equal(ActionKind.AppxRemoveForUser, remove.Kind);
        Assert.Equal("Microsoft.Copilot_1.0.0.0_x64__8wekyb3d8bbwe", remove.Args!["packageFullName"]);
        Assert.False(remove.Reversible);
        Assert.Equal(Badge.Permanent, copilot.Item.Badge);
        Assert.False(copilot.Item.DefaultSelected);

        var plain = await Scan(reg, folder);
        Assert.Contains(plain.Rows, r => r.Tweak.Id == "power.core-parking-off");
    }

    [Fact]
    public async Task Optional_feature_state_comes_from_the_probe()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        var on = Row(await Scan(reg, folder, probe: new FixedSystemProbe { OptionalFeatures = new Dictionary<string, int> { ["SMB1Protocol"] = 1 } }), "network.smb1-off");
        var step = Assert.Single(on.Item.Actions);
        Assert.Equal(ActionKind.OptionalFeatureRemove, step.Kind);
        Assert.Equal("SMB1Protocol", step.Args!["name"]);

        var off = Row(await Scan(reg, folder, probe: new FixedSystemProbe { OptionalFeatures = new Dictionary<string, int> { ["SMB1Protocol"] = 2 } }), "network.smb1-off");
        Assert.Equal(TweakStatus.Applied, off.State.Status);
        var unknown = Row(await Scan(reg, folder), "network.smb1-off");
        Assert.NotNull(unknown.Item.DisabledReason);
    }

    [Fact]
    public async Task Policy_tweaks_carry_the_managed_label_and_need_admin()
    {
        using var reg = new TestRegistry("state");
        using var folder = new TestFolder("state");
        var row = Row(await Scan(reg, folder), "privacy.web-results-in-start");
        Assert.Contains("Managed by your organization", row.Item.Reason);
        Assert.True(row.Item.NeedsAdmin);
        Assert.True(Assert.Single(row.Item.Actions).NeedsAdmin);
    }

    [Fact]
    public void Redirect_only_points_into_the_test_area()
    {
        Assert.Throws<ArgumentException>(() => RegistryRedirect.ToTestRoot(@"Software\Microsoft"));
        Assert.Throws<ArgumentException>(() => RegistryRedirect.ToTestRoot(@"Software\PCTidyTest\..\Microsoft"));
        var r = RegistryRedirect.ToTestRoot(@"Software\PCTidyTest\tweaks\x");
        Assert.Equal((CoreHive.CurrentUser, @"Software\PCTidyTest\tweaks\x\HKLM\SOFTWARE\A"), r.Map(CoreHive.LocalMachine, @"SOFTWARE\A"));
        Assert.Equal((CoreHive.LocalMachine, @"SOFTWARE\A"), RegistryRedirect.None.Map(CoreHive.LocalMachine, @"SOFTWARE\A"));
    }
}
