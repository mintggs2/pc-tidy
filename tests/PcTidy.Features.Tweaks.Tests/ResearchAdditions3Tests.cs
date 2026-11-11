using PcTidy.Catalog;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// Research additions, Update 3 (Community tweaks §9.7): measured gaming rows and the new bad-tweak checks. Every check runs
/// on a fake registry under HKCU\Software\PCTidyTest\tweaks, fixed probes and folders under testbed\research-additions-3.
/// Nothing on this PC is read for a verdict or changed.
/// </summary>
public class ResearchAdditions3Tests
{
    private const string Area = "research-additions-3";
    private const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    private static async Task<ScanResult> Find(TestRegistry reg, TestFolder folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null)
    {
        var env = TestEnv.Create(reg, folder.Path, probe, boot ?? BootConfigSnapshot.Empty);
        return await new BadTweakScanner(_ => env).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);
    }

    private static ScanItem Item(ScanResult r, string checkId) => Assert.Single(r.AllItems, i => i.Id == "badtweaks." + checkId);
    private static bool Has(ScanResult r, string checkId) => r.AllItems.Any(i => i.Id == "badtweaks." + checkId);

    // ------------------------------------------------------------------ gaming rows with the measured numbers

    [Theory]
    [InlineData("gaming.fso-off-global", "6.1 ms")]
    [InlineData("gaming.borderless-vs-fullscreen", "9 ms")]
    [InlineData("gaming.display-scaling", "5.9 ms")]
    [InlineData("gaming.raw-input-keyboard", "about 0 ms")]
    public void Measured_placebo_rows_are_greyed_with_their_numbers(string id, string number)
    {
        var t = TweakCatalog.Find(id)!;
        Assert.Equal(TweakVerdict.Placebo, t.Verdict);
        Assert.False(t.IsOffered);
        Assert.Contains("about 0 ms", t.Caveat);
        Assert.Contains(number, t.Caveat);
        Assert.Contains(TweakSources.Cs2LatencyTests, t.Sources);
    }

    [Fact]
    public async Task Placebo_rows_show_up_unselectable_with_the_measured_reason()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("rows", Area);
        var env = TestEnv.Create(reg, folder.Path);
        var outcome = await new TweaksScanner(_ => env).ScanRowsAsync(TestEnv.Context(folder.Path, expert: true), null, CancellationToken.None);
        foreach (var id in new[] { "gaming.fso-off-global", "gaming.borderless-vs-fullscreen", "gaming.display-scaling", "gaming.raw-input-keyboard" })
        {
            var row = Assert.Single(outcome.Rows, r => r.Tweak.Id == id);
            Assert.False(row.IsSelectable);
            Assert.Empty(row.Item.Actions);
            Assert.StartsWith("Doesn't help: Measured at about 0 ms", row.Item.DisabledReason);
        }
        // Fullscreen optimizations are no longer writable at all (they used to be an Expert row).
        var allowed = new TweakRegistryAllowances().Allowances().Select(a => a.ValueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("GameDVR_FSEBehaviorMode", allowed);
    }

    [Fact]
    public void Frame_cap_is_a_guidance_row_with_the_measured_gain()
    {
        var t = TweakCatalog.Find("gaming.fps-cap")!;
        Assert.Equal("Cap FPS a little below what your GPU can reach (GPU under about 95-97%)", t.Title);
        Assert.True(t.IsOffered);
        Assert.False(t.HasActions);
        Assert.IsType<GuidanceChange>(Assert.Single(t.Changes));
        Assert.Contains("cut input lag more than any setting", t.Effect);
        Assert.Contains("11 to 22 ms", t.Effect);
        Assert.Contains("Anti-Lag", t.Effect);
        Assert.Contains("Ultra Low Latency helped a little", t.Effect);
        Assert.Contains(TweakSources.FrameCapLatencyTest, t.Sources);
    }

    // ------------------------------------------------------------------ LSA protection

    [Fact]
    public async Task Lsa_protection_off_is_an_information_card_that_opens_windows_security()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("lsa", Area);
        reg.Lm(@"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL", 0);
        var item = Item(await Find(reg, folder), "security.lsa-protection-off");
        Assert.Empty(item.Actions); // apps don't set this: Windows Security does
        Assert.Contains("Local Security Authority protection", item.Details!["How to fix"]);
        var link = BadTweakCatalog.Find("security.lsa-protection-off")!.Signals.SelectMany(s => s.Fixes).OfType<GuidanceFix>().Single();
        Assert.Equal("ms-settings:windowsdefender", link.SettingsUri);
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL"));

        // On (1 or 2) or not set: nothing to say.
        reg.Lm(@"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL", 2);
        Assert.False(Has(await Find(reg, folder), "security.lsa-protection-off"));
    }

    // ------------------------------------------------------------------ Defender with only real-time protection left

    private static DefenderProtection Hollowed(bool tamper = false) => new(AntivirusEnabled: true, TamperProtected: tamper, CloudReporting: 0)
    {
        RealTimeProtection = true, BehaviorMonitoring = false, DownloadScanning = false, ScriptScanningDisabled = true, SampleSubmission = 2,
    };

    [Fact]
    public async Task Defender_with_only_real_time_left_is_information_plus_windows_security()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("defender", Area);
        var r = await Find(reg, folder, new FixedSystemProbe { Defender = Hollowed() });
        var item = Item(r, "security.defender-only-realtime");
        Assert.Empty(item.Actions);
        Assert.Contains("Real-time protection is on.", item.Details!["Found"]);
        Assert.Contains("Behaviour monitoring is off.", item.Details["Found"]);
        Assert.Contains("Tamper Protection is off.", item.Details["Found"]);
        Assert.Contains("Set-MpPreference -DisableBehaviorMonitoring $false", item.Details["How to fix"]);
        Assert.Equal(BadTweakSeverity.High, BadTweakCatalog.Find("security.defender-only-realtime")!.Severity);
        // The smaller "Tamper or cloud off" card isn't shown as well.
        Assert.False(Has(r, "security.defender-protection-reduced"));
    }

    [Fact]
    public async Task Defender_only_realtime_needs_real_time_on_defender_in_charge_and_several_switches_off()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("defender", Area);
        // Another antivirus is in charge: Defender's switches don't apply.
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { Defender = Hollowed(), ThirdPartyAntivirus = new[] { "Example AV" } }), "security.defender-only-realtime"));
        // Real-time is off too: the policy finding covers that, not this one.
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { Defender = Hollowed() with { RealTimeProtection = false } }), "security.defender-only-realtime"));
        // Only cloud protection off: the smaller card instead.
        var mild = new DefenderProtection(true, true, 0) { RealTimeProtection = true, BehaviorMonitoring = true, DownloadScanning = true, ScriptScanningDisabled = false, SampleSubmission = 1 };
        var r = await Find(reg, folder, new FixedSystemProbe { Defender = mild });
        Assert.False(Has(r, "security.defender-only-realtime"));
        Assert.True(Has(r, "security.defender-protection-reduced"));
    }

    // ------------------------------------------------------------------ Windows Update paused or deferred by policy

    [Fact]
    public async Task Update_pause_and_deferral_policies_are_offered_for_removal_with_a_resume_pointer()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("wu", Area);
        reg.Text(CoreHive.LocalMachine, WindowsUpdatePolicy, "PauseQualityUpdatesStartTime", "2026-01-01");
        reg.Text(CoreHive.LocalMachine, WindowsUpdatePolicy, "PauseFeatureUpdatesStartTime", "2026-01-01");
        reg.Lm(WindowsUpdatePolicy, "DeferFeatureUpdates", 1);
        reg.Lm(WindowsUpdatePolicy, "DeferFeatureUpdatesPeriodInDays", 365);

        var item = Item(await Find(reg, folder), "update.paused-by-policy");
        var removed = item.Actions.Select(a => a.Registry!.ValueName).ToList();
        Assert.Equal(new[] { "PauseQualityUpdatesStartTime", "PauseFeatureUpdatesStartTime", "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays" }, removed);
        Assert.All(item.Actions, a =>
        {
            Assert.Equal(ActionKind.RegistryDeleteValue, a.Kind); // delete-only: nothing is ever written there
            Assert.True(a.Reversible && a.NeedsAdmin && a.IsSystemChange);
            Assert.StartsWith(RegistryRedirect.TestArea, a.Registry!.KeyPath);
        });
        Assert.Contains("Resume updates", item.Details!["How to fix"]);
        Assert.False(item.DefaultSelected);
        // A companion value that isn't there isn't planned (DeferQualityUpdatesPeriodInDays).
        Assert.DoesNotContain("DeferQualityUpdatesPeriodInDays", removed);
    }

    [Fact]
    public void Update_policy_removals_are_delete_only_allowances_for_the_real_policy_key()
    {
        foreach (var value in new[] { "PauseQualityUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdates", "PauseFeatureUpdates",
                     "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays", "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays" })
        {
            var a = RegistryCatalog.Find(CoreHive.LocalMachine, WindowsUpdatePolicy, value);
            Assert.True(a is not null, value);
            Assert.True(a!.AllowDelete && a.NeedsAdmin, value);
            Assert.Empty(a.AllowedData);
            Assert.Contains(TweakRegistryAllowances.KeyOf(CoreHive.LocalMachine, WindowsUpdatePolicy, value), TweakRegistryAllowances.DeleteOnly);
        }
    }

    [Fact]
    public async Task Update_policies_the_finder_names_are_not_listed_again_as_other_policies()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("wu", Area);
        reg.Lm(WindowsUpdatePolicy, "DeferQualityUpdates", 1);
        var r = await Find(reg, folder);
        Assert.True(Has(r, "update.paused-by-policy"));
        Assert.False(Has(r, "policy.other"));
    }

    // ------------------------------------------------------------------ Safe Mode left on

    [Fact]
    public async Task A_safeboot_flag_is_removed_to_start_windows_normally_with_the_manual_way_shown_too()
    {
        // v3.1: "Start Windows normally again" (bcdedit.deletevalue safeboot); the full check is in FinderV31Tests.
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("safeboot", Area);
        var boot = new BootConfigSnapshot(new[] { new BootElement(BadTweakCatalog.SafeBootElement, null, 0) });
        var item = Item(await Find(reg, folder, boot: boot), "boot.safeboot-left-on");
        var step = Assert.Single(item.Actions);
        Assert.Equal(ToolIds.BcdDeleteValue, step.ToolId);
        Assert.Equal("safeboot", step.Args!["name"]);
        Assert.Contains("bcdedit /deletevalue {current} safeboot", item.Details!["How to fix"]);
        Assert.Contains("msconfig", item.Details["How to fix"]);
        Assert.False(BadTweakCatalog.Find("boot.safeboot-left-on")!.Flags.HasFlag(BadTweakFlags.BitLocker));
        Assert.False(Has(await Find(reg, folder, boot: BootConfigSnapshot.Empty), "boot.safeboot-left-on"));
    }

    // ------------------------------------------------------------------ realtime priority (undocumented value: guidance)

    [Fact]
    public async Task Realtime_priority_is_offered_for_removal_through_the_patterned_allowance_only()
    {
        // v3.1: the value is still undocumented ("verify"), so it has no exact allowance; a delete-only pattern covers it.
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("ifeo", Area);
        reg.Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\game.exe\PerfOptions", "CpuPriorityClass", 4);
        reg.Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\other.exe\PerfOptions", "CpuPriorityClass", 3);
        var item = Item(await Find(reg, folder), "scheduler.realtime-priority");
        Assert.Contains("game.exe: PerfOptions → CpuPriorityClass = 4 (Realtime)", item.Details!["Found"]);
        Assert.DoesNotContain("other.exe", item.Details["Found"]);
        var step = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.RegistryDeleteValue, step.Kind);
        Assert.True(step.Reversible);
        Assert.DoesNotContain(new TweakRegistryAllowances().Allowances(), a => a.ValueName == "CpuPriorityClass");
    }

    // ------------------------------------------------------------------ disabled system devices

    [Fact]
    public async Task Disabled_system_devices_are_listed_with_their_ids_as_guidance()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("devices", Area);
        var probe = new FixedSystemProbe { DisabledSystemDevices = new[] { new DisabledDevice("High precision event timer", @"ACPI\PNP0103\0") } };
        var item = Item(await Find(reg, folder, probe), "windows.system-devices-disabled");
        Assert.Contains(@"High precision event timer is disabled (ACPI\PNP0103\0)", item.Details!["Found"]);
        Assert.Contains("Enable device", item.Details["How to fix"]);
        Assert.Empty(item.Actions); // System-class devices are only explained (manual steps); see FinderV31Tests
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { DisabledSystemDevices = Array.Empty<DisabledDevice>() }), "windows.system-devices-disabled"));
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { DisabledSystemDevices = null }), "windows.system-devices-disabled"));
    }

    // ------------------------------------------------------------------ stray power plans

    private static readonly Guid ImportedPlan = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_stray_active_power_plan_offers_the_switch_back_to_balanced()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("power", Area);
        var plans = new PowerPlanInfo(ImportedPlan, "Quick CPU scheme", new[] { BadTweakCatalog.BalancedPlan, ImportedPlan });
        var item = Item(await Find(reg, folder, new FixedSystemProbe { PowerPlans = plans }), "cpu.stray-power-plan");
        var step = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.PowerPlanSet, step.Kind);
        Assert.Equal(BadTweakCatalog.BalancedPlan.ToString("D"), step.Args!["schemeGuid"]);
        Assert.True(step.Reversible);
        Assert.Contains("\"Quick CPU scheme\" is the active power plan", item.Details!["Found"]);
        Assert.Contains("PC maker", item.Details["Good to know"]);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task Power_plans_are_left_alone_when_they_are_windows_own_or_balanced_is_missing()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("power", Area);
        var highPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { PowerPlans = new PowerPlanInfo(highPerformance, "x", new[] { highPerformance }) }), "cpu.stray-power-plan"));
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { PowerPlans = null }), "cpu.stray-power-plan"));

        // No Balanced plan on this PC: the finding stays, but only as guidance.
        var item = Item(await Find(reg, folder, new FixedSystemProbe { PowerPlans = new PowerPlanInfo(ImportedPlan, null, new[] { ImportedPlan }) }), "cpu.stray-power-plan");
        Assert.Empty(item.Actions);
        Assert.Contains("A custom plan is the active power plan", item.Details!["Found"]);
    }

    [Fact]
    public async Task The_power_plan_switch_passes_the_real_safety_guard()
    {
        using var reg = new TestRegistry("ra3");
        using var folder = new TestFolder("power", Area);
        var plans = new PowerPlanInfo(ImportedPlan, null, new[] { BadTweakCatalog.BalancedPlan, ImportedPlan });
        var env = TestEnv.Create(reg, folder.Path, new FixedSystemProbe { PowerPlans = plans });
        var ctx = TestEnv.Context(folder.Path);
        var r = await new BadTweakScanner(_ => env).ScanAsync(ctx, null, CancellationToken.None);
        var step = Assert.Single(Item(r, "cpu.stray-power-plan").Actions);
        Assert.True(ctx.Safety.Check(step).Allowed);
    }
}
