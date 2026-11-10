using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Running;
using PcTidy.Features.Speed.Sources;
using PcTidy.Features.Speed.Startup;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Speed.Tests;

public class ServiceRuleTests
{
    private const string Win = @"C:\Windows";

    [Theory]
    [InlineData(0x10, 2, true)]   // own process, automatic
    [InlineData(0x20, 2, true)]   // shared process, automatic
    [InlineData(0x110, 2, true)]  // interactive own process
    [InlineData(0x10, 3, false)]  // manual
    [InlineData(0x10, 4, false)]  // disabled
    [InlineData(0x1, 2, false)]   // kernel driver
    [InlineData(0x2, 0, false)]   // file-system driver
    [InlineData(0x1, 1, false)]   // system-start driver
    [InlineData(0x50, 2, false)]  // per-user service template
    [InlineData(0xE0, 2, false)]  // per-user service instance
    public void Only_automatic_process_services_qualify(int type, int start, bool expected)
        => Assert.Equal(expected, ServiceRules.IsAutoStartProcessService(type, start));

    [Fact]
    public void Microsoft_and_Windows_services_are_not_listed()
    {
        Assert.Equal(ServiceEligibility.Hidden, ServiceRules.Evaluate("SomeSvc", FixedFileFacts.Microsoft(@"C:\Program Files\MS\svc.exe"), Win).Eligibility);
        var inWindows = new FileFacts(@"C:\Windows\System32\thing.dll", true, "Contoso", null, SignatureState.Unsigned, null);
        Assert.Equal(ServiceEligibility.Hidden, ServiceRules.Evaluate("Thing", inWindows, Win).Eligibility);
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("wuauserv")]
    [InlineData("BITS")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService")]
    [InlineData("vgc")]
    [InlineData("XboxGipSvc")]
    public void Protected_services_are_shown_but_never_offered(string name)
    {
        var facts = new FileFacts(@"C:\Program Files\X\svc.exe", true, "Contoso", null, SignatureState.Verified, "Contoso");
        var (eligibility, why) = ServiceRules.Evaluate(name, facts, Win);
        Assert.Equal(ServiceEligibility.ShownOnly, eligibility);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Theory]
    [InlineData("ESET, spol. s r.o.")]
    [InlineData("Malwarebytes")]
    [InlineData("Riot Games, Inc.")]
    [InlineData("BattlEye Innovations e.K.")]
    public void Security_and_anti_cheat_publishers_are_never_offered(string company)
    {
        var facts = new FileFacts(@"C:\Program Files\X\svc.exe", true, company, null, SignatureState.Verified, company);
        Assert.Equal(ServiceEligibility.ShownOnly, ServiceRules.Evaluate("VendorSvc", facts, Win).Eligibility);
    }

    [Fact]
    public void A_file_that_only_claims_to_be_Microsoft_is_not_offered()
    {
        var fake = new FileFacts(@"C:\ProgramData\svc.exe", true, "Microsoft Corporation", null, SignatureState.Unsigned, null);
        Assert.False(fake.IsMicrosoft);
        Assert.Equal(ServiceEligibility.ShownOnly, ServiceRules.Evaluate("Updater", fake, Win).Eligibility);
    }

    [Fact]
    public async Task Services_scan_offers_Manual_only_never_Disabled_and_skips_drivers()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var exe = sb.MakeFile(@"C\Program Files\ToolCo\toolsvc.exe");
        const string services = @"SYSTEM\CurrentControlSet\Services";
        reg.Set(Hive.LocalMachine, services + @"\ToolCoSvc", "Type", 0x10);
        reg.Set(Hive.LocalMachine, services + @"\ToolCoSvc", "Start", 2);
        reg.Set(Hive.LocalMachine, services + @"\ToolCoSvc", "ImagePath", $"\"{exe}\" -service");
        reg.Set(Hive.LocalMachine, services + @"\ToolCoSvc", "DisplayName", "ToolCo Helper Service");
        reg.Set(Hive.LocalMachine, services + @"\tooldrv", "Type", 0x1);
        reg.Set(Hive.LocalMachine, services + @"\tooldrv", "Start", 2);
        reg.Set(Hive.LocalMachine, services + @"\tooldrv", "ImagePath", @"\SystemRoot\System32\drivers\tooldrv.sys");
        reg.Set(Hive.LocalMachine, services + @"\ManualSvc", "Type", 0x10);
        reg.Set(Hive.LocalMachine, services + @"\ManualSvc", "Start", 3);
        reg.Set(Hive.LocalMachine, services + @"\ManualSvc", "ImagePath", exe);
        reg.Set(Hive.LocalMachine, services + @"\EasyAntiCheat_EOS", "Type", 0x10);
        reg.Set(Hive.LocalMachine, services + @"\EasyAntiCheat_EOS", "Start", 2);
        reg.Set(Hive.LocalMachine, services + @"\EasyAntiCheat_EOS", "ImagePath", exe);

        var result = await new StartupScanner(_ => Env.Fake(registry: reg.View)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var group = result.Groups.Single(g => g.Id == StartupScanner.ServicesGroup);
        Assert.Equal(2, group.Items.Count);
        var tool = group.Items.Single(i => i.Title == "ToolCo Helper Service");
        var a = Assert.Single(tool.Actions);
        Assert.Equal(ActionKind.ServiceSetStartType, a.Kind);
        Assert.Equal(ServiceStart.Manual, a.ServiceStartType);
        Assert.Equal("ToolCoSvc", a.ServiceName);
        Assert.True(a.NeedsAdmin);
        Assert.True(a.IsSystemChange);
        Assert.False(tool.DefaultSelected);
        var eac = group.Items.Single(i => i.Title == "EasyAntiCheat_EOS");
        Assert.Empty(eac.Actions);
        Assert.NotNull(eac.DisabledReason);
        Assert.DoesNotContain(result.AllItems, i => i.Actions.Any(x => x.ServiceStartType == ServiceStart.Disabled));
    }
}

public class BackgroundAppTests
{
    private const string Win = @"C:\Windows";

    private static ProcessSnapshot P(string name, string? path, int session = 1, bool window = false, int pid = 500)
        => new(pid, name, path, session, DateTime.UtcNow.AddMinutes(-5), TimeSpan.FromSeconds(1), 10 << 20, 0, window, window ? name : null);

    [Theory]
    [InlineData("explorer")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("dwm")]
    [InlineData("MsMpEng")]
    [InlineData("SecurityHealthSystray")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService_x64")]
    [InlineData("vgtray")]
    [InlineData("PcTidy")]
    public void Deny_listed_processes_are_never_closable(string name)
    {
        var p = P(name, $@"C:\Program Files\X\{name}.exe", window: true);
        Assert.Equal(AppClass.Protected, BackgroundAppRules.Classify(p, FileFacts.None(null), 1, Win));
    }

    [Fact]
    public void Services_session_Windows_folder_and_security_publishers_are_protected()
    {
        Assert.Equal(AppClass.Protected, BackgroundAppRules.Classify(P("vendorsvc", @"C:\Program Files\V\vendorsvc.exe", session: 0), FileFacts.None(null), 1, Win));
        Assert.Equal(AppClass.Protected, BackgroundAppRules.Classify(P("helperx", @"C:\Windows\System32\helperx.exe", window: true), FileFacts.None(null), 1, Win));
        var av = new FileFacts(@"C:\Program Files\K\ui.exe", true, "AO Kaspersky Lab", null, SignatureState.Verified, "AO Kaspersky Lab");
        Assert.Equal(AppClass.Protected, BackgroundAppRules.Classify(P("ui", av.Path, window: true), av, 1, Win));
        // Ordinary Windows apps you open yourself can be closed.
        Assert.Equal(AppClass.Closable, BackgroundAppRules.Classify(P("notepad", @"C:\Windows\System32\notepad.exe", window: true), FileFacts.None(null), 1, Win));
    }

    [Fact]
    public void Only_apps_with_a_window_in_your_session_are_closable()
    {
        Assert.Equal(AppClass.Closable, BackgroundAppRules.Classify(P("editor", @"C:\Apps\editor.exe", window: true), FileFacts.None(null), 1, Win));
        Assert.Equal(AppClass.Background, BackgroundAppRules.Classify(P("trayhelper", @"C:\Apps\trayhelper.exe"), FileFacts.None(null), 1, Win));
        Assert.Equal(AppClass.Background, BackgroundAppRules.Classify(P("editor", @"C:\Apps\editor.exe", session: 2, window: true), FileFacts.None(null), 1, Win));
    }

    [Fact]
    public async Task Running_now_measures_cpu_groups_by_app_and_offers_close_only_for_windows()
    {
        using var sb = new SpeedSandbox();
        var start = DateTime.UtcNow.AddMinutes(-10);
        IReadOnlyList<ProcessSnapshot> Snap(int call) => new[]
        {
            // A browser with three processes, one with a window.
            new ProcessSnapshot(100, "browser", @"C:\Apps\Browser\browser.exe", 1, start, TimeSpan.FromMilliseconds(1000 + call * 400), 300 << 20, 0, true, "News - Browser"),
            new ProcessSnapshot(101, "browser", @"C:\Apps\Browser\browser.exe", 1, start, TimeSpan.FromMilliseconds(500 + call * 200), 200 << 20, 0, false, null),
            new ProcessSnapshot(102, "browser", @"C:\Apps\Browser\browser.exe", 1, start, TimeSpan.FromMilliseconds(100), 100 << 20, 0, false, null),
            new ProcessSnapshot(200, "explorer", @"C:\Windows\explorer.exe", 1, start, TimeSpan.FromMilliseconds(50 + call * 10), 90 << 20, 0, true, "File Explorer"),
            new ProcessSnapshot(300, "synchelper", @"C:\Apps\Sync\synchelper.exe", 1, start, TimeSpan.FromMilliseconds(10), 20 << 20, 0, false, null),
        };
        var env = Env.Fake(processes: new FixedProcessSource(Snap));
        var apps = RunningAppsScanner.Measure(Snap(0), Snap(1), TimeSpan.FromSeconds(2), 4, env, @"C:\Windows");

        var browser = apps.Single(a => a.Name == "News - Browser");
        Assert.Equal(3, browser.Processes.Count);
        Assert.Equal(600 << 20, browser.PrivateBytes);
        Assert.Equal(7.5, browser.CpuPercent, 3); // 600 ms of CPU over 2 s on 4 processors
        Assert.Equal(AppClass.Closable, browser.Class);
        Assert.Equal(AppClass.Protected, apps.Single(a => a.Processes[0].Name == "explorer").Class);
        Assert.Equal(AppClass.Background, apps.Single(a => a.Processes[0].Name == "synchelper").Class);

        var result = await new RunningAppsScanner(_ => env).ScanAsync(sb.Context(), null, CancellationToken.None);
        var close = result.Groups.Single(g => g.Id == RunningAppsScanner.ClosableGroup).Items.Single();
        var action = Assert.Single(close.Actions); // only the process with a window is asked
        Assert.Equal(ActionKind.CloseApp, action.Kind);
        Assert.Equal("100", action.Args!["pid"]);
        Assert.Equal("browser", action.Args["processName"]);
        Assert.False(action.Reversible);
        Assert.False(close.DefaultSelected);
        Assert.True(sb.Host.Safety.Check(action).Allowed);
        Assert.All(result.Groups.Where(g => g.Id != RunningAppsScanner.ClosableGroup).SelectMany(g => g.Items), i =>
        {
            Assert.Empty(i.Actions);
            Assert.NotNull(i.DisabledReason);
        });
        Assert.DoesNotContain(result.AllItems, i => i.Actions.Any(a => a.Args?["processName"] == "explorer"));

        // The real guard refuses explorer too (two walls).
        var explorer = SpeedPlanItems.CloseApp("File Explorer", 200, "explorer", null);
        Assert.False(sb.Host.Safety.Check(explorer).Allowed);
    }
}
