using PcTidy.Catalog;
using PcTidy.Catalog.PcCare;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Tests;

/// <summary>
/// Adapter power saving (build list B6) with invented registry data: only physical Wi-Fi/Ethernet adapters whose class
/// subkey matches the adapter, the exact before-value, HKLM flags, the patterned allowance and the real guard.
/// </summary>
public class AdapterPowerTests
{
    private const string WifiId = "{11111111-2222-3333-4444-555555555501}";
    private const string LanId = "{11111111-2222-3333-4444-555555555502}";
    private const string HyperVId = "{11111111-2222-3333-4444-555555555503}";
    private const string BtId = "{11111111-2222-3333-4444-555555555504}";
    private const string TapId = "{11111111-2222-3333-4444-555555555505}";
    private const string LoopId = "{11111111-2222-3333-4444-555555555506}";
    private const string UsbLanId = "{11111111-2222-3333-4444-555555555507}";
    private const string CustomId = "{11111111-2222-3333-4444-555555555508}";
    private const string GoneId = "{11111111-2222-3333-4444-555555555509}";

    /// <summary>A made-up PC: one PCI Wi-Fi card, one PCI Ethernet port, a USB Ethernet dongle (already off), a card with a
    /// custom value, and the usual virtual adapters (Hyper-V, Bluetooth PAN, a VPN TAP, loopback) plus a stale subkey.</summary>
    internal static AdapterPowerFacts Facts() => new(
        new[]
        {
            new NetClassEntry("0001", WifiId, "Contoso Wireless AX Card", @"pci\ven_1234&dev_0001", 0x84, 71, 9, null),
            new NetClassEntry("0002", LanId, "Contoso Gigabit Ethernet", @"PCI\VEN_1234&DEV_0002", 0x84, 6, 14, 0),
            new NetClassEntry("0003", HyperVId, "Hyper-V Virtual Ethernet Adapter", "vms_mp", 0x1, 6, 14, null),
            new NetClassEntry("0004", BtId, "Bluetooth Device (Personal Area Network)", @"bth\ms_bthpan", 0x4, 6, 14, null),
            new NetClassEntry("0005", TapId, "TAP-Windows Adapter V9", @"root\tap0901", 0x1, 6, 14, null),
            new NetClassEntry("0006", LoopId, "Microsoft KM-TEST Loopback Adapter", @"*msloop", 0x1, 6, 14, null),
            new NetClassEntry("0007", UsbLanId, "Fabrikam USB 2.5G Ethernet", @"usb\vid_0b95&pid_1790", 0x84, 6, 14, 24),
            new NetClassEntry("0008", CustomId, "Contoso Wireless AC Card", @"pci\ven_1234&dev_0003", 0x84, 71, 9, 280),
            new NetClassEntry("0009", GoneId, "Contoso Old Ethernet", @"pci\ven_1234&dev_0009", 0x84, 6, 14, null),
            new NetClassEntry("Properties", WifiId, "Contoso Wireless AX Card", @"pci\ven_1234&dev_0001", 0x84, 71, 9, null),
        },
        new[]
        {
            new NetAdapter(WifiId, "Wi-Fi", "Contoso Wireless AX Card", NetAdapterMedium.WiFi, true),
            new NetAdapter(LanId, "Ethernet", "Contoso Gigabit Ethernet", NetAdapterMedium.Ethernet, true),
            new NetAdapter(HyperVId, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetAdapterMedium.Ethernet, false),
            new NetAdapter(BtId, "Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetAdapterMedium.Ethernet, true),
            new NetAdapter(TapId, "VPN", "TAP-Windows Adapter V9", NetAdapterMedium.Ethernet, false),
            new NetAdapter(LoopId, "Loopback", "Microsoft KM-TEST Loopback Adapter", NetAdapterMedium.Ethernet, false),
            new NetAdapter(UsbLanId, "Ethernet 2", "Fabrikam USB 2.5G Ethernet", NetAdapterMedium.Ethernet, true),
            new NetAdapter(CustomId, "Wi-Fi 2", "Contoso Wireless AC Card", NetAdapterMedium.WiFi, true),
        });

    [Fact]
    public void Only_physical_adapters_whose_subkey_matches_are_candidates()
    {
        var c = AdapterPowerRules.Candidates(Facts());
        Assert.Equal(new[] { "0008", "0001", "0002", "0007" }, c.Select(x => x.Entry.Subkey)); // Wi-Fi first, then Ethernet, each by name
        Assert.Equal(AdapterPowerState.CustomValue, c[0].State);
        Assert.Equal(AdapterPowerState.CanTurnOff, c[1].State);
        Assert.Equal(AdapterPowerState.CanTurnOff, c[2].State);
        Assert.Equal(AdapterPowerState.AlreadyOff, c[3].State);
        Assert.Equal("absent", c[1].BeforeValue);
        Assert.Equal("280", c[0].BeforeValue);
        Assert.Equal("0", c[2].BeforeValue);
    }

    [Theory]
    [InlineData(0x84 | 0x1)]  // virtual flag
    [InlineData(0x80)]        // no physical flag
    [InlineData(0x84 | 0x8)]  // hidden
    public void Characteristics_must_say_physical_and_not_virtual_or_hidden(int flags)
    {
        var f = Facts();
        var e = f.ClassEntries[0] with { Characteristics = flags };
        Assert.Empty(AdapterPowerRules.Candidates(f with { ClassEntries = new[] { e } }));
    }

    [Fact]
    public void A_mismatched_or_ambiguous_instance_id_is_never_matched()
    {
        var f = Facts();
        var wifi = f.ClassEntries[0];
        Assert.Empty(AdapterPowerRules.Candidates(f with { ClassEntries = new[] { wifi with { NetCfgInstanceId = GoneId } } }));
        Assert.Empty(AdapterPowerRules.Candidates(f with { ClassEntries = new[] { wifi with { NetCfgInstanceId = "not-a-guid" } } }));
        var twice = f with { ClassEntries = new[] { wifi }, Adapters = new[] { f.Adapters[0], f.Adapters[0] with { Name = "Copy" } } };
        Assert.Empty(AdapterPowerRules.Candidates(twice));
        // WMI saying "not physical" wins; a medium that disagrees with Windows' own listing is skipped too.
        Assert.Empty(AdapterPowerRules.Candidates(f with { ClassEntries = new[] { wifi }, Adapters = new[] { f.Adapters[0] with { WmiPhysical = false } } }));
        Assert.Empty(AdapterPowerRules.Candidates(f with { ClassEntries = new[] { wifi }, Adapters = new[] { f.Adapters[0] with { Medium = NetAdapterMedium.Ethernet } } }));
        // A non-DWORD value is left alone.
        Assert.Equal(AdapterPowerState.CustomValue,
            AdapterPowerRules.Candidates(f with { ClassEntries = new[] { wifi with { PnPCapabilitiesOtherType = true } } }).Single().State);
    }

    [Fact]
    public void Rows_start_unticked_and_say_plainly_what_they_cost()
    {
        var r = Scenario.Build(Scenario.OwnerLike() with { AdapterPower = Facts() });
        var g = r.Group(T.Network, "adapterpower")!;
        Assert.Contains("iwlwifi", g.Description);
        Assert.Contains("50-300 ms", g.Description);
        Assert.Contains("Situational", g.Description);
        Assert.Contains("Nothing is ticked for you", g.Description);
        Assert.DoesNotContain("Wi-Fi is ticked", g.Description);

        var wifi = g.Items.Single(i => i.Title == "Wi-Fi: Contoso Wireless AX Card");
        var lan = g.Items.Single(i => i.Title == "Ethernet: Contoso Gigabit Ethernet");
        // Safety review M3: Wi-Fi is unticked too, like every other new v3.1 apply path.
        Assert.False(wifi.DefaultSelected);
        Assert.False(lan.DefaultSelected);
        Assert.True(wifi.NeedsRestart && wifi.NeedsAdmin);
        // The row says it's situational evidence, turns off wake on wireless, needs a restart, and may be ignored.
        Assert.Contains("Situational", wifi.Reason);
        Assert.Contains("wake on wireless", wifi.Reason);
        Assert.Contains("restart", wifi.Reason);
        Assert.Contains("Newer drivers may ignore this setting", wifi.Reason);
        Assert.Contains("wake on LAN", lan.Reason);
        Assert.Contains("restart", lan.Reason);
        Assert.Contains("Newer drivers may ignore this setting", lan.Reason);
        Assert.Contains("iwlwifi", wifi.Details!["Evidence"]);

        var custom = g.Items.Single(i => i.Title == "Wi-Fi: Contoso Wireless AC Card");
        Assert.NotNull(custom.DisabledReason);
        Assert.Empty(custom.Actions);
        var already = g.Items.Single(i => i.Title == "Ethernet: Fabrikam USB 2.5G Ethernet");
        Assert.Empty(already.Actions);
        Assert.False(already.DefaultSelected);

        // Nothing is ticked for you anywhere in PC care.
        Assert.All(r.Result.AllItems, i => Assert.False(i.DefaultSelected, i.Title));
        Assert.DoesNotContain(g.Items, i => i.Title.Contains("Hyper-V") || i.Title.Contains("Bluetooth") || i.Title.Contains("TAP") || i.Title.Contains("Loopback"));
    }

    [Fact]
    public void The_plan_item_is_an_exact_HKLM_admin_system_change_with_the_before_value()
    {
        var r = Scenario.Build(Scenario.OwnerLike() with { AdapterPower = Facts() });
        var items = r.Group(T.Network, "adapterpower")!.Items.SelectMany(i => i.Actions).ToList();
        Assert.Equal(2, items.Count);
        var wifi = items.Single(i => i.Registry!.KeyPath.EndsWith(@"\0001"));
        Assert.Equal(ActionKind.RegistrySetValue, wifi.Kind);
        Assert.Equal(RegistryHive.LocalMachine, wifi.Registry!.Hive);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001", wifi.Registry.KeyPath);
        Assert.Equal("PnPCapabilities", wifi.Registry.ValueName);
        Assert.Equal(RegistryKind.DWord, wifi.Registry.Kind);
        Assert.Equal("24", wifi.Registry.Data);
        Assert.True(wifi.NeedsAdmin && wifi.IsSystemChange && wifi.Reversible);
        Assert.Equal("absent", wifi.Args!["before"]);
        Assert.Equal("true", wifi.Args["needsRestart"]);
        Assert.Contains("after a restart", wifi.Description);
        Assert.Equal("0", items.Single(i => i.Registry!.KeyPath.EndsWith(@"\0002")).Args!["before"]);
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007", "PnPCapabilities", true)]
    [InlineData(@"system\currentcontrolset\control\class\{4D36E972-E325-11CE-BFC1-08002BE10318}\0012", "pnpcapabilities", true)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007\Ndi", "PnPCapabilities", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\00071", "PnPCapabilities", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\007", "PnPCapabilities", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\Properties", "PnPCapabilities", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", "PnPCapabilities", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007", "PnPCapabilitiesX", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007", "*SpeedDuplex", false)]
    public void The_allowance_pattern_matches_one_adapter_subkey_and_one_value_only(string key, string value, bool allowed)
    {
        var a = RegistryCatalog.Find(RegistryHive.LocalMachine, key, value);
        if (!allowed)
        {
            Assert.True(a is null || a.Source != AdapterPowerAllowances.Source, $"{key} {value} -> {a?.Source}");
            return;
        }
        Assert.NotNull(a);
        Assert.Equal(AdapterPowerAllowances.Source, a!.Source);
        Assert.Equal(RegistryKind.DWord, a.Kind);
        Assert.Equal(new[] { "24" }, a.AllowedData);
        Assert.True(a.NeedsAdmin && a.AllowDelete);
        Assert.Equal(AdapterPowerAllowances.KeyPathPattern, a.KeyPathPattern);
        Assert.Null(RegistryCatalog.Find(RegistryHive.CurrentUser, key, value));
    }

    [Fact]
    public void The_real_guard_allows_24_and_refuses_any_other_data_or_missing_flags()
    {
        using var tb = new TestbedFolder();
        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var item = Planning.PcCareActions.AdapterPowerSavingOff("0003", "Contoso Wireless AX Card", "absent");
        Assert.True(host.Safety.Check(item).Allowed, host.Safety.Check(item).Reason);
        Assert.False(host.Safety.Check(item with { Registry = item.Registry! with { Data = "0" } }).Allowed);
        Assert.False(host.Safety.Check(item with { Registry = item.Registry! with { Data = "280" } }).Allowed);
        Assert.False(host.Safety.Check(item with { IsSystemChange = false }).Allowed);
        Assert.False(host.Safety.Check(item with { NeedsAdmin = false }).Allowed);
        Assert.False(host.Safety.Check(item with { Registry = item.Registry! with { KeyPath = item.Registry.KeyPath + @"\Ndi" } }).Allowed);
    }

    [Fact]
    public void Without_adapter_facts_there_is_no_group()
    {
        Assert.Null(Scenario.Build().Group(T.Network, "adapterpower"));
        Assert.Null(Scenario.Build(Scenario.OwnerLike() with { AdapterPower = new AdapterPowerFacts(Array.Empty<NetClassEntry>(), Array.Empty<NetAdapter>()) })
            .Group(T.Network, "adapterpower"));
    }
}
