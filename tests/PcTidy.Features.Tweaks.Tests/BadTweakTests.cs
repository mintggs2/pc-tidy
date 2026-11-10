using Microsoft.Win32;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>Bad-tweak detection on a fake registry (test area), a fake hosts file and fake boot/task/probe readers.</summary>
public class BadTweakTests
{
    private const string Defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    private static async Task<ScanResult> Find(TestRegistry reg, TestFolder folder, FixedSystemProbe? probe = null, BootConfigSnapshot? boot = null,
        IReadOnlyDictionary<string, bool>? tasks = null)
    {
        var env = TestEnv.Create(reg, folder.Path, probe, boot ?? BootConfigSnapshot.Empty, tasks: tasks);
        return await new BadTweakScanner(_ => env).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);
    }

    private static ScanItem Item(ScanResult r, string checkId) => Assert.Single(r.AllItems, i => i.Id == "badtweaks." + checkId);

    [Fact]
    public async Task Clean_pc_has_no_findings()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var r = await Find(reg, folder);
        Assert.Empty(r.AllItems);
        Assert.Contains(r.Notes, n => n.Contains("No bad tweaks found"));
    }

    [Fact]
    public async Task Defender_turned_off_by_policy_is_found_with_a_guided_restore()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        reg.Lm(Defender, "DisableAntiSpyware", 1);
        reg.Lm(Defender, "DisableAntiVirus", 1);
        reg.Service("WinDefend", start: 4);
        reg.Service("WdNisSvc", start: 3); // default: not flagged

        var item = Item(await Find(reg, folder), "security.defender-off");
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.True(item.NeedsAdmin && item.NeedsRestart);
        Assert.Contains("DisableAntiSpyware = 1", item.Details!["Found"]);
        Assert.Contains("Service 'WinDefend' is Disabled", item.Details["Found"]);
        Assert.False(string.IsNullOrWhiteSpace(item.Details["Windows default"]));

        var deletes = item.Actions.Where(a => a.Kind == ActionKind.RegistryDeleteValue).ToList();
        Assert.Equal(new[] { "DisableAntiSpyware", "DisableAntiVirus" }, deletes.Select(d => d.Registry!.ValueName).ToArray());
        Assert.All(deletes, d => Assert.Equal(reg.Physical(CoreHive.LocalMachine, Defender), d.Registry!.KeyPath));
        var service = Assert.Single(item.Actions, a => a.Kind == ActionKind.ServiceSetStartType);
        Assert.Equal("WinDefend", service.ServiceName);
        Assert.Equal(ServiceStart.Automatic, service.ServiceStartType);
        Assert.All(item.Actions, a => Assert.True(a.NeedsAdmin && a.IsSystemChange && a.Reversible && a.FeatureId == "tweaks.badtweaks"));
        Assert.Equal("Security turned off", (await Find(reg, folder)).Groups.Single(g => g.Id == "tweaks.badtweaks.security").Title);
    }

    [Fact]
    public async Task Third_party_antivirus_is_mentioned_next_to_the_defender_finding()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        reg.Lm(Defender, "DisableAntiSpyware", 1);
        var item = Item(await Find(reg, folder, new FixedSystemProbe { ThirdPartyAntivirus = new[] { "Example AV" } }), "security.defender-off");
        Assert.Contains("Example AV", item.Details!["Other antivirus"]);
    }

    [Fact]
    public async Task Value_tests_match_only_the_bad_values()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        const string mm = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        const string profile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        reg.Lm(mm, "FeatureSettingsOverride", 72);   // turns EXTRA protections on: fine
        reg.Lm(profile, "SystemResponsiveness", 20);   // the default
        reg.Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x20); // supported "prefer IPv4"
        var clean = await Find(reg, folder);
        Assert.DoesNotContain(clean.AllItems, i => i.Id is "badtweaks.security.spectre-mitigations-off" or "badtweaks.scheduler.system-responsiveness" or "badtweaks.network.ipv6-disabled");

        reg.Lm(mm, "FeatureSettingsOverride", 3);
        reg.Lm(mm, "FeatureSettingsOverrideMask", 3);
        reg.Lm(profile, "SystemResponsiveness", 100);
        reg.Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF);
        reg.Set(CoreHive.LocalMachine, mm, "PagingFiles", new[] { "" }, RegistryValueKind.MultiString);
        var bad = await Find(reg, folder);

        var spectre = Item(bad, "security.spectre-mitigations-off");
        Assert.Equal(new[] { "FeatureSettingsOverride", "FeatureSettingsOverrideMask" }, spectre.Actions.Select(a => a.Registry!.ValueName).ToArray());
        var mmcss = Assert.Single(Item(bad, "scheduler.system-responsiveness").Actions);
        Assert.Equal(ActionKind.RegistrySetValue, mmcss.Kind);
        Assert.Equal("20", mmcss.Registry!.Data);
        Assert.Equal(ActionKind.RegistryDeleteValue, Assert.Single(Item(bad, "network.ipv6-disabled").Actions).Kind);
        var pagefile = Assert.Single(Item(bad, "storage.pagefile-off").Actions);
        Assert.Equal(RegistryKind.MultiString, pagefile.Registry!.Kind);
        Assert.Equal(@"?:\pagefile.sys", pagefile.Registry.Data);
    }

    [Fact]
    public async Task Hosts_lines_blocking_microsoft_servers_are_commented_out_and_nothing_else()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var env = TestEnv.Create(reg, folder.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(env.HostsPath)!);
        File.WriteAllLines(env.HostsPath, new[]
        {
            "# Copyright (c) Microsoft Corp.",
            "127.0.0.1 localhost",
            "0.0.0.0 update.microsoft.com   # added by an optimizer",
            "0.0.0.0 vortex.data.microsoft.com",
            "0.0.0.0 fe2.update.microsoft.com",
            "192.168.1.5 nas.local",
        });

        var item = Item(await Find(reg, folder), "network.hosts-blocks-microsoft");
        var step = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.HostsCommentOut, step.Kind);
        Assert.Equal(env.HostsPath, step.SourcePath);
        Assert.Equal(new[] { "0.0.0.0 update.microsoft.com   # added by an optimizer", "0.0.0.0 fe2.update.microsoft.com" }, step.Args!["lines"].Split('\n'));
        Assert.Contains("other blocking lines", item.Details!["Found"]);
    }

    [Fact]
    public async Task Boot_leftovers_are_read_from_the_boot_entry_and_removed_with_a_bitlocker_warning()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var boot = new BootConfigSnapshot(new[]
        {
            new BootElement(0x260000A2, true, null),   // useplatformclock
            new BootElement(0x16000049, true, null),   // testsigning
            new BootElement(0x25000020, null, 0),      // nx OptIn: fine
            new BootElement(0x260000A5, false, null),  // disabledynamictick off: fine
        });
        var r = await Find(reg, folder, boot: boot);

        var hpet = Item(r, "boot.useplatformclock");
        var step = Assert.Single(hpet.Actions);
        Assert.Equal(ActionKind.RunSystemTool, step.Kind);
        Assert.Equal(ToolIds.BcdDeleteValue, step.ToolId);
        Assert.Equal("useplatformclock", step.Args!["name"]);
        Assert.False(hpet.Details!.ContainsKey("BitLocker"));

        var testSigning = Item(r, "security.test-signing");
        Assert.Contains("recovery key", testSigning.Details!["BitLocker"]);
        Assert.Equal("testsigning", Assert.Single(testSigning.Actions).Args!["name"]);
        Assert.DoesNotContain(r.AllItems, i => i.Id is "badtweaks.security.dep-off" or "badtweaks.boot.disabledynamictick");
    }

    [Fact]
    public async Task Unreadable_boot_settings_are_reported_not_guessed()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var env = TestEnv.Create(reg, folder.Path) ;
        var unreadable = new TweakEnvironment
        {
            Registry = env.Registry, Probe = env.Probe, Boot = new FixedBootConfigReader(null), Apps = env.Apps, Tasks = env.Tasks,
            HostsPath = env.HostsPath, LocalAppData = env.LocalAppData, WindowsDir = env.WindowsDir, IsRealPc = true,
        };
        var r = await new BadTweakScanner(_ => unreadable).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);
        Assert.Contains(r.Notes, n => n.Contains("administrator"));
    }

    [Fact]
    public async Task Tasks_services_autotuning_and_admin_account_findings()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        reg.Service("wuauserv", start: 4);
        reg.Service("Themes", start: 4);
        var r = await Find(reg, folder,
            probe: new FixedSystemProbe { TcpAutotuningLevel = 0, BuiltInAdministratorEnabled = true, BuiltInAdministratorName = "Administrateur", CoreParkingMinCoresAc = 100, CpuKind = CpuKind.Hybrid },
            tasks: new Dictionary<string, bool> { [@"\Microsoft\Windows\Defrag\ScheduledDefrag"] = false, [@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"] = true });

        var task = Assert.Single(Item(r, "storage.optimize-schedule-off").Actions);
        Assert.Equal(ActionKind.ScheduledTaskSetEnabled, task.Kind);
        Assert.Equal(true, task.Enable);
        Assert.DoesNotContain(r.AllItems, i => i.Id == "badtweaks.update.tasks-disabled");

        var update = Assert.Single(Item(r, "update.services-disabled").Actions);
        Assert.Equal("wuauserv", update.ServiceName);
        Assert.Equal(ServiceStart.Manual, update.ServiceStartType);
        Assert.Equal(ServiceStart.Automatic, Assert.Single(Item(r, "services.themes-disabled").Actions).ServiceStartType);

        var autotuning = Assert.Single(Item(r, "network.autotuning-off").Actions);
        Assert.Equal(ToolIds.NetshAutotuningNormal, autotuning.ToolId);

        var admin = Item(r, "security.builtin-administrator");
        Assert.Empty(admin.Actions); // guidance only: PC Tidy doesn't change accounts
        // The account's name differs by language: the command uses the name this PC reported, never a hard-coded "administrator".
        Assert.Contains("net user \"Administrateur\" /active:no", admin.Details!["Found"]);
        Assert.Contains("malware", admin.Reason);
        Assert.Contains("wrong cores", Item(r, "cpu.core-parking-off").Details!["Found"]);
    }

    [Fact]
    public async Task Unparked_cores_are_only_a_finding_on_x3d_and_hybrid_cpus()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var plain = await Find(reg, folder, new FixedSystemProbe { CoreParkingMinCoresAc = 100, CpuKind = CpuKind.None });
        Assert.DoesNotContain(plain.AllItems, i => i.Id == "badtweaks.cpu.core-parking-off"); // High performance plans do this on purpose
        var x3d = await Find(reg, folder, new FixedSystemProbe { CoreParkingMinCoresAc = 100, CpuKind = CpuKind.DualCcdX3D });
        Assert.Contains(x3d.AllItems, i => i.Id == "badtweaks.cpu.core-parking-off");
    }

    [Fact]
    public async Task Broken_services_pointing_at_missing_programs_are_listed()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var present = Path.Combine(folder.Path, "present.exe");
        File.WriteAllText(present, "x");
        reg.Service("GoneUpdater", start: 2, imagePath: $"\"{Path.Combine(folder.Path, "Gone App", "updater.exe")}\" /service");
        reg.Service("FineService", start: 3, imagePath: present);
        reg.Service("DisabledGone", start: 4, imagePath: Path.Combine(folder.Path, "nothing.exe"));

        var item = Item(await Find(reg, folder), "services.broken");
        Assert.Contains("'GoneUpdater'", item.Details!["Found"]);
        Assert.DoesNotContain("FineService", item.Details["Found"]);
        Assert.DoesNotContain("DisabledGone", item.Details["Found"]);
        Assert.Empty(item.Actions);
    }

    [Fact]
    public async Task Managed_policies_are_listed_one_by_one_and_unknown_ones_are_information_only()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        reg.Lm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
        reg.Lm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "SomeUnknownPolicy", 1);
        reg.Lm(@"SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
        var r = await Find(reg, folder, new FixedSystemProbe { IsDomainJoined = true });

        var known = Item(r, "policy.telemetry-level");
        Assert.Equal(ActionKind.RegistryDeleteValue, Assert.Single(known.Actions).Kind);
        Assert.Contains("Managed by your organization", known.Reason);
        var other = Item(r, "policy.other");
        Assert.Contains("SomeUnknownPolicy", other.Details!["Found"]);
        Assert.DoesNotContain("AllowTelemetry", other.Details["Found"]); // already its own row
        Assert.Empty(other.Actions);
        Assert.Contains("HubsSidebarEnabled", Item(r, "policy.edge").Details!["Found"]);
        Assert.Contains(r.Notes, n => n.Contains("work network"));
        Assert.Contains(r.Groups, g => g.Id == "tweaks.badtweaks.policies");
    }

    [Fact]
    public async Task Fake_update_server_is_flagged_but_a_real_company_server_is_not()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        const string wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        reg.Lm(wu + @"\AU", "UseWUServer", 1);
        reg.Text(CoreHive.LocalMachine, wu, "WUServer", "http://127.0.0.1:8530");
        var item = Item(await Find(reg, folder), "update.fake-update-server");
        Assert.Equal(new[] { "UseWUServer", "WUServer" }, item.Actions.Select(a => a.Registry!.ValueName).ToArray());

        reg.Text(CoreHive.LocalMachine, wu, "WUServer", "http://wsus.example.com:8530");
        Assert.DoesNotContain((await Find(reg, folder)).AllItems, i => i.Id == "badtweaks.update.fake-update-server");
    }

    [Fact]
    public async Task Stale_roblox_fastflag_file_goes_to_quarantine()
    {
        using var reg = new TestRegistry("finder");
        using var folder = new TestFolder("finder");
        var env = TestEnv.Create(reg, folder.Path);
        var file = Path.Combine(env.LocalAppData, "Roblox", "Versions", "version-abc", "ClientSettings", "ClientAppSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"DFIntTaskSchedulerTargetFps\": 9999}");
        var step = Assert.Single(Item(await Find(reg, folder), "games.roblox-fastflags").Actions);
        Assert.Equal(ActionKind.QuarantinePath, step.Kind);
        Assert.Equal(file, step.SourcePath);
    }

    [Fact]
    public void Hosts_parser_ignores_comments_and_non_blocking_lines()
    {
        var f = HostsFile.Scan(new[] { "#0.0.0.0 update.microsoft.com", "  ", "0.0.0.0 example.com", "127.0.0.1 login.live.com", "10.0.0.1 update.microsoft.com" });
        Assert.Equal(new[] { "127.0.0.1 login.live.com" }, f.CriticalLines);
        Assert.Equal(2, f.TotalBlockLines);
        Assert.False(HostsFile.IsCritical("notmicrosoft.com"));
        Assert.True(HostsFile.IsCritical("sls.microsoft.com."));
    }

    /// <summary>
    /// The owner's PC has Defender switched off by policy. This reads the REAL registry (read-only) and, when the policy
    /// is there, proves the finder reports it. On a PC without the policy it only checks the scan runs read-only.
    /// </summary>
    [Fact]
    public async Task Real_pc_read_only_scan_reports_a_defender_policy_when_present()
    {
        using var folder = new TestFolder("realpc");
        var real = new RegistryReader(RegistryRedirect.None);
        var locations = new PcTidy.Safety.WindowsKnownLocations(new AppSettings { DataRoot = Path.Combine(folder.Path, "Data") });
        var env = TweakEnvironment.Real(locations, null);
        var result = await new BadTweakScanner(_ => env).ScanAsync(TestEnv.Context(folder.Path), null, CancellationToken.None);

        var policy = real.Read(CoreHive.LocalMachine, Defender, "DisableAntiSpyware") ?? real.Read(CoreHive.LocalMachine, Defender, "DisableAntiVirus");
        if (policy is { Data: "1" })
        {
            var item = Item(result, "security.defender-off");
            Assert.Contains(item.Actions, a => a.Kind == ActionKind.RegistryDeleteValue && a.Registry!.KeyPath == Defender);
            Assert.True(item.NeedsAdmin && item.NeedsRestart);
        }
        Assert.All(result.AllItems, i => Assert.False(i.DefaultSelected));
    }
}
