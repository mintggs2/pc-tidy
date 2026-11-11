using PcTidy.Catalog;
using PcTidy.Catalog.Games;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// An in-memory registry for the finder (fake reader only: nothing real is read or written). Keys are logical
/// (no redirect), so the plan items carry the real HKLM paths and the REAL SafetyGuard can judge them.
/// </summary>
public sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, RegValue>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public RegistryRedirect Redirect => RegistryRedirect.None;

    private static string Id(CoreHive hive, string key) => $@"{RegistryNames.Short(hive)}\{key.Trim('\\')}";

    public FakeRegistry Key(CoreHive hive, string key)
    {
        var parts = key.Trim('\\').Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var id = Id(hive, string.Join('\\', parts[..i]));
            if (!_keys.ContainsKey(id)) _keys[id] = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
        }
        return this;
    }

    public FakeRegistry Set(CoreHive hive, string key, string name, RegistryKind kind, string data)
    {
        Key(hive, key);
        _keys[Id(hive, key)][name] = new RegValue(kind, data);
        return this;
    }

    public FakeRegistry Dword(CoreHive hive, string key, string name, long value) => Set(hive, key, name, RegistryKind.DWord, unchecked((uint)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
    public FakeRegistry Text(CoreHive hive, string key, string name, string value) => Set(hive, key, name, RegistryKind.String, value);
    public FakeRegistry Lm(string key, string name, long value) => Dword(CoreHive.LocalMachine, key, name, value);
    public FakeRegistry LmText(string key, string name, string value) => Text(CoreHive.LocalMachine, key, name, value);

    /// <summary>A service key (Start, Type, ImagePath).</summary>
    public FakeRegistry Service(string name, int start, int type, string imagePath)
    {
        var key = $@"SYSTEM\CurrentControlSet\Services\{name}";
        return Lm(key, "Start", start).Lm(key, "Type", type).Set(CoreHive.LocalMachine, key, "ImagePath", RegistryKind.ExpandString, imagePath);
    }

    public bool KeyExists(CoreHive hive, string keyPath) => _keys.ContainsKey(Id(hive, keyPath));

    public RegValue? Read(CoreHive hive, string keyPath, string valueName)
        => _keys.TryGetValue(Id(hive, keyPath), out var values) && values.TryGetValue(valueName, out var v) ? v : null;

    public IReadOnlyList<string> ValueNames(CoreHive hive, string keyPath)
        => _keys.TryGetValue(Id(hive, keyPath), out var values) ? values.Keys.ToList() : Array.Empty<string>();

    public IReadOnlyList<string> SubKeyNames(CoreHive hive, string keyPath)
    {
        var prefix = Id(hive, keyPath) + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..]).Where(rest => !rest.Contains('\\')).ToList();
    }
}

/// <summary>
/// v3.1 bad-tweak finder additions (comparison page §8.3, "FPS boost tweaks" §4 HARMFUL rows, the two new repairs).
/// Fake registry, fixed probes and temp folders only; the real SafetyGuard of a sandbox judges every planned step.
/// </summary>
public class FinderV31Tests
{
    private const string Area = "finder-v31";
    private const string Defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    private const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static TweakEnvironment Env(FakeRegistry reg, string folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null,
        IReadOnlyDictionary<string, string>? apps = null, int build = 26100, IReadOnlyList<string>? leftoverFolders = null) => new()
    {
        Registry = reg,
        Probe = probe ?? FixedSystemProbe.Unknown,
        Boot = new FixedBootConfigReader(boot ?? BootConfigSnapshot.Empty),
        Apps = new FixedAppxInventory(apps),
        Tasks = new FixedTaskReader(),
        HostsPath = TweakEnvironment.HostsFileIn(Path.Combine(folder, "Windows")),
        LocalAppData = Path.Combine(folder, "LocalAppData"),
        WindowsDir = Path.Combine(folder, "Windows"),
        WindowsBuild = build,
        LeftoverFolders = leftoverFolders ?? Array.Empty<string>(),
    };

    private sealed record Run(ScanResult Result, ScanContext Context);

    private static async Task<Run> Find(TweakEnvironment env, string folder)
    {
        var ctx = TestEnv.Context(folder);
        return new Run(await new BadTweakScanner(_ => env).ScanAsync(ctx, null, CancellationToken.None), ctx);
    }

    private static async Task<Run> Find(FakeRegistry reg, TestFolder folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null,
        IReadOnlyDictionary<string, string>? apps = null, int build = 26100, IReadOnlyList<string>? leftoverFolders = null)
        => await Find(Env(reg, folder.Path, probe, boot, apps, build, leftoverFolders), folder.Path);

    private static ScanItem Item(Run r, string checkId) => Assert.Single(r.Result.AllItems, i => i.Id == "badtweaks." + checkId);
    private static bool Has(Run r, string checkId) => r.Result.AllItems.Any(i => i.Id == "badtweaks." + checkId);
    private static GuidanceFix Guidance(string checkId) => BadTweakCatalog.Find(checkId)!.Signals.SelectMany(s => s.Fixes).OfType<GuidanceFix>().First();

    // ------------------------------------------------------------------ Tamper Protection and Defender

    [Fact]
    public async Task Tamper_protection_off_is_reported_and_only_opens_windows_security()
    {
        using var folder = new TestFolder("tamper", Area);
        var probe = new FixedSystemProbe { Defender = new DefenderProtection(AntivirusEnabled: true, TamperProtected: false, CloudReporting: 2) { RealTimeProtection = true } };
        var r = await Find(new FakeRegistry(), folder, probe);
        var item = Item(r, "security.defender-protection-reduced");
        Assert.Contains("Tamper Protection is off.", item.Details!["Found"]);
        Assert.Empty(item.Actions); // the only fix is opening Windows Security
        Assert.Equal("windowsdefender://threat-settings", Guidance("security.defender-protection-reduced").SettingsUri);
        Assert.Equal("windowsdefender://threat-settings", Guidance("security.defender-only-realtime").SettingsUri);

        // The link the page opens passes the real guard as an OpenExternal step.
        var open = new PlanItem { Kind = ActionKind.OpenExternal, FeatureId = "tweaks", Description = "Open Windows Security",
            Args = new Dictionary<string, string> { ["target"] = BadTweakCatalog.DefenderThreatSettings } };
        Assert.True(r.Context.Safety.Check(open).Allowed);
    }

    [Fact]
    public void No_finder_fix_ever_writes_a_defender_or_tamper_value_except_removing_policies()
    {
        foreach (var check in BadTweakCatalog.All)
            foreach (var signal in check.Signals)
                foreach (var fix in signal.Fixes)
                {
                    var key = fix switch
                    {
                        RegistrySetFix when signal.Probe is RegistryProbe p => p.KeyPath,
                        RegistryWriteFix w => w.KeyPath,
                        RegistryDeleteFix d => d.KeyPath ?? (signal.Probe as RegistryProbe)?.KeyPath,
                        _ => null,
                    };
                    if (key is null || !key.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)) continue;
                    Assert.IsType<RegistryDeleteFix>(fix);
                    Assert.StartsWith(@"SOFTWARE\Policies\", key, StringComparison.OrdinalIgnoreCase);
                }
        // Defender's own drivers and switches are information only.
        var defenderOff = BadTweakCatalog.Find("security.defender-off")!;
        foreach (var svc in new[] { "WdFilter", "WdBoot", "WdNisDrv" })
            Assert.All(defenderOff.Signals.Where(s => s.Probe is ServiceDisabledProbe p && p.ServiceName == svc), s => Assert.All(s.Fixes, f => Assert.IsType<GuidanceFix>(f)));
        Assert.DoesNotContain(RegistryCatalog.All, a => a.KeyPath.StartsWith(@"SOFTWARE\Microsoft\Windows Defender", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Realtime_protection_off_without_a_policy_is_its_own_information_card()
    {
        using var folder = new TestFolder("realtime", Area);
        var off = new DefenderProtection(AntivirusEnabled: true, TamperProtected: true, CloudReporting: 2) { RealTimeProtection = false };
        var item = Item(await Find(new FakeRegistry(), folder, new FixedSystemProbe { Defender = off }), "security.defender-realtime-off");
        Assert.Empty(item.Actions);
        Assert.Equal(BadTweakSeverity.High, BadTweakCatalog.Find("security.defender-realtime-off")!.Severity);
        Assert.Equal(BadTweakCatalog.DefenderThreatSettings, Guidance("security.defender-realtime-off").SettingsUri);

        // A policy is behind it: the policy card (which removes it) says so instead.
        var policy = new FakeRegistry().Lm(Defender + @"\Real-Time Protection", "DisableRealtimeMonitoring", 1);
        var withPolicy = await Find(policy, folder, new FixedSystemProbe { Defender = off });
        Assert.False(Has(withPolicy, "security.defender-realtime-off"));
        Assert.True(Has(withPolicy, "security.defender-off"));
        // Another antivirus in charge: Defender's switch doesn't apply.
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { Defender = off, ThirdPartyAntivirus = new[] { "Example AV" } }), "security.defender-realtime-off"));
    }

    [Fact]
    public async Task Defender_switched_off_in_its_own_keys_and_drivers_is_listed_but_never_changed()
    {
        using var folder = new TestFolder("defender-own", Area);
        var reg = new FakeRegistry()
            .Lm(@"SOFTWARE\Microsoft\Windows Defender", "DisableAntiSpyware", 1)
            .Lm(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 1)
            .Service("WdFilter", start: 4, type: 2, imagePath: @"system32\drivers\wd\WdFilter.sys");
        var item = Item(await Find(reg, folder), "security.defender-off");
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows Defender → DisableAntiSpyware = 1 (Defender's own setting, not a policy)", item.Details!["Found"]);
        Assert.Contains("Service 'WdFilter' is Disabled", item.Details["Found"]);
        Assert.Empty(item.Actions);
        Assert.Contains("Fix problems using Windows Update", item.Details["How to fix"]);

        // With another antivirus in charge, Defender's own switches are Windows' business.
        var other = Item(await Find(reg, folder, new FixedSystemProbe { ThirdPartyAntivirus = new[] { "Example AV" } }), "security.defender-off");
        Assert.DoesNotContain("Defender's own setting", other.Details!["Found"]);
    }

    [Theory]
    [InlineData(@"NIS\Consumers\IPS", "DisableProtocolRecognition", "1", DefenderPolicyEffect.Weakens)]
    [InlineData(@"NIS\Consumers\IPS", "DisableProtocolRecognition", "0", DefenderPolicyEffect.Protects)]
    [InlineData(@"NIS\Consumers\IPS", "ThrottleForScheduledScanOnly", "1", DefenderPolicyEffect.Protects)]
    public void The_network_inspection_switch_off_is_a_weakening_policy(string key, string name, string data, DefenderPolicyEffect expected)
        => Assert.Equal(expected, DefenderPolicyEffects.Classify(key, name, data).Effect);

    // ------------------------------------------------------------------ network

    [Fact]
    public async Task Bbr2_on_windows_11_24h2_is_reported_with_the_command_to_put_cubic_back()
    {
        using var folder = new TestFolder("bbr2", Area);
        var probe = new FixedSystemProbe
        {
            CongestionProviders = new[] { new CongestionSetting("Internet", "BBR2"), new CongestionSetting("InternetCustom", "BBR2"), new CongestionSetting("Datacenter", "CUBIC") },
        };
        var item = Item(await Find(new FakeRegistry(), folder, probe), "network.bbr2-congestion");
        Assert.Contains("Internet: BBR2. To put CUBIC back: netsh int tcp set supplemental template=internet congestionprovider=cubic", item.Details!["Found"]);
        Assert.Contains("template=internetcustom", item.Details["Found"]);
        Assert.DoesNotContain("Datacenter", item.Details["Found"]);
        Assert.Empty(item.Actions); // report-only until a reset tool id exists
        Assert.True(item.NeedsAdmin);

        Assert.False(Has(await Find(new FakeRegistry(), folder, probe, build: 22631), "network.bbr2-congestion")); // before 24H2
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { CongestionProviders = new[] { new CongestionSetting("Internet", null) } }), "network.bbr2-congestion"));
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe()), "network.bbr2-congestion"));
    }

    private static FakeRegistry Adapter(FakeRegistry reg, string sub, string name, int characteristics, int ifType, string? jumbo, string? driverDefault = "1514", int? media = 14)
    {
        var key = $@"{NetClass}\{sub}";
        reg.Lm(key, "Characteristics", characteristics).Lm(key, "*IfType", ifType).LmText(key, "DriverDesc", name);
        if (media is { } m) reg.Lm(key, "*PhysicalMediaType", m);
        if (jumbo is not null) reg.LmText(key, "*JumboPacket", jumbo);
        if (driverDefault is not null) reg.LmText(key + @"\Ndi\Params\*JumboPacket", "default", driverDefault);
        return reg;
    }

    [Fact]
    public async Task Jumbo_packets_on_a_physical_ethernet_adapter_are_reset_to_1514_through_the_patterned_allowance()
    {
        using var folder = new TestFolder("jumbo", Area);
        var reg = new FakeRegistry();
        Adapter(reg, "0001", "Contoso Gigabit Ethernet", characteristics: 0x84, ifType: 6, jumbo: "9014");
        Adapter(reg, "0002", "Contoso Wi-Fi 7", characteristics: 0x84, ifType: 71, jumbo: "9014", media: 9);   // Wi-Fi: not Ethernet
        Adapter(reg, "0003", "Fabrikam Virtual Ethernet", characteristics: 0x1, ifType: 6, jumbo: "9014");     // virtual
        Adapter(reg, "0004", "Northwind Ethernet", characteristics: 0x84, ifType: 6, jumbo: "1514");           // already standard
        Adapter(reg, "0005", "Woodgrove 10G", characteristics: 0x84, ifType: 6, jumbo: "9000", driverDefault: "1500"); // no 1514 option
        reg.LmText($@"{NetClass}\Properties", "x", "y"); // not an adapter subkey

        var r = await Find(reg, folder);
        var item = Item(r, "network.jumbo-frames");
        Assert.Contains("Contoso Gigabit Ethernet: Jumbo Packet = 9014 bytes", item.Details!["Found"]);
        Assert.Contains("Woodgrove 10G: Jumbo Packet = 9000 bytes (PC Tidy can't pick the standard size", item.Details["Found"]);
        Assert.DoesNotContain("Wi-Fi", item.Details["Found"]);
        Assert.DoesNotContain("Virtual", item.Details["Found"]);
        Assert.DoesNotContain("Northwind", item.Details["Found"]);
        Assert.True(item.NeedsAdmin && item.NeedsRestart);
        Assert.False(item.DefaultSelected);

        var step = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.RegistrySetValue, step.Kind);
        Assert.Equal($@"{NetClass}\0001", step.Registry!.KeyPath);
        Assert.Equal("*JumboPacket", step.Registry.ValueName);
        Assert.Equal(RegistryKind.String, step.Registry.Kind);
        Assert.Equal("1514", step.Registry.Data);
        Assert.True(step.NeedsAdmin && step.IsSystemChange && step.Reversible);
        Assert.Contains("restart", step.Description);

        // The real guard: 1514 on a network-adapter subkey only.
        Assert.True(r.Context.Safety.Check(step).Allowed);
        Assert.False(r.Context.Safety.Check(step with { Registry = step.Registry with { Data = "9014" } }).Allowed);
        Assert.False(r.Context.Safety.Check(step with { Registry = step.Registry with { KeyPath = $@"{DisplayClass}\0001" } }).Allowed);
        Assert.False(r.Context.Safety.Check(step with { Registry = step.Registry with { KeyPath = $@"{NetClass}\0001\Ndi" } }).Allowed);
        Assert.False(r.Context.Safety.Check(step with { Kind = ActionKind.RegistryDeleteValue }).Allowed);
        Assert.False(r.Context.Safety.Check(step with { IsSystemChange = false }).Allowed);
    }

    [Fact]
    public void Patterned_allowances_are_anchored_and_name_one_value_each()
    {
        var mine = new BadTweakAllowances().Allowances().ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, a =>
        {
            Assert.StartsWith("^", a.KeyPathPattern);
            Assert.EndsWith("$", a.KeyPathPattern);
            Assert.Null(a.ValueNamePattern);
            Assert.True(a.NeedsAdmin);
            Assert.Equal(CoreHive.LocalMachine, a.Hive);
        });
        var jumbo = RegistryCatalog.Find(CoreHive.LocalMachine, $@"{NetClass}\0012", "*JumboPacket");
        Assert.NotNull(jumbo);
        Assert.Equal(new[] { "1514" }, jumbo!.AllowedData);
        Assert.False(jumbo.AllowDelete);
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{NetClass}\00123", "*JumboPacket"));
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{NetClass}\0012", "*FlowControl"));
        var priority = RegistryCatalog.Find(CoreHive.LocalMachine, $@"{Ifeo}\game.exe\PerfOptions", "CpuPriorityClass");
        Assert.NotNull(priority);
        Assert.Empty(priority!.AllowedData); // delete-only
        Assert.True(priority.AllowDelete);
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{Ifeo}\game.exe", "Debugger"));
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{Ifeo}\a\b\PerfOptions", "CpuPriorityClass"));
    }

    // ------------------------------------------------------------------ Realtime priority (undo-able removal)

    [Fact]
    public async Task Realtime_priority_removal_passes_the_guard_and_nothing_else_under_ifeo_does()
    {
        using var folder = new TestFolder("ifeo", Area);
        var reg = new FakeRegistry()
            .Lm($@"{Ifeo}\game.exe\PerfOptions", "CpuPriorityClass", 4)
            .Lm($@"{Ifeo}\tool.exe\PerfOptions", "CpuPriorityClass", 3);
        var r = await Find(reg, folder);
        var item = Item(r, "scheduler.realtime-priority");
        var step = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.RegistryDeleteValue, step.Kind);
        Assert.Equal($@"{Ifeo}\game.exe\PerfOptions", step.Registry!.KeyPath);
        Assert.Contains("forces game.exe to Realtime priority", step.Description);
        Assert.True(step.Reversible && step.NeedsAdmin && step.IsSystemChange);
        Assert.Contains("Undo", item.Details!["Good to know"]);
        Assert.True(r.Context.Safety.Check(step).Allowed);
        // Never a write, and never another IFEO value (a "Debugger" there could hijack a program).
        Assert.False(r.Context.Safety.Check(step with { Kind = ActionKind.RegistrySetValue, Registry = step.Registry with { Data = "4" } }).Allowed);
        Assert.False(r.Context.Safety.Check(step with { Registry = step.Registry with { KeyPath = $@"{Ifeo}\game.exe", ValueName = "Debugger" } }).Allowed);
    }

    [Fact]
    public void A_priority_removal_is_planned_only_while_the_data_is_still_realtime()
    {
        // The patterned allowance can't say "delete only when it is 4" (AllowedData lists data that may be written), so
        // the finder reads the value again when it builds the step (safety review L2).
        using var folder = new TestFolder("ifeo-recheck", Area);
        var reg = new FakeRegistry()
            .Lm($@"{Ifeo}\game.exe\PerfOptions", "CpuPriorityClass", 4)
            .Lm($@"{Ifeo}\tool.exe\PerfOptions", "CpuPriorityClass", 3);
        var env = Env(reg, folder.Path);
        FoundValue V(string exe) => new(CoreHive.LocalMachine, $@"{Ifeo}\{exe}\PerfOptions", "CpuPriorityClass", RegistryKind.DWord,
            OnlyIfData: BadTweakAllowances.PriorityRealtime);
        Assert.Equal("4", BadTweakAllowances.PriorityRealtime);
        Assert.True(BadTweakScanner.StillHolds(env, V("game.exe"), BadTweakAllowances.PriorityRealtime));
        Assert.False(BadTweakScanner.StillHolds(env, V("tool.exe"), BadTweakAllowances.PriorityRealtime));
        Assert.False(BadTweakScanner.StillHolds(env, V("gone.exe"), BadTweakAllowances.PriorityRealtime));
    }

    [Fact]
    public void Patterned_adapter_keys_take_ascii_digits_only()
    {
        // Arabic-Indic digits match \d in .NET; the patterns use [0-9] (safety review L1).
        Assert.NotNull(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{NetClass}\0012", "*JumboPacket"));
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $"{NetClass}\\١٢٣٤", "*JumboPacket"));
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $"{NetClass}\\١٢٣٤", "PnPCapabilities"));
    }

    // ------------------------------------------------------------------ BIOS tools, kernel helpers, optimizer suites

    [Fact]
    public async Task Bios_tool_leftovers_list_names_and_folders_never_contents_and_offer_nothing()
    {
        using var folder = new TestFolder("bios", Area);
        var downloads = Path.Combine(folder.Path, "Downloads");
        var tool = Path.Combine(downloads, "BiosTools", "AMISCE_x");
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, "SCEWIN_64.exe"), "stub");
        File.WriteAllText(Path.Combine(tool, "amifldrv64.sys"), "stub");
        File.WriteAllText(Path.Combine(tool, "nvram.txt"), "Setup Question = INVENTED-SECRET-VALUE");
        var lonely = Path.Combine(downloads, "Notes");
        Directory.CreateDirectory(lonely);
        File.WriteAllText(Path.Combine(lonely, "nvram.txt"), "not next to a tool");
        var reg = new FakeRegistry().Service("amifldrv64", start: 3, type: 1, imagePath: @"\??\C:\Tools\amifldrv64.sys");

        var item = Item(await Find(reg, folder, leftoverFolders: new[] { downloads }), "windows.bios-tool-leftovers");
        var found = item.Details!["Found"];
        Assert.Contains("Driver 'amifldrv64' (amifldrv64.sys, from AMI's BIOS tools (SCEWIN, AFUWIN))", found);
        Assert.Contains($"{tool}: amifldrv64.sys, SCEWIN_64.exe, nvram.txt (an export holds BIOS settings)", found);
        Assert.DoesNotContain(lonely, found);
        Assert.DoesNotContain("INVENTED-SECRET-VALUE", string.Join("\n", item.Details.Values));
        Assert.Empty(item.Actions); // never deleted automatically
        Assert.Contains("never deletes firmware tools", item.Details["Good to know"]);
        Assert.True(File.Exists(Path.Combine(tool, "SCEWIN_64.exe")));

        Assert.False(Has(await Find(new FakeRegistry(), folder, leftoverFolders: new[] { lonely }), "windows.bios-tool-leftovers"));
    }

    [Fact]
    public async Task Kernel_helper_drivers_are_reported_with_pc_care_overlap_and_a_pointer_to_apps_or_drivers()
    {
        using var folder = new TestFolder("helpers", Area);
        var reg = new FakeRegistry()
            .Service("PawnIO", start: 3, type: 1, imagePath: @"\??\C:\Program Files\PawnIO\PawnIO.sys")
            .Service("ContosoFan", start: 3, type: 1, imagePath: @"\??\C:\Apps\Fan\WinRing0x64.sys")
            .Service("ContosoAudio", start: 3, type: 1, imagePath: @"\SystemRoot\System32\drivers\contosoaudio.sys");
        var item = Item(await Find(reg, folder), "services.hardware-access-drivers");
        Assert.Contains("Driver 'PawnIO' (PawnIO.sys, from PawnIO", item.Details!["Found"]);
        Assert.Contains("WinRing0x64.sys", item.Details["Found"]);
        Assert.Contains("PC care also lists it for a known security hole (CVE-2020-14979)", item.Details["Found"]);
        Assert.DoesNotContain("contosoaudio", item.Details["Found"]);
        Assert.Empty(item.Actions);
        Assert.Contains("Apps section", item.Details["How to fix"]);
        Assert.Contains("Drivers section", item.Details["How to fix"]);
    }

    [Fact]
    public async Task Optimizer_suites_and_registry_cleaners_are_reported_with_the_measured_number()
    {
        using var folder = new TestFolder("suites", Area);
        const string lm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        var reg = new FakeRegistry()
            .LmText(lm + @"\ASC_is1", "DisplayName", "Advanced SystemCare 99")
            .LmText(wow + @"\WRC_is1", "DisplayName", "Wise Registry Cleaner 99")
            .Text(CoreHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CCleaner", "DisplayName", "CCleaner")
            .LmText(lm + @"\Notes", "DisplayName", "Contoso Notes")
            .LmText(lm + @"\Hidden", "DisplayName", "Glary Utilities").Lm(lm + @"\Hidden", "SystemComponent", 1);
        var r = await Find(reg, folder);

        var suite = Item(r, "windows.optimizer-suite-installed");
        Assert.Equal("Advanced SystemCare 99 is installed", suite.Details!["Found"]);
        Assert.Contains("about 7% slower", suite.Reason);
        Assert.Contains("73% slower to start", suite.Reason);
        Assert.Contains("test PC not stated", suite.Reason);
        Assert.Empty(suite.Actions);
        Assert.Contains("Apps section", suite.Details["How to fix"]);
        Assert.Equal("ms-settings:appsfeatures", Guidance("windows.optimizer-suite-installed").SettingsUri);

        var cleaners = Item(r, "windows.registry-cleaner-installed");
        Assert.Contains("Wise Registry Cleaner 99 is installed", cleaners.Details!["Found"]);
        Assert.Contains("CCleaner is installed (it includes a registry cleaner)", cleaners.Details["Found"]);
        Assert.DoesNotContain("Contoso", cleaners.Details["Found"]);
        Assert.Empty(cleaners.Actions);

        Assert.Equal(ReportedProgramKind.OptimizerSuite, BadTweakLeftoverNames.MatchProgram("Glary Utilities 6")!.Kind);
        Assert.Null(BadTweakLeftoverNames.MatchProgram("Contoso Notes"));
    }

    // ------------------------------------------------------------------ CPU and power

    [Fact]
    public async Task Processor_idle_states_switched_off_are_reported_with_the_restore_command()
    {
        using var folder = new TestFolder("idle", Area);
        var item = Item(await Find(new FakeRegistry(), folder, new FixedSystemProbe { ProcessorIdleDisableAc = 1, ProcessorIdleDisableDc = 0 }), "cpu.processor-idle-disabled");
        Assert.Contains("off when plugged in", item.Details!["Found"]);
        Assert.DoesNotContain("on battery", item.Details["Found"]);
        Assert.Contains("5d76a2ca-e8c0-402f-a133-2158492d58ad 0", item.Details["How to fix"]);
        Assert.Empty(item.Actions);
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { ProcessorIdleDisableAc = 0, ProcessorIdleDisableDc = 0 }), "cpu.processor-idle-disabled"));
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe()), "cpu.processor-idle-disabled"));
    }

    [Fact]
    public async Task Game_bar_removed_on_a_dual_ccd_x3d_is_reported_only_when_the_app_list_was_read()
    {
        using var folder = new TestFolder("x3d", Area);
        var x3d = new FixedSystemProbe { CpuKind = CpuKind.DualCcdX3D };
        var shell = new Dictionary<string, string> { ["Microsoft.Windows.ShellExperienceHost"] = "Microsoft.Windows.ShellExperienceHost_1.0.0.0_neutral__x" };
        var item = Item(await Find(new FakeRegistry(), folder, x3d, apps: shell), "cpu.x3d-game-bar-missing");
        Assert.Contains("Xbox Game Bar", item.Details!["Found"]);
        Assert.Empty(item.Actions);
        Assert.Equal(BadTweakCatalog.GameBarStorePage, Guidance("cpu.x3d-game-bar-missing").SettingsUri);
        Assert.Contains("Balanced", item.Details["Good to know"]);

        var withBar = new Dictionary<string, string>(shell) { ["Microsoft.XboxGamingOverlay"] = "Microsoft.XboxGamingOverlay_1.0.0.0_x64__x" };
        Assert.False(Has(await Find(new FakeRegistry(), folder, x3d, apps: withBar), "cpu.x3d-game-bar-missing"));
        Assert.False(Has(await Find(new FakeRegistry(), folder, x3d), "cpu.x3d-game-bar-missing"));             // app list unreadable
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { CpuKind = CpuKind.Hybrid }, apps: shell), "cpu.x3d-game-bar-missing"));
    }

    // ------------------------------------------------------------------ Windows and graphics

    [Fact]
    public async Task A_missing_webview2_runtime_on_windows_11_is_reported()
    {
        using var folder = new TestFolder("webview2", Area);
        var item = Item(await Find(new FakeRegistry(), folder, new FixedSystemProbe { WebView2Installed = false }), "windows.webview2-removed");
        Assert.Empty(item.Actions);
        Assert.Equal(BadTweakSources.WebView2Download, Guidance("windows.webview2-removed").SettingsUri);
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { WebView2Installed = false }, build: 19045), "windows.webview2-removed"));
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe { WebView2Installed = true }), "windows.webview2-removed"));
        Assert.False(Has(await Find(new FakeRegistry(), folder, new FixedSystemProbe()), "windows.webview2-removed"));
    }

    [Fact]
    public void Webview2_detection_reads_microsofts_pv_value_in_the_three_documented_places()
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        Assert.False(WindowsSystemProbe.ReadWebView2(new FakeRegistry()));
        Assert.True(WindowsSystemProbe.ReadWebView2(new FakeRegistry().LmText(@"SOFTWARE\WOW6432Node\" + client, "pv", "130.0.1.2")));
        Assert.True(WindowsSystemProbe.ReadWebView2(new FakeRegistry().Text(CoreHive.CurrentUser, @"Software\" + client, "pv", "130.0.1.2")));
        Assert.False(WindowsSystemProbe.ReadWebView2(new FakeRegistry().LmText(@"SOFTWARE\WOW6432Node\" + client, "pv", "0.0.0.0")));
        Assert.False(WindowsSystemProbe.ReadWebView2(new FakeRegistry().LmText(@"SOFTWARE\WOW6432Node\" + client, "pv", "")));
    }

    [Fact]
    public async Task Tcc_mode_value_is_explained_and_never_removed_by_pc_tidy()
    {
        using var folder = new TestFolder("tcc", Area);
        var item = Item(await Find(new FakeRegistry().Lm($@"{DisplayClass}\0000", "TCCSupported", 1), folder), "graphics.tcc-mode");
        Assert.Contains("TCCSupported = 1", item.Details!["Found"]);
        Assert.Empty(item.Actions);
        Assert.Null(RegistryCatalog.Find(CoreHive.LocalMachine, $@"{DisplayClass}\0000", "TCCSupported"));
        Assert.False(Has(await Find(new FakeRegistry().Lm($@"{DisplayClass}\0000", "TCCSupported", 0), folder), "graphics.tcc-mode"));
    }

    [Fact]
    public async Task The_anti_lag_plus_driver_is_information_only()
    {
        using var folder = new TestFolder("antilag", Area);
        var reg = new FakeRegistry().LmText($@"{DisplayClass}\0001", "RadeonSoftwareVersion", "23.10.1").LmText($@"{DisplayClass}\0001", "DriverDesc", "Example Radeon");
        var item = Item(await Find(reg, folder), "graphics.antilag-plus-driver");
        Assert.Contains("Example Radeon: AMD Software 23.10.1", item.Details!["Found"]);
        Assert.Contains("VAC ban", item.Reason);
        Assert.Empty(item.Actions);
        Assert.False(Has(await Find(new FakeRegistry().LmText($@"{DisplayClass}\0001", "RadeonSoftwareVersion", "23.10.10"), folder), "graphics.antilag-plus-driver"));
        Assert.False(Has(await Find(new FakeRegistry().LmText($@"{DisplayClass}\0001", "RadeonSoftwareVersion", "24.3.1"), folder), "graphics.antilag-plus-driver"));
    }

    // ------------------------------------------------------------------ Safe Mode and disabled devices (new repair offers)

    [Fact]
    public async Task A_leftover_safe_boot_flag_offers_start_windows_normally_again()
    {
        using var folder = new TestFolder("safeboot", Area);
        var boot = new BootConfigSnapshot(new[]
        {
            new BootElement(BadTweakCatalog.SafeBootElement, null, 0),
            new BootElement(BadTweakCatalog.SafeBootAlternateShellElement, true, null),
        });
        var r = await Find(new FakeRegistry(), folder, boot: boot);
        var item = Item(r, "boot.safeboot-left-on");
        Assert.Equal(new[] { "safeboot", "safebootalternateshell" }, item.Actions.Select(a => a.Args!["name"]).ToArray());
        Assert.All(item.Actions, a =>
        {
            Assert.Equal(ActionKind.RunSystemTool, a.Kind);
            Assert.Equal(ToolIds.BcdDeleteValue, a.ToolId);
            Assert.True(a.NeedsAdmin && a.IsSystemChange && a.Reversible);
            Assert.True(r.Context.Safety.Check(a).Allowed, a.Args!["name"]);
        });
        Assert.StartsWith("Start Windows normally again", item.Actions[0].Description);
        Assert.Contains("restart", item.Actions[0].Description);
        Assert.True(item.NeedsRestart && item.NeedsAdmin);
        Assert.False(item.DefaultSelected);
        Assert.False(item.Details!.ContainsKey("BitLocker"));

        // Only the safe-boot flag: one step.
        var only = Item(await Find(new FakeRegistry(), folder, boot: new BootConfigSnapshot(new[] { new BootElement(BadTweakCatalog.SafeBootElement, null, 1) })), "boot.safeboot-left-on");
        Assert.Equal("safeboot", Assert.Single(only.Actions).Args!["name"]);
    }

    [Fact]
    public async Task Disabled_system_devices_get_manual_steps_only_never_a_device_step()
    {
        using var folder = new TestFolder("devices", Area);
        // The shape the real DeviceReader returns: it enumerates GUID_DEVCLASS_SYSTEM only, so every device has the
        // System class (invented names and ids).
        var systemClass = new Guid("4d36e97d-e325-11ce-bfc1-08002be10318");
        var devices = new[]
        {
            new DisabledDevice("Invented timer", @"ACPI\PCTIDYTEST0103\0") { ClassGuid = systemClass },
            new DisabledDevice("Invented bus", @"PCI\VEN_PCTIDY&DEV_TEST\0") { ClassGuid = systemClass },
            new DisabledDevice("Invented unknown", @"ROOT\PCTIDYTEST\0000"),
        };
        var r = await Find(new FakeRegistry(), folder, new FixedSystemProbe { DisabledSystemDevices = devices });
        var item = Item(r, "windows.system-devices-disabled");
        Assert.Contains("Invented timer is disabled", item.Details!["Found"]);
        Assert.Contains("Invented bus is disabled", item.Details["Found"]);
        Assert.False(item.DefaultSelected);

        // System-class devices stay protected: no DeviceSetEnabled step, only the manual steps.
        Assert.Empty(item.Actions);
        Assert.DoesNotContain(r.Result.AllItems.SelectMany(i => i.Actions), a => a.Kind == ActionKind.DeviceSetEnabled);
        var how = item.Details["How to fix"];
        Assert.Contains("Device Manager", how);
        Assert.Contains("right-click the device", how);
        Assert.Contains("Enable device", how);
        Assert.Contains("didn't turn it off on purpose", how);
    }

    [Fact]
    public void The_finder_has_no_device_enable_offer_left()
    {
        // The dead offer path was removed (code review H1): no fix type in the catalog plans a device step.
        Assert.Null(typeof(BadFix).Assembly.GetTypes().FirstOrDefault(t => t.Name == "DeviceEnableFix"));
        Assert.Null(typeof(BadFix).Assembly.GetTypes().FirstOrDefault(t => t.Name == "BadTweakDeviceClasses"));
    }

    // ------------------------------------------------------------------ every HARMFUL row is covered

    /// <summary>The HARMFUL rows of "FPS boost tweaks - do they work" §4 (plus the comparison page's §8.3 extras).</summary>
    private static readonly string[] HarmfulRows =
    {
        "Spectre/Meltdown off", "useplatformclock", "disabledynamictick on laptops", "Idle disable / C-states off",
        "Core parking off on dual-CCD X3D", "Game Bar removal on dual-CCD X3D", "Priority Realtime", "Autotuning off",
        "BBR2 on Windows 11 24H2", "IPv6 off", "Jumbo frames", "Page file off", "Fixed/disabled page file", "TRIM off",
        "Registry cleaners", "Windows Update / BITS / WpnService / DoSvc off", "BIOS vars from Windows / UEFI variables protection off",
        "xHCI IMOD (RWEverything)", "Read-only on Fortnite ini", "-onethread / -nothreading", "Anti-Lag+", "TCCSupported",
        "Edge / WebView2 removal", "Defender real-time off", "PC optimizer suites (Advanced SystemCare)", "SVM/VT-x/IOMMU off",
        "Tamper Protection off", "Services mass disabling (preset leftovers)",
    };

    [Fact]
    public void Every_harmful_row_has_a_finder_check_another_section_or_a_reason()
    {
        foreach (var row in HarmfulRows)
        {
            var c = BadTweakCoverage.Find(row);
            Assert.True(c is not null, $"no coverage entry for '{row}'");
            Assert.True(c!.IsCovered, row);
            foreach (var id in c.CheckIds) Assert.True(BadTweakCatalog.Find(id) is not null, $"{row}: unknown check {id}");
            if (c.CheckIds.Count == 0) Assert.False(string.IsNullOrWhiteSpace(c.CoveredElsewhere ?? c.NotDetectable), row);
            if (c.NotDetectable is { } why) Assert.DoesNotContain('\n', why); // one line
        }
        Assert.Equal(HarmfulRows.Length, BadTweakCoverage.HarmfulRows.Count);
        // The two rows the Games section covers really are there, marked Harmful.
        foreach (var token in new[] { "-onethread", "-nothreading" })
            Assert.Contains(LaunchOptionFacts.All, f => f.Token == token && f.Verdict == PcTidy.Catalog.Games.TweakVerdict.Harmful);
    }

    [Fact]
    public void New_checks_only_ever_reset_to_the_windows_default_or_explain()
    {
        var ids = new[] { "security.defender-realtime-off", "network.bbr2-congestion", "network.jumbo-frames", "windows.bios-tool-leftovers",
            "windows.optimizer-suite-installed", "windows.registry-cleaner-installed", "cpu.processor-idle-disabled", "cpu.x3d-game-bar-missing",
            "windows.webview2-removed", "graphics.tcc-mode", "graphics.antilag-plus-driver" };
        foreach (var id in ids)
        {
            var check = BadTweakCatalog.Find(id);
            Assert.True(check is not null, id);
            var fixes = check!.Signals.SelectMany(s => s.Fixes).ToList();
            Assert.All(fixes, f => Assert.True(f is GuidanceFix || f is RegistrySetFoundFix { Data: "1514" }, $"{id}: {f}"));
            Assert.All(check.Sources, s => Assert.StartsWith("https://", s));
            Assert.False(string.IsNullOrWhiteSpace(check.WhyBad), id);
        }
    }
}
