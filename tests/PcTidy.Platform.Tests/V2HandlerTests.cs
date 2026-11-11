using PcTidy.Core;
using Microsoft.Win32;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Platform.Tests.V2Items;

namespace PcTidy.Platform.Tests;

/// <summary>v2 system handlers against in-memory fakes (and real writes only under HKCU\Software\PCTidyTest\secfix\platform).</summary>
public class V2HandlerTests
{
    // ---------------------------------------------------------------- real handlers never act in the sandbox

    public static IEnumerable<object[]> RealHandlerItems() => new[]
    {
        new object[] { new PowerPlanHandler(), Item(ActionKind.PowerPlanSet, new() { ["schemeGuid"] = Guid.NewGuid().ToString() }) },
        new object[] { new RestorePointDeleteHandler(), Item(ActionKind.RestorePointDelete, new() { ["sequenceNumber"] = "7" }, admin: true, reversible: false) },
        new object[] { new WlanProfileHandler(), Item(ActionKind.WlanProfileDelete, new() { ["profileName"] = "Cafe", ["interfaceGuid"] = Guid.NewGuid().ToString() }, admin: true) },
        new object[] { new UserProfileDeleteHandler(), Item(ActionKind.UserProfileDelete, new() { ["sid"] = "S-1-5-21-1-2-3-1009" }, admin: true, reversible: false) },
        new object[] { new OptionalFeatureHandler(), Item(ActionKind.OptionalFeatureRemove, new() { ["name"] = "WorkFolders-Client", ["kind"] = "feature" }, admin: true) },
        new object[] { new VolumeOptimizeHandler(), Item(ActionKind.VolumeOptimize, new() { ["drive"] = "C", ["expect"] = "retrim" }, admin: true, reversible: false) },
        new object[] { new CloseAppHandler(), Item(ActionKind.CloseApp, new() { ["pid"] = "4242", ["processName"] = "notepad" }, reversible: false) },
        new object[] { new StoreRepairHandler(), Item(ActionKind.StoreRepair, new() { ["wsreset"] = "true" }, reversible: false) },
        new object[] { new AppxRemoveForUserHandler(), Item(ActionKind.AppxRemoveForUser, new() { ["packageFullName"] = "Contoso.Game_1.2.3.0_x64__abcdefghjkmnp" }, reversible: false) },
        new object[] { new KnownFolderRedirectHandler(), Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = "{374DE290-123F-4565-9164-39C4925E467B}" }, destination: @"D:\Users\tester\Downloads") },
        new object[] { new UninstallerHandler(), Item(ActionKind.RunUninstaller, new() { ["wingetId"] = "Contoso.App", ["displayName"] = "Contoso" }, reversible: false) },
        new object[] { new ShellExtensionHandler(), Item(ActionKind.ShellExtensionSetEnabled, new() { ["clsid"] = Guid.NewGuid().ToString("B") }, enable: false) },
        new object[] { new ShellVerbHandler(), Item(ActionKind.ShellVerbSetInstalled, new() { ["verb"] = "Organize", ["command"] = "\"C:\\Program Files\\PC Tidy\\PcTidy.exe\" --organize \"%V\"" }, enable: true) },
        new object[] { new LaunchAppHandler(), Item(ActionKind.LaunchApp, new() { ["exePath"] = @"C:\Program Files\Contoso\app.exe" }) },
        new object[] { new QuickAccessPinHandler(), Item(ActionKind.QuickAccessPin, source: @"C:\Users\tester\Projects") },
        new object[] { new StartWithWindowsHandler(), Item(ActionKind.StartWithWindowsSet, enable: true) },
        new object[] { new ScheduledTaskDeleteHandler(), Item(ActionKind.ScheduledTaskDelete, new() { ["name"] = "Weekly report" }) },
        new object[] { new DeviceSetEnabledHandler(), Item(ActionKind.DeviceSetEnabled, new() { ["instanceId"] = @"USB\VID_1234&PID_ABCD\5&2B3C4D5E&0&1" }, enable: true, admin: true) },
    };

    [Theory]
    [MemberData(nameof(RealHandlerItems))]
    public async Task Real_handlers_only_simulate_in_the_sandbox(IActionHandler handler, PlanItem item)
    {
        using var sandbox = new TestSandbox();
        var ctx = sandbox.Context(elevated: item.NeedsAdmin);
        var result = await handler.ExecuteAsync(Steps.Of(item), ctx, default);
        Assert.True(result.Status is StepStatus.Simulated or StepStatus.Skipped, result.Message);
        if (result.Status == StepStatus.Simulated) Assert.Contains("Would", result.Message);
        Assert.False(await handler.WasCompletedAsync(Steps.Of(item), ctx, default));
        var simulated = await handler.SimulateAsync(Steps.Of(item), ctx, default);
        Assert.Equal(StepStatus.Simulated, simulated.Status);
    }

    [Fact]
    public void Every_platform_kind_has_exactly_one_handler()
    {
        var handlers = PlatformHandlers.CreateAll();
        var kinds = new[]
        {
            ActionKind.RegistrySetValue, ActionKind.RegistryDeleteValue, ActionKind.RegistryDeleteKey, ActionKind.ServiceSetStartType,
            ActionKind.ScheduledTaskSetEnabled, ActionKind.StartupItemSetEnabled, ActionKind.EmptyRecycleBin, ActionKind.RunSystemTool, ActionKind.OpenExternal,
            ActionKind.KnownFolderRedirect, ActionKind.AppxRemoveForUser, ActionKind.RunUninstaller, ActionKind.PowerPlanSet, ActionKind.ShellVerbSetInstalled,
            ActionKind.ShellExtensionSetEnabled, ActionKind.RestorePointDelete, ActionKind.WlanProfileDelete, ActionKind.UserProfileDelete,
            ActionKind.OptionalFeatureRemove, ActionKind.VolumeOptimize, ActionKind.CloseApp, ActionKind.StoreRepair,
            ActionKind.ScheduledTaskCreate, ActionKind.ScheduledTaskDelete, ActionKind.StartWithWindowsSet, ActionKind.LaunchApp, ActionKind.QuickAccessPin,
            ActionKind.DeviceSetEnabled,
        };
        foreach (var kind in kinds) Assert.Single(handlers, h => h.CanHandle(new PlanItem { Kind = kind, FeatureId = "t", Description = "t" }));
    }

    [Fact]
    public void Admin_helper_kinds_include_the_machine_wide_v2_kinds_but_never_per_user_ones()
    {
        foreach (var k in new[] { ActionKind.VolumeOptimize, ActionKind.ShellExtensionSetEnabled, ActionKind.RestorePointDelete, ActionKind.UserProfileDelete,
                     ActionKind.OptionalFeatureRemove, ActionKind.WlanProfileDelete, ActionKind.RunUninstaller, ActionKind.DeviceSetEnabled })
            Assert.Contains(k, ElevatedBatchExecutor.AdminCapableKinds);
        foreach (var k in new[] { ActionKind.LaunchApp, ActionKind.CloseApp, ActionKind.AppxRemoveForUser, ActionKind.KnownFolderRedirect, ActionKind.StoreRepair,
                     ActionKind.ScheduledTaskCreate, ActionKind.ScheduledTaskDelete, ActionKind.StartWithWindowsSet, ActionKind.QuickAccessPin, ActionKind.ShellVerbSetInstalled })
            Assert.DoesNotContain(k, ElevatedBatchExecutor.AdminCapableKinds);
    }

    // ---------------------------------------------------------------- power plan

    [Fact]
    public async Task Power_plan_records_the_previous_plan_and_undo_switches_back()
    {
        using var sandbox = new TestSandbox();
        var balanced = Guid.NewGuid();
        var high = Guid.NewGuid();
        var fake = new FakePowerSchemes { Active = balanced };
        fake.Schemes.UnionWith(new[] { balanced, high });
        var handler = new PowerPlanHandler(fake);

        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.PowerPlanSet, new() { ["schemeGuid"] = high.ToString() })), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(high, fake.Active);
        Assert.Equal(balanced.ToString("D"), done.UndoData![PowerPlanHandler.PriorSchemeData]);
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(), default));

        var undone = await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(balanced, fake.Active);
    }

    [Fact]
    public async Task Power_plan_refuses_missing_and_bad_plans()
    {
        using var sandbox = new TestSandbox();
        var handler = new PowerPlanHandler(new FakePowerSchemes());
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.PowerPlanSet, new() { ["schemeGuid"] = "x" })), sandbox.Context(), default)).Status);
        var missing = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.PowerPlanSet, new() { ["schemeGuid"] = Guid.NewGuid().ToString() })), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, missing.Status);
        Assert.Contains("isn't on this PC", missing.Message);
    }

    // ---------------------------------------------------------------- restore points

    [Fact]
    public async Task Restore_point_delete_keeps_the_newest_and_is_not_reversible()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeRestorePoints();
        fake.Points.Add(new RestorePointEntry(5, "old", DateTime.UtcNow.AddDays(-20), 12));
        fake.Points.Add(new RestorePointEntry(9, "new", DateTime.UtcNow.AddDays(-1), 12));
        var handler = new RestorePointDeleteHandler(fake);

        var newest = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.RestorePointDelete, new() { ["sequenceNumber"] = "9" }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, newest.Status);
        Assert.Empty(fake.Removed);

        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.RestorePointDelete, new() { ["sequenceNumber"] = "5" }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new uint[] { 5 }, fake.Removed);
        Assert.Equal(StepStatus.Skipped, (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Status);

        var reversible = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.RestorePointDelete, new() { ["sequenceNumber"] = "5" }, admin: true, reversible: true)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, reversible.Status);
    }

    // ---------------------------------------------------------------- Wi-Fi

    private const string WifiXml = "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>Cafe</name><MSM><security><sharedKey><protected>true</protected><keyMaterial>01000000D08C9DDF</keyMaterial></sharedKey></security></MSM></WLANProfile>";

    [Fact]
    public async Task Wifi_profile_is_exported_first_and_undo_imports_the_backup()
    {
        using var sandbox = new TestSandbox();
        var adapter = Guid.NewGuid();
        var fake = new FakeWlan();
        fake.Profiles[(adapter, "Cafe")] = new WlanProfileExport(WifiXml, 0);
        var handler = new WlanProfileHandler(fake);
        var item = Item(ActionKind.WlanProfileDelete, new() { ["profileName"] = "Cafe", ["interfaceGuid"] = adapter.ToString() }, admin: true);

        var done = await handler.ExecuteAsync(Steps.Of(item, index: 3), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Empty(fake.Profiles);
        var backup = done.UndoData![WlanProfileHandler.BackupData];
        Assert.StartsWith(Path.Combine(sandbox.Locations.DataRoot, "backups", "wlan"), backup);
        Assert.Equal(WifiXml, File.ReadAllText(backup));

        // The journal lost the undo data (cut off mid-way): the backup is found by its fixed name.
        var undone = await handler.UndoAsync(done with { UndoData = null }, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(WifiXml, fake.Profiles[(adapter, "Cafe")].Xml);
    }

    [Fact]
    public async Task Wifi_group_policy_profiles_and_outside_backups_are_refused()
    {
        using var sandbox = new TestSandbox();
        var adapter = Guid.NewGuid();
        var fake = new FakeWlan();
        fake.Profiles[(adapter, "Work")] = new WlanProfileExport(WifiXml, WlanProfileExport.GroupPolicy);
        var handler = new WlanProfileHandler(fake);
        var item = Item(ActionKind.WlanProfileDelete, new() { ["profileName"] = "Work", ["interfaceGuid"] = adapter.ToString() }, admin: true);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status);

        var outside = Path.Combine(sandbox.Root, "evil.xml");
        File.WriteAllText(outside, WifiXml);
        var forged = Steps.Of(item, status: StepStatus.Done) with { UndoData = new() { [WlanProfileHandler.BackupData] = outside } };
        fake.Profiles.Clear();
        Assert.Equal(StepStatus.UndoFailed, (await handler.UndoAsync(forged, sandbox.Context(elevated: true), default)).Status);
        Assert.Empty(fake.Profiles);
    }

    // ---------------------------------------------------------------- user profiles

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", false, false, "your own")]
    [InlineData("S-1-5-21-1-2-3-1002", true, false, "in use")]
    [InlineData("S-1-5-21-1-2-3-1003", false, true, "Windows uses")]
    public async Task User_profile_delete_refuses_own_loaded_and_special(string sid, bool loaded, bool special, string why)
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeProfiles();
        fake.Profiles[sid] = new UserProfileFacts(sid, @"C:\Users\old", loaded, special);
        var result = await new UserProfileDeleteHandler(fake).ExecuteAsync(Steps.Of(Item(ActionKind.UserProfileDelete, new() { ["sid"] = sid }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Contains(why, result.Message);
        Assert.Empty(fake.Deleted);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3-1004' OR '1'='1")]
    [InlineData("")]
    public async Task User_profile_delete_refuses_bad_sids(string sid)
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeProfiles();
        var result = await new UserProfileDeleteHandler(fake).ExecuteAsync(Steps.Of(Item(ActionKind.UserProfileDelete, new() { ["sid"] = sid }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task User_profile_delete_removes_a_leftover_and_cannot_be_undone()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeProfiles();
        fake.Profiles["S-1-5-21-1-2-3-1005"] = new UserProfileFacts("S-1-5-21-1-2-3-1005", @"C:\Users\old", false, false);
        var handler = new UserProfileDeleteHandler(fake);
        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.UserProfileDelete, new() { ["sid"] = "S-1-5-21-1-2-3-1005" }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("can't be undone", (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Message);
    }

    // ---------------------------------------------------------------- optional features

    [Fact]
    public async Task Feature_is_turned_off_keeping_files_and_undo_turns_it_on()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeFeatures { RemoveResult = OptionalFeatureHandler.RestartRequired };
        fake.States["WorkFolders-Client"] = OptionalFeatureState.Enabled;
        var handler = new OptionalFeatureHandler(fake);
        var item = Item(ActionKind.OptionalFeatureRemove, new() { ["name"] = "WorkFolders-Client", ["kind"] = "feature" }, admin: true);

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("Restart", done.Message);
        Assert.Equal("feature:WorkFolders-Client", done.UndoData![OptionalFeatureHandler.ReAddData]);
        var undone = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(new[] { "disable:WorkFolders-Client", "enable:WorkFolders-Client" }, fake.Calls);
    }

    [Fact]
    public async Task Capability_removal_is_honestly_not_undone_and_protected_features_are_refused()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeFeatures();
        fake.States["Browser.InternetExplorer~~~~0.0.11.0"] = OptionalFeatureState.Enabled;
        var handler = new OptionalFeatureHandler(fake);
        var cap = Item(ActionKind.OptionalFeatureRemove, new() { ["name"] = "Browser.InternetExplorer~~~~0.0.11.0", ["kind"] = "capability" }, admin: true, reversible: false);
        var done = await handler.ExecuteAsync(Steps.Of(cap), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var undo = await handler.UndoAsync(done, sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Contains("Optional features", undo.Message);

        var defender = Item(ActionKind.OptionalFeatureRemove, new() { ["name"] = "Windows-Defender-Default-Definitions", ["kind"] = "feature" }, admin: true);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(defender), sandbox.Context(elevated: true), default)).Status);
    }

    // ---------------------------------------------------------------- volumes

    [Theory]
    [InlineData(DriveMedia.Ssd, "retrim", true, false, true)]
    [InlineData(DriveMedia.Ssd, "", true, false, true)]
    [InlineData(DriveMedia.Hdd, "defrag", false, true, true)]
    [InlineData(DriveMedia.Ssd, "defrag", false, false, false)]
    [InlineData(DriveMedia.Hdd, "retrim", false, false, false)]
    [InlineData(DriveMedia.Unknown, "defrag", false, false, false)]
    public void Volume_optimize_never_defrags_an_ssd(DriveMedia media, string expect, bool retrim, bool defrag, bool allowed)
    {
        var (r, d, problem) = VolumeOptimizeHandler.Decide(media, expect);
        Assert.Equal((retrim, defrag, allowed), (r, d, problem is null));
    }

    [Fact]
    public async Task Volume_optimize_runs_the_decided_operation_only()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeVolumes();
        fake.Drives['C'] = DriveMedia.Ssd;
        var handler = new VolumeOptimizeHandler(fake);
        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.VolumeOptimize, new() { ["drive"] = "C:\\", ["expect"] = "retrim" }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(('C', true, false), fake.Calls.Single());
        var defrag = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.VolumeOptimize, new() { ["drive"] = "C", ["expect"] = "defrag" }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, defrag.Status);
        Assert.Single(fake.Calls);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.VolumeOptimize, new() { ["drive"] = "Z" }, admin: true)), sandbox.Context(elevated: true), default)).Status);
    }

    // ---------------------------------------------------------------- close app

    [Fact]
    public async Task Close_app_posts_wm_close_and_never_touches_a_reused_pid_or_protected_app()
    {
        using var sandbox = new TestSandbox();
        var started = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var fake = new FakeApps();
        fake.Processes[4242] = new ProcessFacts(4242, "notepad", started, 1);
        var handler = new CloseAppHandler(fake);

        var reused = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.CloseApp, new() { ["pid"] = "4242", ["processName"] = "notepad", ["startedUtc"] = started.AddMinutes(5).ToString("O") }, reversible: false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, reused.Status);
        Assert.Empty(fake.Posted);

        var other = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.CloseApp, new() { ["pid"] = "4242", ["processName"] = "chrome" }, reversible: false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, other.Status);

        foreach (var name in new[] { "explorer", "MsMpEng", "EasyAntiCheat_EOS", "PcTidy" })
            Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.CloseApp, new() { ["pid"] = "4242", ["processName"] = name }, reversible: false)), sandbox.Context(), default)).Status);
        Assert.Empty(fake.Posted);

        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.CloseApp, new() { ["pid"] = "4242", ["processName"] = "notepad.exe", ["startedUtc"] = started.ToString("O") }, reversible: false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { 4242 }, fake.Posted);
    }

    [Fact]
    public async Task Close_app_that_stays_open_is_left_open_and_the_helper_never_closes_apps()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeApps { ClosesOnRequest = false };
        fake.Processes[4243] = new ProcessFacts(4243, "editor", DateTime.UtcNow, 1);
        var handler = new CloseAppHandler(fake);
        var item = Item(ActionKind.CloseApp, new() { ["pid"] = "4243", ["processName"] = "editor" }, reversible: false);
        var stays = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, stays.Status);
        Assert.Contains("never force-closes", stays.Message);
        Assert.True(fake.Processes.ContainsKey(4243));
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status);
    }

    // ---------------------------------------------------------------- Store apps

    [Fact]
    public async Task Appx_removal_re_registers_on_undo_when_still_staged_else_says_store()
    {
        using var sandbox = new TestSandbox();
        const string full = "Contoso.Game_1.2.3.0_x64__abcdefghjkmnp";
        var fake = new FakeAppx();
        fake.Installed[full] = new AppxPackageFacts(full, "Contoso.Game_abcdefghjkmnp", @"C:\Program Files\WindowsApps\Contoso", false, false);
        fake.StagedFolders.Add(@"C:\Program Files\WindowsApps\Contoso");
        var handler = new AppxRemoveForUserHandler(fake);
        var item = Item(ActionKind.AppxRemoveForUser, new() { ["packageFullName"] = full, ["displayName"] = "Contoso Game" }, reversible: false);

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Contains("Undo can add it back", done.Message);
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.Equal(new[] { "remove:" + full, "register:" + full }, fake.Calls);

        fake.StagedFolders.Clear();
        var again = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Contains("Microsoft Store", again.Message);
        var noUndo = await handler.UndoAsync(again, sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, noUndo.Status);
        Assert.Contains("Microsoft Store", noUndo.Message);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore_22401.1400.6.0_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe")]
    [InlineData("not a package")]
    public async Task Appx_removal_refuses_critical_and_malformed_packages(string full)
    {
        using var sandbox = new TestSandbox();
        var result = await new AppxRemoveForUserHandler(new FakeAppx()).ExecuteAsync(Steps.Of(Item(ActionKind.AppxRemoveForUser, new() { ["packageFullName"] = full }, reversible: false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task Store_repair_runs_wsreset_or_re_registers_the_family()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAppx();
        fake.Installed["Contoso.Notes_2.0.0.0_x64__abcdefghjkmnp"] = new AppxPackageFacts("Contoso.Notes_2.0.0.0_x64__abcdefghjkmnp", "Contoso.Notes_abcdefghjkmnp", @"C:\WA\Notes", false, false);
        fake.StagedFolders.Add(@"C:\WA\Notes");
        var handler = new StoreRepairHandler(fake);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.StoreRepair, new() { ["wsreset"] = "true" }, reversible: false)), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.StoreRepair, new() { ["packageFamilyName"] = "Contoso.Notes_abcdefghjkmnp" }, reversible: false)), sandbox.Context(), default)).Status);
        Assert.Equal(new[] { "wsreset", "register:Contoso.Notes_2.0.0.0_x64__abcdefghjkmnp" }, fake.Calls);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.StoreRepair, new() { ["packageFamilyName"] = "bad family" })), sandbox.Context(), default)).Status);
    }

    // ---------------------------------------------------------------- known folders

    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [Fact]
    public async Task Known_folder_redirect_checks_first_copies_verifies_and_undo_points_back()
    {
        using var sandbox = new TestSandbox();
        var old = Path.Combine(sandbox.Root, "Profile", "Downloads");
        Directory.CreateDirectory(Path.Combine(old, "sub"));
        File.WriteAllText(Path.Combine(old, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(old, "sub", "b.txt"), "world");
        var target = Path.Combine(sandbox.Root, "D", "Downloads");
        var fake = new FakeKnownFolders();
        fake.Paths[Downloads] = old;
        var handler = new KnownFolderRedirectHandler(fake);
        var item = Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = Downloads.ToString("B") }, destination: target);

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { true, false }, fake.Calls.Select(c => c.CheckOnly));
        Assert.True(File.Exists(Path.Combine(old, "a.txt"))); // old copy kept (a separate quarantine step removes it)
        Assert.Equal(old, done.UndoData![KnownFolderRedirectHandler.PriorPathData]);
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(), default));

        var undone = await handler.UndoAsync(done, sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(old, fake.Paths[Downloads]);
    }

    [Fact]
    public async Task Known_folder_redirect_points_back_when_the_copy_is_incomplete_and_refuses_bad_targets()
    {
        using var sandbox = new TestSandbox();
        var old = Path.Combine(sandbox.Root, "Profile", "Downloads");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.txt"), "hello");
        var fake = new FakeKnownFolders { CopyContents = false };
        fake.Paths[Downloads] = old;
        var handler = new KnownFolderRedirectHandler(fake);

        var incomplete = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = Downloads.ToString() }, destination: Path.Combine(sandbox.Root, "D", "Dl"))), sandbox.Context(), default);
        Assert.Equal(StepStatus.Failed, incomplete.Status);
        Assert.Equal(old, fake.Paths[Downloads]);

        foreach (var bad in new[] { @"\\server\share\Downloads", @"\\?\C:\x", @"\\.\GLOBALROOT\Device\x", "D:\\", Path.Combine(old, "inside") })
        {
            var r = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = Downloads.ToString() }, destination: bad)), sandbox.Context(), default);
            Assert.Equal(StepStatus.Skipped, r.Status);
        }
        var notLibrary = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = Guid.NewGuid().ToString() }, destination: Path.Combine(sandbox.Root, "x"))), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, notLibrary.Status);
    }

    /// <summary>KFM-1: hitting the entry limit is "couldn't fully check", never "complete".</summary>
    [Fact]
    public void The_copy_check_never_reports_complete_after_hitting_its_entry_limit()
    {
        using var sandbox = new TestSandbox();
        var source = Path.Combine(sandbox.Root, "old");
        var target = Path.Combine(sandbox.Root, "new");
        foreach (var dir in new[] { source, target })
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        }
        Assert.True(KnownFolderRedirectHandler.CheckCopy(source, target, default).Complete);
        var limited = KnownFolderRedirectHandler.CheckCopy(source, target, default, maxEntries: 2);
        Assert.False(limited.Complete);
        Assert.Null(limited.Missing);
        Assert.Contains("more than 2 items", limited.Unchecked);
    }

    /// <summary>
    /// KFM-1: a subfolder the check can't list (here: a deny rule for the current user, inside the testbed only) makes
    /// the move fail and point the folder back, so the old-copy quarantine steps (which need this step Done) never run.
    /// </summary>
    [Fact]
    public async Task Known_folder_redirect_points_back_when_a_subfolder_cant_be_checked()
    {
        using var sandbox = new TestSandbox();
        var old = Path.Combine(sandbox.Root, "Profile", "Downloads");
        var locked = Path.Combine(old, "Locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(old, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(locked, "only-copy.txt"), "precious");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            // The fake copies what it can see, like a copy that skips what it can't read.
            var fake = new FakeKnownFolders();
            fake.Paths[Downloads] = old;
            var handler = new KnownFolderRedirectHandler(fake);
            var target = Path.Combine(sandbox.Root, "D", "Downloads");
            var result = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.KnownFolderRedirect, new() { ["folderId"] = Downloads.ToString() }, destination: target)), sandbox.Context(), default);

            Assert.Equal(StepStatus.Failed, result.Status);
            Assert.Contains("couldn't fully check", result.Message);
            Assert.Contains("Locked", result.Message);
            Assert.Equal(old, fake.Paths[Downloads]);
        }
        finally
        {
            acl = info.GetAccessControl();
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
        Assert.True(File.Exists(Path.Combine(locked, "only-copy.txt")));
    }

    // ---------------------------------------------------------------- uninstaller

    private const string Code = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public async Task Uninstaller_prefers_msiexec_x_for_a_listed_product_code()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeUninstall();
        fake.Listed.Add(new UninstallEntry("HKLM", Code, "Contoso", "MsiExec.exe /I" + Code, null));
        fake.Files.Add(fake.WingetPath);
        var handler = new UninstallerHandler(fake);
        var item = Item(ActionKind.RunUninstaller, new() { ["msiProductCode"] = Code, ["wingetId"] = "Contoso.App", ["uninstallString"] = "MsiExec.exe /I" + Code, ["displayName"] = "Contoso" }, admin: true, reversible: false);

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var cmd = fake.Ran.Single();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), cmd.ExePath);
        Assert.Equal(new[] { "/x", Code, "/norestart" }, cmd.Arguments);
        Assert.Contains("can't be undone", (await handler.UndoAsync(done, sandbox.Context(elevated: true), default)).Message);
    }

    [Fact]
    public async Task Uninstaller_falls_back_from_winget_not_found_to_the_vendor_command_without_a_shell()
    {
        using var sandbox = new TestSandbox();
        const string vendor = @"C:\Program Files\Contoso App\unins000.exe /SILENT";
        var fake = new FakeUninstall { ExitCode = c => c.Via == UninstallVia.Winget ? unchecked((int)0x8A150014) : 0 };
        fake.Listed.Add(new UninstallEntry("HKCU", "Contoso_is1", "Contoso", vendor, null));
        fake.Files.UnionWith(new[] { fake.WingetPath, @"C:\Program Files\Contoso App\unins000.exe" });
        var handler = new UninstallerHandler(fake);
        var item = Item(ActionKind.RunUninstaller, new() { ["wingetId"] = "Contoso.App", ["uninstallString"] = vendor, ["displayName"] = "Contoso" }, reversible: false);

        var done = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(new[] { UninstallVia.Winget, UninstallVia.Vendor }, fake.Ran.Select(r => r.Via));
        Assert.Equal(new[] { "uninstall", "--id", "Contoso.App", "--exact", "--silent", "--disable-interactivity" }, fake.Ran[0].Arguments);
        Assert.Equal(@"C:\Program Files\Contoso App\unins000.exe", fake.Ran[1].ExePath);
        Assert.Equal("/SILENT", fake.Ran[1].RawArguments);
    }

    [Theory]
    [InlineData("cmd.exe /c del C:\\")]
    [InlineData("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -c x")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL")]
    public async Task Uninstaller_refuses_shells_and_script_hosts(string vendor)
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeUninstall();
        fake.Listed.Add(new UninstallEntry("HKLM", "X", "X", vendor, null));
        fake.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        var result = await new UninstallerHandler(fake).ExecuteAsync(Steps.Of(Item(ActionKind.RunUninstaller, new() { ["uninstallString"] = vendor }, admin: true, reversible: false)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Uninstaller_only_runs_commands_windows_lists_and_in_the_helper_only_hklm_ones()
    {
        using var sandbox = new TestSandbox();
        const string vendor = @"""C:\Program Files\Contoso\uninstall.exe"" /S";
        var fake = new FakeUninstall();
        fake.Files.Add(@"C:\Program Files\Contoso\uninstall.exe");
        var handler = new UninstallerHandler(fake);
        var item = Item(ActionKind.RunUninstaller, new() { ["uninstallString"] = vendor }, admin: true, reversible: false);

        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status); // not listed at all
        fake.Listed.Add(new UninstallEntry("HKCU", "Contoso", "Contoso", vendor, null));
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status); // per-user entry in the helper
        Assert.Empty(fake.Ran);
    }

    [Fact]
    public async Task Uninstaller_that_leaves_the_app_listed_fails_and_cancel_is_skipped()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeUninstall { RemoveEntryOnRun = false };
        fake.Listed.Add(new UninstallEntry("HKLM", Code, "Contoso", null, null));
        var handler = new UninstallerHandler(fake);
        var item = Item(ActionKind.RunUninstaller, new() { ["msiProductCode"] = Code }, admin: true, reversible: false);
        Assert.Equal(StepStatus.Failed, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status);
        fake.ExitCode = _ => 1602;
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default)).Status);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\uninst.exe /S", @"C:\Program Files\App\uninst.exe", "/S")]
    [InlineData(@"""C:\Program Files\App\u.exe"" --remove ""x y""", @"C:\Program Files\App\u.exe", @"--remove ""x y""")]
    [InlineData(@"C:\Program Files\App\uninst.exe", @"C:\Program Files\App\uninst.exe", "")]
    public void Vendor_uninstall_strings_are_split_into_program_and_arguments(string line, string exe, string args)
    {
        Assert.True(UninstallerHandler.ParseVendor(line, p => p == exe, out var command, out var problem), problem);
        Assert.Equal(exe, command!.ExePath);
        Assert.Equal(args, command.RawArguments);
    }

    [Fact]
    public void Msiexec_install_strings_become_x_never_i()
    {
        Assert.True(UninstallerHandler.ParseVendor("MsiExec.exe /I" + Code, _ => false, out var command, out _));
        Assert.Equal(new[] { "/x", Code, "/norestart" }, command!.Arguments);
    }

    // ---------------------------------------------------------------- shell extensions and verbs (real writes, test key only)

    [Fact]
    public async Task Shell_extension_block_records_prior_and_undo_restores_exactly()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var blocked = key.Sub("Blocked");
        var handler = new ShellExtensionHandler(blocked, key.Sub(@"Classes"));
        var clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var hide = Item(ActionKind.ShellExtensionSetEnabled, new() { ["clsid"] = clsid, ["hive"] = "HKCU", ["name"] = "Contoso menu" }, enable: false);

        var done = await handler.ExecuteAsync(Steps.Of(hide), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        using (var k = Registry.CurrentUser.OpenSubKey(blocked)) Assert.Equal("Contoso menu", k!.GetValue(clsid));
        Assert.False(done.RegistryPrior!.ValueExisted);

        var undone = await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.False(TestRegistryKey.Exists(blocked)); // the key PC Tidy created is gone again
    }

    [Fact]
    public async Task Shell_verb_legacy_disable_and_bad_verb_keys()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var classes = key.Sub("Classes");
        var verbKey = $@"{classes}\Directory\shell\git_shell";
        using (var k = Registry.CurrentUser.CreateSubKey(verbKey)) k.SetValue("", "Git Bash");
        var handler = new ShellExtensionHandler(key.Sub("Blocked"), classes);

        var done = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ShellExtensionSetEnabled, new() { ["verbKey"] = verbKey, ["hive"] = "HKCU" }, enable: false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        using (var k = Registry.CurrentUser.OpenSubKey(verbKey)) Assert.Equal("", k!.GetValue(ShellExtensionHandler.LegacyDisable));
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        using (var k = Registry.CurrentUser.OpenSubKey(verbKey)) Assert.Null(k!.GetValue(ShellExtensionHandler.LegacyDisable));

        foreach (var bad in new[] { $@"{classes}\Directory\notshell\x", $@"Software\Other\shell\x", $@"{classes}\..\shell\x" })
            Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ShellExtensionSetEnabled, new() { ["verbKey"] = bad }, enable: false)), sandbox.Context(), default)).Status);

        // HKLM verbs are never written from a test: the sandbox simulates them.
        var hklm = await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ShellExtensionSetEnabled, new() { ["verbKey"] = verbKey, ["hive"] = "HKLM" }, enable: false, admin: true)), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Simulated, hklm.Status);
    }

    [Fact]
    public async Task Pc_tidy_shell_verb_install_and_undo_remove_only_its_own_key()
    {
        using var sandbox = new TestSandbox();
        using var key = new TestRegistryKey();
        var exe = Environment.ProcessPath!;
        var handler = new ShellVerbHandler(key.Sub("Classes"));
        var install = Item(ActionKind.ShellVerbSetInstalled, new() { ["verb"] = "Organize", ["label"] = "Tidy with PC Tidy", ["command"] = $"\"{exe}\" --organize \"%V\"" }, enable: true);

        var done = await handler.ExecuteAsync(Steps.Of(install), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        var verbKey = key.Sub(@"Classes\Directory\shell\PCTidy.Organize");
        using (var k = Registry.CurrentUser.OpenSubKey(verbKey + @"\command")) Assert.Equal($"\"{exe}\" --organize \"%V\"", k!.GetValue(""));
        Assert.Equal(StepStatus.Undone, (await handler.UndoAsync(done, sandbox.Context(), default)).Status);
        Assert.False(TestRegistryKey.Exists(verbKey));

        foreach (var bad in new[] { "cmd.exe /c calc", "\"C:\\Windows\\System32\\cmd.exe\" /c x", "\"\\\\server\\x.exe\"" })
            Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ShellVerbSetInstalled, new() { ["verb"] = "X", ["command"] = bad }, enable: true)), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item(ActionKind.ShellVerbSetInstalled, new() { ["verb"] = "..\\x", ["command"] = $"\"{exe}\"" }, enable: true)), sandbox.Context(), default)).Status);
    }
}
