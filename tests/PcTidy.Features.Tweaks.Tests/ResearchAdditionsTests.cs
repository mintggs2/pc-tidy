using Microsoft.Win32;
using PcTidy.Catalog;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;
using Win32Hive = Microsoft.Win32.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// The community-tweaks re-check additions (docs/reviews/research-additions.md) and review item TWK-1: every finder check
/// runs on a fake registry under HKCU\Software\PCTidyTest and fixed probes. Nothing on this PC is changed.
/// </summary>
public class ResearchAdditionsTests
{
    private const string Defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    private const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private static async Task<ScanResult> Find(TestRegistry reg, TestFolder folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null)
    {
        var env = TestEnv.Create(reg, folder.Path, probe, boot ?? BootConfigSnapshot.Empty);
        return await new BadTweakScanner(_ => env).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);
    }

    private static ScanItem Item(ScanResult r, string checkId) => Assert.Single(r.AllItems, i => i.Id == "badtweaks." + checkId);
    private static bool Has(ScanResult r, string checkId) => r.AllItems.Any(i => i.Id == "badtweaks." + checkId);

    // ------------------------------------------------------------------ Defender policies

    [Fact]
    public async Task Any_leftover_defender_policy_is_listed_and_offered_for_removal()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        reg.Dword(CoreHive.LocalMachine, Defender + @"\Exclusions\Paths", @"C:\Games", 0);
        reg.Lm(Defender + @"\Real-Time Protection", "DisableScriptScanning", 1);
        reg.Lm(Defender, "DisableAntiSpyware", 1); // named by the catalog: removed once, not listed twice

        var result = await Find(reg, folder);
        var off = Item(result, "security.defender-off");
        var offRemoved = off.Actions.Where(a => a.Kind == ActionKind.RegistryDeleteValue).Select(a => a.Registry!.ValueName).ToList();
        Assert.Equal(new[] { "DisableAntiSpyware" }, offRemoved); // the exclusion isn't "Defender is off" (DEF-1)
        Assert.Contains("only while you play", off.Reason);

        var item = Item(result, "security.defender-weakened");
        var removed = item.Actions.Where(a => a.Kind == ActionKind.RegistryDeleteValue).Select(a => a.Registry!.ValueName).ToList();
        Assert.DoesNotContain("DisableAntiSpyware", removed); // named by the catalog: removed once, not listed twice
        Assert.Contains(@"C:\Games", removed);
        Assert.Contains("DisableScriptScanning", removed);
        Assert.Contains(item.Actions, a => a.Description.Contains("turns script scanning off"));
        Assert.All(item.Actions, a => Assert.True(a.Reversible && a.NeedsAdmin && a.IsSystemChange));
        Assert.All(item.Actions, a => Assert.StartsWith(RegistryRedirect.TestArea, a.Registry!.KeyPath)); // plan writes where the scan read
        Assert.False(item.DefaultSelected);
        Assert.False(off.DefaultSelected);
    }

    /// <summary>DEF-1: policies that ADD protection are listed as kept and never offered for removal, and alone they fire nothing.</summary>
    [Fact]
    public async Task Protective_defender_policies_are_kept_and_never_offered_for_removal()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        const string asrGuid = "56a863a9-875e-4185-98a7-b882c64b5ce5";
        reg.Set(CoreHive.LocalMachine, Defender + @"\Windows Defender Exploit Guard\ASR\Rules", asrGuid, "1", RegistryValueKind.String);
        reg.Lm(Defender + @"\Windows Defender Exploit Guard\ASR", "ExploitGuard_ASR_Rules", 1);
        reg.Lm(Defender + @"\Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess", 1);
        reg.Lm(Defender + @"\Windows Defender Exploit Guard\Network Protection", "EnableNetworkProtection", 1);
        reg.Lm(Defender, "PUAProtection", 1);
        reg.Lm(Defender + @"\MpEngine", "MpCloudBlockLevel", 2);
        reg.Lm(Defender + @"\Spynet", "SpynetReporting", 2);
        reg.Lm(Defender + @"\Spynet", "SubmitSamplesConsent", 1);
        reg.Lm(Defender + @"\Real-Time Protection", "DisableRealtimeMonitoring", 0); // protection kept ON
        reg.Lm(Defender + @"\Signature Updates", "SignatureUpdateInterval", 4);

        var only = await Find(reg, folder);
        Assert.False(Has(only, "security.defender-off"));
        Assert.False(Has(only, "security.defender-weakened"));

        // With one switched-off protection next to them, only that one is offered; the others are listed as kept.
        reg.Lm(Defender + @"\Real-Time Protection", "DisableIOAVProtection", 1);
        reg.Set(CoreHive.LocalMachine, Defender + @"\Threats\ThreatSeverityDefaultAction", "5", "6", RegistryValueKind.String);
        var item = Item(await Find(reg, folder), "security.defender-weakened");
        var removed = item.Actions.Select(a => a.Registry!.ValueName).ToList();
        Assert.Contains("5", removed);                 // "Ignore" for severe threats
        Assert.DoesNotContain(asrGuid, removed);
        foreach (var keptName in new[] { "ExploitGuard_ASR_Rules", "EnableControlledFolderAccess", "EnableNetworkProtection", "PUAProtection", "MpCloudBlockLevel", "SignatureUpdateInterval" })
        {
            Assert.DoesNotContain(keptName, removed);
            Assert.Contains($"→ {keptName} (", item.Details!["Found"]);
        }
        Assert.Contains("Kept, these add protection", item.Details!["Found"]);
        // DisableIOAVProtection is named by the "Defender is off" check, which removes it there.
        Assert.Contains(Item(await Find(reg, folder), "security.defender-off").Actions, a => a.Registry!.ValueName == "DisableIOAVProtection");
    }

    [Theory]
    [InlineData(@"Real-Time Protection", "DisableRealtimeMonitoring", "1", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Real-Time Protection", "DisableRealtimeMonitoring", "0", DefenderPolicyEffect.Protects)]
    [InlineData(@"Real-Time Protection", "DisableBehaviorMonitoring", "1", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Real-Time Protection", "DisableOnAccessProtection", "1", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Real-Time Protection", "RealtimeScanDirection", "1", DefenderPolicyEffect.Weakens)]
    [InlineData("", "DisableAntiSpyware", "1", DefenderPolicyEffect.Weakens)]
    [InlineData("", "PUAProtection", "1", DefenderPolicyEffect.Protects)]
    [InlineData("", "PUAProtection", "0", DefenderPolicyEffect.Protects)]
    [InlineData("Spynet", "SpynetReporting", "0", DefenderPolicyEffect.Weakens)]
    [InlineData("Spynet", "SpynetReporting", "2", DefenderPolicyEffect.Protects)]
    [InlineData("Spynet", "SubmitSamplesConsent", "2", DefenderPolicyEffect.Weakens)]
    [InlineData("Spynet", "SubmitSamplesConsent", "3", DefenderPolicyEffect.Protects)]
    [InlineData(@"Exclusions\Paths", @"C:\Games", "0", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Exclusions\Processes", "game.exe", "0", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Windows Defender Exploit Guard\ASR\Rules", "56a863a9-875e-4185-98a7-b882c64b5ce5", "1", DefenderPolicyEffect.Protects)]
    [InlineData(@"Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess", "1", DefenderPolicyEffect.Protects)]
    [InlineData(@"Windows Defender Exploit Guard\Network Protection", "EnableNetworkProtection", "1", DefenderPolicyEffect.Protects)]
    [InlineData(@"MpEngine", "MpCloudBlockLevel", "2", DefenderPolicyEffect.Protects)]
    [InlineData(@"Signature Updates", "SignatureUpdateInterval", "4", DefenderPolicyEffect.Protects)]
    [InlineData(@"Threats\ThreatSeverityDefaultAction", "5", "6", DefenderPolicyEffect.Weakens)]
    [InlineData(@"Threats\ThreatSeverityDefaultAction", "5", "2", DefenderPolicyEffect.Protects)]
    [InlineData(@"Scan", "AvgCPULoadFactor", "30", DefenderPolicyEffect.Other)]
    public void Defender_policies_are_sorted_by_name_and_data(string key, string name, string data, DefenderPolicyEffect expected)
        => Assert.Equal(expected, DefenderPolicyEffects.Classify(key, name, data).Effect);

    [Fact]
    public void Leftover_defender_policies_get_delete_only_allowances_for_the_real_policy_key()
    {
        var area = $@"{RegistryRedirect.TestArea}\tweaks\allow-{Guid.NewGuid():N}";
        using var hkcu = RegistryKey.OpenBaseKey(Win32Hive.CurrentUser, RegistryView.Registry64);
        try
        {
            using (var k = hkcu.CreateSubKey(area + @"\Exclusions\Paths")) k!.SetValue(@"D:\Stuff", 0, RegistryValueKind.DWord);
            using (var k = hkcu.CreateSubKey(area)) k!.SetValue("DisableAntiSpyware", 1, RegistryValueKind.DWord);

            var allowances = new DefenderPolicyAllowances(Win32Hive.CurrentUser, area).Allowances().ToList();
            var leftover = Assert.Single(allowances);
            Assert.Equal(CoreHive.LocalMachine, leftover.Hive);
            Assert.Equal(DefenderPolicyAllowances.PolicyKey + @"\Exclusions\Paths", leftover.KeyPath);
            Assert.Equal(@"D:\Stuff", leftover.ValueName);
            Assert.Empty(leftover.AllowedData); // delete-only: nothing can ever be written there
            Assert.True(leftover.AllowDelete && leftover.NeedsAdmin);
        }
        finally
        {
            hkcu.DeleteSubKeyTree(area, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Tamper_and_cloud_protection_off_are_information_with_a_windows_security_link()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var off = new DefenderProtection(AntivirusEnabled: true, TamperProtected: false, CloudReporting: 0);
        var item = Item(await Find(reg, folder, new FixedSystemProbe { Defender = off }), "security.defender-protection-reduced");
        Assert.Empty(item.Actions); // apps can't set Tamper Protection: PC Tidy only explains and opens Windows Security
        Assert.Contains("Tamper Protection is off", item.Details!["Found"]);
        Assert.Contains("Cloud-delivered protection is off", item.Details["Found"]);
        var link = BadTweakCatalog.Find("security.defender-protection-reduced")!.Signals.SelectMany(s => s.Fixes).OfType<GuidanceFix>().Single();
        Assert.Equal("windowsdefender://threat-settings", link.SettingsUri); // v3.1: straight to Virus & threat protection settings

        // Another antivirus in charge, or Defender's switches on: nothing to say.
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { Defender = off, ThirdPartyAntivirus = new[] { "Example AV" } }), "security.defender-protection-reduced"));
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { Defender = new DefenderProtection(true, true, 2) }), "security.defender-protection-reduced"));
    }

    // ------------------------------------------------------------------ accounts and restore points

    [Fact]
    public async Task System_protection_off_is_found_and_explains_pc_tidys_own_safety_net()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var item = Item(await Find(reg, folder, new FixedSystemProbe { SystemProtectionOn = false, RestorePointCount = 0 }), "security.restore-points-off");
        Assert.Contains("System Protection is off", item.Details!["Found"]);
        Assert.Contains("no restore points", item.Details["Found"]);
        Assert.Contains("PC Tidy also makes a restore point", item.Reason);
        Assert.Empty(item.Actions);
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { SystemProtectionOn = true, RestorePointCount = 0 }), "security.restore-points-off"));
    }

    // ------------------------------------------------------------------ paging file and memory

    [Fact]
    public async Task Fixed_pagefile_is_reset_to_let_windows_manage_but_system_managed_is_left_alone()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        reg.Set(CoreHive.LocalMachine, MemoryManagement, "PagingFiles", new[] { @"C:\pagefile.sys 32768 32768" }, RegistryValueKind.MultiString);
        var item = Item(await Find(reg, folder, new FixedSystemProbe { TotalMemoryBytes = 16L << 30 }), "storage.fixed-pagefile");
        Assert.Contains("32,768 MB", item.Details!["Found"].Replace('\u00A0', ','));
        Assert.Contains("2×", item.Details["Found"]);
        var set = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.RegistrySetValue, set.Kind);
        Assert.Equal(RegistryKind.MultiString, set.Registry!.Kind);
        Assert.Equal(@"?:\pagefile.sys", set.Registry.Data);
        Assert.True(set.Reversible);

        reg.Set(CoreHive.LocalMachine, MemoryManagement, "PagingFiles", new[] { @"C:\pagefile.sys 0 0" }, RegistryValueKind.MultiString);
        Assert.False(Has(await Find(reg, folder), "storage.fixed-pagefile"));
        reg.Set(CoreHive.LocalMachine, MemoryManagement, "PagingFiles", new[] { @"?:\pagefile.sys" }, RegistryValueKind.MultiString);
        Assert.False(Has(await Find(reg, folder), "storage.fixed-pagefile"));
    }

    [Fact]
    public async Task Clearing_the_pagefile_at_shutdown_is_set_back_to_the_windows_default()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        reg.Lm(MemoryManagement, "ClearPageFileAtShutdown", 1);
        var set = Assert.Single(Item(await Find(reg, folder), "storage.clear-pagefile-at-shutdown").Actions);
        Assert.Equal("ClearPageFileAtShutdown", set.Registry!.ValueName);
        Assert.Equal("0", set.Registry.Data);

        reg.Lm(MemoryManagement, "ClearPageFileAtShutdown", 0);
        Assert.False(Has(await Find(reg, folder), "storage.clear-pagefile-at-shutdown"));
    }

    [Fact]
    public async Task Memory_compression_is_only_flagged_when_its_process_is_known_to_be_missing()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var item = Item(await Find(reg, folder, new FixedSystemProbe { MemoryCompressionRunning = false }), "storage.memory-compression-off");
        Assert.Empty(item.Actions);
        Assert.Contains("Enable-MMAgent -MemoryCompression", item.Details!["How to fix"]);
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { MemoryCompressionRunning = null }), "storage.memory-compression-off"));
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { MemoryCompressionRunning = true }), "storage.memory-compression-off"));
    }

    // ------------------------------------------------------------------ network and services

    [Fact]
    public async Task Unticked_adapter_protocols_are_explained_with_a_guided_fix()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var probe = new FixedSystemProbe { UnboundProtocols = new[] { new UnboundProtocol("Ethernet", "ms_tcpip6"), new UnboundProtocol("Ethernet", "ms_msclient") } };
        var item = Item(await Find(reg, folder, probe), "network.adapter-protocols-unbound");
        Assert.Contains("Ethernet: Internet Protocol Version 6 (IPv6), Client for Microsoft Networks unticked", item.Details!["Found"]);
        Assert.Empty(item.Actions);
        Assert.False(Has(await Find(reg, folder, new FixedSystemProbe { UnboundProtocols = Array.Empty<UnboundProtocol>() }), "network.adapter-protocols-unbound"));
    }

    [Fact]
    public async Task Most_third_party_services_disabled_at_once_points_to_normal_startup()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var apps = Path.Combine(folder.Path, "Program Files");
        for (var i = 0; i < 6; i++) reg.Service($"VendorSvc{i}", start: i < 5 ? 4 : 2, imagePath: $@"""{apps}\Vendor{i}\svc.exe""");
        // Windows' own services don't count, disabled or not.
        reg.Service("SomeWindowsSvc", start: 4, imagePath: Path.Combine(folder.Path, "Windows", "System32", "thing.exe"));

        var item = Item(await Find(reg, folder), "services.third-party-all-disabled");
        Assert.Contains("5 of 6 services from other companies are disabled.", item.Details!["Found"]);
        Assert.DoesNotContain("SomeWindowsSvc", item.Details["Found"]);
        Assert.Contains("Normal startup", item.Details["How to fix"]);
        Assert.Empty(item.Actions); // the old start types aren't known, so PC Tidy doesn't guess them

        // One or two disabled updaters is normal.
        reg.Service("VendorSvc0", start: 2, imagePath: $@"""{apps}\Vendor0\svc.exe""");
        reg.Service("VendorSvc1", start: 2, imagePath: $@"""{apps}\Vendor1\svc.exe""");
        reg.Service("VendorSvc2", start: 2, imagePath: $@"""{apps}\Vendor2\svc.exe""");
        Assert.False(Has(await Find(reg, folder), "services.third-party-all-disabled"));
    }

    [Fact]
    public async Task Rweverything_and_winring0_drivers_are_flagged_without_an_exclusion()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        reg.Service("RwDrv", start: 3, imagePath: @"\??\C:\Tools\RW\RwDrv.sys");
        reg.Lm(@"SYSTEM\CurrentControlSet\Services\RwDrv", "Type", 1);
        reg.Service("R0FanControl", start: 3, imagePath: @"\??\C:\Apps\Fan\WinRing0x64.sys");
        reg.Lm(@"SYSTEM\CurrentControlSet\Services\R0FanControl", "Type", 1);

        var item = Item(await Find(reg, folder), "services.hardware-access-drivers");
        Assert.Contains("RwDrv.sys", item.Details!["Found"]);
        Assert.Contains("WinRing0x64.sys", item.Details["Found"]);
        Assert.Empty(item.Actions);
        Assert.Contains("Don't exclude", item.Details["Good to know"]);
    }

    // ------------------------------------------------------------------ TWK-1: boot fixes the platform can't undo

    [Fact]
    public async Task Boot_fixes_the_platform_cannot_undo_are_marked_permanent()
    {
        using var reg = new TestRegistry("research");
        using var folder = new TestFolder("research");
        var boot = new BootConfigSnapshot(new[]
        {
            new BootElement(0x25000055, null, 1),      // x2apicpolicy
            new BootElement(0x26000054, true, null),   // uselegacyapicmode
            new BootElement(0x260000A2, true, null),   // useplatformclock (undoable)
        });
        var r = await Find(reg, folder, boot: boot);
        foreach (var id in new[] { "boot.x2apicpolicy", "boot.uselegacyapicmode" })
        {
            var item = Item(r, id);
            var step = Assert.Single(item.Actions);
            Assert.False(step.Reversible);
            Assert.Equal(Badge.Permanent, item.Badge);
            Assert.False(item.DefaultSelected);
            Assert.Contains("can't be undone", item.Details!["Undo"]);
        }
        var clock = Item(r, "boot.useplatformclock");
        Assert.True(Assert.Single(clock.Actions).Reversible);
        Assert.Equal(Badge.Check, clock.Badge);
    }

    [Fact]
    public void Every_boot_fix_matches_what_the_platform_can_undo()
    {
        var probes = BadTweakCatalog.All.SelectMany(c => c.Signals)
            .Where(s => s.Fixes.OfType<BootDeleteFix>().Any()).Select(s => ((BootValueProbe)s.Probe).Name).Distinct().ToList();
        Assert.NotEmpty(probes);
        foreach (var name in probes)
        {
            Assert.True(PcTidy.Platform.SystemToolCatalog.BcdRemovable.TryGetValue(name, out var elementType), $"{name}: the platform doesn't allow removing it");
            Assert.Equal(elementType is not null, BadTweakCatalog.IsBootRemovalUndoable(name));
        }
    }

    // ------------------------------------------------------------------ catalog rows

    [Fact]
    public void Keep_it_on_rows_are_worded_in_the_catalog_and_change_nothing()
    {
        var keepOn = TweakCatalog.All.Where(t => t.IsKeepOn).ToList();
        Assert.Contains(keepOn, t => t.Id == "gaming.memory-integrity" && t.Title == "Memory integrity: keep it on");
        Assert.Contains(keepOn, t => t.Id == "gaming.write-cache-flushing");
        Assert.Contains(keepOn, t => t.Id == "gaming.exploit-protection");
        Assert.All(keepOn, t =>
        {
            Assert.False(t.HasActions);
            Assert.Contains(": keep", t.Title, StringComparison.Ordinal);
            Assert.True(TweakRowBuilder.IsKeepOn(t));
        });
    }

    [Fact]
    public void Undocumented_privacy_values_are_only_pointed_to_never_written()
    {
        foreach (var id in new[] { "privacy.lock-screen-spotlight", "privacy.explorer-sync-ads" })
        {
            var t = TweakCatalog.Find(id)!;
            Assert.False(t.HasActions);
            Assert.IsType<GuidanceChange>(Assert.Single(t.Changes));
        }
        var allowed = new TweakRegistryAllowances().Allowances().Select(a => a.ValueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("RotatingLockScreenEnabled", allowed);
        Assert.DoesNotContain("ShowSyncProviderNotifications", allowed);
    }

    [Fact]
    public void New_finder_writes_are_in_the_catalog_allowances_with_the_windows_default_only()
    {
        var all = new TweakRegistryAllowances().Allowances().ToList();
        var paging = Assert.Single(all, a => a.ValueName == "PagingFiles");
        Assert.Equal(new[] { @"?:\pagefile.sys" }, paging.AllowedData);
        var clear = Assert.Single(all, a => a.ValueName == "ClearPageFileAtShutdown");
        Assert.Equal(new[] { "0" }, clear.AllowedData);
        // And the guard's own lookup finds them for the real key.
        Assert.NotNull(RegistryCatalog.Find(CoreHive.LocalMachine, MemoryManagement, "ClearPageFileAtShutdown"));
    }

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", CpuKind.DualCcdX3D)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", CpuKind.DualCcdX3D)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", CpuKind.None)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", CpuKind.Hybrid)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuKind.Hybrid)]
    [InlineData("", CpuKind.None)]
    public void Cpu_families_are_classified_the_same_everywhere(string brand, CpuKind expected)
        => Assert.Equal(expected, CpuKinds.Classify(brand));
}
