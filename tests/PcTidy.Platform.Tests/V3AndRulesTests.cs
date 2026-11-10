using PcTidy.Core;
using Microsoft.Win32;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Platform.Tests.V2Items;

namespace PcTidy.Platform.Tests;

/// <summary>v3 handlers, the aligned service / task rules, OpenExternal targets and the visual-effects follow-up.</summary>
public class V3AndRulesTests
{
    private const string AppPath = @"C:\Program Files\PC Tidy\PcTidy.exe";

    // ---------------------------------------------------------------- own scheduled tasks

    private static PlanItem Create(string args = "--scheduled-scan", string schedule = "weekly", string day = "Sunday", string time = "09:30") =>
        Item(ActionKind.ScheduledTaskCreate, new() { ["name"] = "Weekly report", ["schedule"] = schedule, ["day"] = day, ["time"] = time, ["arguments"] = args });

    [Fact]
    public async Task Own_task_runs_only_pc_tidy_with_report_switches_and_undo_deletes_it()
    {
        using var sandbox = new TestSandbox();
        var tasks = new FakeOwnTasks();
        var handler = new ScheduledTaskCreateHandler(tasks, AppPath);

        var done = await handler.ExecuteAsync(Steps.Of(Create("--scheduled-scan --checkup --checkup")), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var xml = tasks.Tasks["Weekly report"];
        Assert.True(OwnTaskDefinition.IsOwnDefinition(xml, AppPath));
        Assert.Contains("<Arguments>--scheduled-scan --checkup</Arguments>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        Assert.Contains("<Sunday />", xml);

        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Empty(tasks.Tasks);
    }

    [Fact]
    public async Task Replacing_an_own_task_saves_the_old_definition_and_undo_puts_it_back()
    {
        using var sandbox = new TestSandbox();
        var tasks = new FakeOwnTasks();
        var handler = new ScheduledTaskCreateHandler(tasks, AppPath);
        await handler.ExecuteAsync(Steps.Of(Create(schedule: "daily", day: "")), sandbox.Context(), default);
        var original = tasks.Tasks["Weekly report"];

        var replaced = await handler.ExecuteAsync(Steps.Of(Create(schedule: "monthly", day: "1")), sandbox.Context(), default);
        Assert.Equal(original, replaced.UndoData![ScheduledTaskCreateHandler.PriorXmlData]);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(replaced, sandbox.Context(), default)).Status);
        Assert.Equal(original, tasks.Tasks["Weekly report"]);

        // A saved definition that runs anything else is never put back.
        var evil = original.Replace(AppPath, @"C:\Users\Public\evil.exe");
        var forged = replaced with { UndoData = new() { [ScheduledTaskCreateHandler.PriorXmlData] = evil } };
        Assert.Equal(StepStatus.UndoFailed, (await handler.UndoAsync(forged, sandbox.Context(), default)).Status);
    }

    [Theory]
    [InlineData("--scheduled-scan --apply-all", "weekly", "Sunday", "09:30")]
    [InlineData("", "weekly", "Sunday", "09:30")]
    [InlineData("--checkup", "hourly", "", "09:30")]
    [InlineData("--checkup", "weekly", "Funday", "09:30")]
    [InlineData("--checkup", "monthly", "31", "09:30")]
    [InlineData("--checkup", "daily", "", "9:30pm")]
    public async Task Own_task_arguments_and_schedules_are_validated(string args, string schedule, string day, string time)
    {
        using var sandbox = new TestSandbox();
        var tasks = new FakeOwnTasks();
        var result = await new ScheduledTaskCreateHandler(tasks, AppPath).ExecuteAsync(Steps.Of(Create(args, schedule, day, time)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Empty(tasks.Tasks);
    }

    [Fact]
    public async Task Own_task_refuses_a_bad_name_a_non_pc_tidy_program_and_the_admin_helper()
    {
        using var sandbox = new TestSandbox();
        var tasks = new FakeOwnTasks();
        var bad = Item(ActionKind.ScheduledTaskCreate, new() { ["name"] = @"..\Microsoft\x", ["schedule"] = "logon", ["arguments"] = "--checkup" });
        Assert.Equal(StepStatus.Skipped, (await new ScheduledTaskCreateHandler(tasks, AppPath).ExecuteAsync(Steps.Of(bad), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await new ScheduledTaskCreateHandler(tasks, @"C:\Tools\other.exe").ExecuteAsync(Steps.Of(Create()), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await new ScheduledTaskCreateHandler(tasks, AppPath).ExecuteAsync(Steps.Of(Create()), sandbox.Context(elevated: true), default)).Status);
        Assert.Empty(tasks.Tasks);
    }

    [Fact]
    public async Task Own_task_delete_saves_the_definition_and_undo_recreates_it()
    {
        using var sandbox = new TestSandbox();
        var tasks = new FakeOwnTasks();
        await new ScheduledTaskCreateHandler(tasks, AppPath).ExecuteAsync(Steps.Of(Create()), sandbox.Context(), default);
        var original = tasks.Tasks["Weekly report"];
        var handler = new ScheduledTaskDeleteHandler(tasks, AppPath);
        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ScheduledTaskDelete, new() { ["name"] = "Weekly report" })), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Empty(tasks.Tasks);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Equal(original, tasks.Tasks["Weekly report"]);
    }

    // ---------------------------------------------------------------- start with Windows (real writes, test key only)

    [Fact]
    public async Task Start_with_windows_writes_only_the_pc_tidy_run_value_and_undo_restores_it()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var run = key.Sub("Run");
        var approved = key.Sub("Approved");
        using (var k = Registry.CurrentUser.CreateSubKey(run)) k.SetValue("Other", "x");
        using (var k = Registry.CurrentUser.CreateSubKey(approved)) k.SetValue(StartWithWindowsHandler.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        var handler = new StartWithWindowsHandler(run, approved, AppPath);

        var on = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.StartWithWindowsSet, enable: true)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, on.Status);
        Assert.Contains("Task Manager", on.Message); // the user's own "off" in Task Manager is respected, not overwritten
        using (var k = Registry.CurrentUser.OpenSubKey(run)) Assert.Equal($"\"{AppPath}\" --tray", k!.GetValue(StartWithWindowsHandler.ValueName));
        using (var k = Registry.CurrentUser.OpenSubKey(approved)) Assert.Equal(3, ((byte[])k!.GetValue(StartWithWindowsHandler.ValueName)!)[0]);

        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(on)), sandbox.Context(), default)).Status);
        using (var k = Registry.CurrentUser.OpenSubKey(run))
        {
            Assert.Null(k!.GetValue(StartWithWindowsHandler.ValueName));
            Assert.Equal("x", k.GetValue("Other"));
        }

        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.StartWithWindowsSet, enable: false)), sandbox.Context(), default)).Status);
    }

    // ---------------------------------------------------------------- launch app / quick access

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Tools\powershell.exe")]
    [InlineData(@"C:\Tools\mshta.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"C:\Program Files\Contoso\app.bat")]
    public async Task Launch_app_refuses_system_tools_shells_and_odd_paths(string exe)
    {
        using var sandbox = new TestSandbox();
        var launcher = new FakeLauncher();
        launcher.Files.Add(exe);
        var result = await new LaunchAppHandler(launcher).ExecuteAsync(Steps.Of(Item(ActionKind.LaunchApp, new() { ["exePath"] = exe })), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Launch_app_starts_the_program_with_its_arguments_and_never_from_the_helper()
    {
        using var sandbox = new TestSandbox();
        var launcher = new FakeLauncher();
        const string exe = @"C:\Program Files\Contoso\Chat.exe";
        launcher.Files.Add(exe);
        var handler = new LaunchAppHandler(launcher);
        var item = Item(ActionKind.LaunchApp, new() { ["exePath"] = exe, ["arguments"] = "--minimized" }, reversible: false);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default)).Status);
        Assert.Equal((exe, "--minimized"), launcher.Launched.Single());
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task Quick_access_pin_undo_unpins_only_what_pc_tidy_pinned()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeQuickAccess();
        var handler = new QuickAccessPinHandler(fake);
        var item = Item(ActionKind.QuickAccessPin, source: sandbox.Root);
        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains(sandbox.Root, fake.Pinned);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Empty(fake.Pinned);

        fake.Pinned.Add(sandbox.Root);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default)).Status); // already pinned
        Assert.Equal(StepStatus.Skipped, (await handler.UndoAsync(Steps.Of(item, status: StepStatus.Done), sandbox.Context(), default)).Status);
        Assert.Contains(sandbox.Root, fake.Pinned); // the user's own pin stays

        fake.Pinned.Clear();
        fake.Blocked = true;
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default)).Status);
    }

    // ---------------------------------------------------------------- service and task rules

    private static PlanItem Service(string name, ServiceStart start, string feature = "tweaks.badtweaks") => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = "t", ServiceName = name, ServiceStartType = start, NeedsAdmin = true, IsSystemChange = true,
    };

    [Theory]
    [InlineData("wuauserv", ServiceStart.Manual, "tweaks.badtweaks", true)]
    [InlineData("WinDefend", ServiceStart.Automatic, "tweaks.badtweaks", true)]
    [InlineData("wscsvc", ServiceStart.AutomaticDelayed, "tweaks.badtweaks", true)]
    [InlineData("WinDefend", ServiceStart.Manual, "tweaks.badtweaks", false)] // not its default
    [InlineData("wuauserv", ServiceStart.Disabled, "tweaks.badtweaks", false)]
    [InlineData("wuauserv", ServiceStart.Manual, "tweaks", false)]
    [InlineData("XblAuthManager", ServiceStart.Manual, "tweaks.badtweaks", true)]
    [InlineData("XboxSomethingNew", ServiceStart.Manual, "tweaks.badtweaks", false)] // unknown protected: only Automatic
    public void Protected_services_may_only_be_restored_to_their_windows_default_by_the_bad_tweak_finder(string name, ServiceStart start, string feature, bool allowed)
        => Assert.Equal(allowed, ServiceStartTypeHandler.Validate(Service(name, start, feature)) is null);

    [Fact]
    public async Task Bad_tweak_repair_switches_a_disabled_protected_service_back_on()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl();
        fake.Services["wuauserv"] = new ServiceStartConfig(4, false, 0x20);
        var done = await new ServiceStartTypeHandler(fake).ExecuteAsync(Steps.Of(Service("wuauserv", ServiceStart.Manual)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(3, fake.Services["wuauserv"].Start);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start", true, "tweaks.badtweaks", true)]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start", false, "tweaks.badtweaks", false)]
    [InlineData(@"\Microsoft\Office\OfficeTelemetryAgentLogOn", false, "tweaks", false)]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true, "tweaks", false)]
    [InlineData(@"\Contoso\Updater", false, "startup", true)]
    public void Windows_tasks_may_only_be_turned_back_on_by_the_bad_tweak_finder(string path, bool enable, string feature, bool allowed)
    {
        var item = new PlanItem { Kind = ActionKind.ScheduledTaskSetEnabled, FeatureId = feature, Description = "t", TaskPath = path, Enable = enable };
        Assert.Equal(allowed, ScheduledTaskHandler.Validate(item) is null);
    }

    // ---------------------------------------------------------------- OpenExternal

    [Theory]
    [InlineData("steam://uninstall/440", ExternalTargetKind.SteamScreen)]
    [InlineData("steam://open/settings", ExternalTargetKind.SteamScreen)]
    [InlineData("com.epicgames.launcher://store", ExternalTargetKind.LauncherScreen)]
    [InlineData("com.epicgames.launcher://store/library", ExternalTargetKind.LauncherScreen)]
    [InlineData("goggalaxy://openGameView/1207658924", ExternalTargetKind.LauncherScreen)]
    [InlineData("ms-windows-store://pdp/?PFN=Microsoft.WindowsCalculator_8wekyb3d8bbwe", ExternalTargetKind.StorePage)]
    [InlineData("ms-windows-store://downloadsandupdates", ExternalTargetKind.StorePage)]
    [InlineData("ms-settings:windowsdefender", ExternalTargetKind.SettingsPage)]
    public void New_launcher_and_store_targets_are_allowed(string target, ExternalTargetKind kind)
    {
        Assert.True(OpenExternalHandler.TryValidate(target, out var k, out var problem), problem);
        Assert.Equal(kind, k);
    }

    [Theory]
    [InlineData("steam://uninstall/440;rm")]
    [InlineData("steam://install/440")]
    [InlineData("com.epicgames.launcher://apps/x?action=launch")]
    [InlineData("com.epicgames.launcher://store//x")]
    [InlineData("goggalaxy://launch/1")]
    [InlineData("ms-windows-store://pdp/?x=\"a\"")]
    [InlineData(@"\\.\C:\")]
    [InlineData(@"\\?\C:\Windows")]
    public void Other_launcher_verbs_and_device_paths_are_refused(string target)
        => Assert.False(OpenExternalHandler.TryValidate(target, out _, out _));

    // ---------------------------------------------------------------- visual effects follow-up (real writes, test key only)

    [Fact]
    public async Task Visual_fx_best_performance_applies_spi_switches_and_undo_restores_the_exact_prior_states()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var fxKey = key.Sub("VisualEffects");
        var spi = new FakeVisualEffects();
        foreach (var name in VisualEffects.Switches.Keys) spi.States[name] = true;
        spi.States[VisualEffects.MinMaxAnimation] = true;
        spi.States["DropShadow"] = false; // the user's own odd choice must come back exactly
        var handler = new RegistryHandler(spi, fxKey);
        var item = Steps.RegSet(fxKey, VisualEffects.Value, RegistryKind.DWord, "2");

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.All(spi.Applied.Single(), kv => Assert.False(kv.Value));

        var undone = await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.True(spi.States["MenuAnimation"]);
        Assert.False(spi.States["DropShadow"]);
    }

    [Fact]
    public void Visual_fx_values_map_to_switch_states()
    {
        Assert.All(VisualEffects.TargetFor("2")!, kv => Assert.False(kv.Value));
        Assert.True(VisualEffects.TargetFor("0")![VisualEffects.UiEffects]);
        Assert.False(VisualEffects.TargetFor("2")!.ContainsKey(VisualEffects.UiEffects)); // the master switch is never turned off
        Assert.Null(VisualEffects.TargetFor("3"));
    }

    [Fact]
    public async Task Other_registry_values_never_touch_visual_effects()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var spi = new FakeVisualEffects();
        var handler = new RegistryHandler(spi, key.Sub("VisualEffects"));
        await handler.ExecuteAsync(Steps.Of(Steps.RegSet(key.Sub("Other"), VisualEffects.Value, RegistryKind.DWord, "2")), sandbox.Context(), default);
        Assert.Empty(spi.Applied);
    }

    // ---------------------------------------------------------------- path arguments

    [Theory]
    [InlineData(@"C:\Users\tester\Documents", true)]
    [InlineData(@"D:\Games", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"C:\x\GLOBALROOT\y", false)]
    [InlineData(@"C:\x:stream", false)]
    [InlineData(@"C:\x\..\Windows", false)]
    [InlineData(@"C:\x.\y", false)]
    [InlineData(@"C:/x", false)]
    [InlineData(@"relative\path", false)]
    public void Path_arguments_must_be_plain_local_paths(string path, bool ok) => Assert.Equal(ok, PathArgs.IsPlainLocal(path));
}
