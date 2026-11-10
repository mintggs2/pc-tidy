using Microsoft.Win32;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;
using PcTidy.Features.Speed.Startup;
using PcTidy.UI.Flow;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Speed.Tests;

public class StartupTests
{
    private const string RunUser = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceUser = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ApprovedMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static byte[] Off() { var b = new byte[12]; b[0] = 3; BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4); return b; }

    [Fact]
    public async Task Run_keys_are_read_with_their_on_off_state_and_toggle_args()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var chat = sb.MakeFile(@"C\Users\tester\AppData\Local\Chat\Chat.exe");
        var tool = sb.MakeFile(@"C\Program Files (x86)\Tool Co\tool.exe");
        reg.Set(Hive.CurrentUser, RunUser, "Chat", $"\"{chat}\" --startup");
        reg.Set(Hive.CurrentUser, RunUser, "Gone", @"C:\Nowhere\gone.exe -q");
        reg.Set(Hive.LocalMachine, Run32, "ToolCo", $"{tool} /tray");
        reg.Set(Hive.CurrentUser, RunOnceUser, "FinishSetup", "setup.exe /finish");
        reg.Set(Hive.CurrentUser, ApprovedUser + @"\Run", "Gone", Off(), RegistryValueKind.Binary);
        reg.Set(Hive.LocalMachine, ApprovedMachine + @"\Run32", "ToolCo", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        var scanner = new StartupScanner(_ => Env.Fake(registry: reg.View));
        var result = await scanner.ScanAsync(sb.Context(), null, CancellationToken.None);
        var items = result.AllItems.ToList();

        var chatItem = items.Single(i => i.Title == "Chat");
        Assert.Equal("On", chatItem.CurrentState);
        Assert.Equal(chat, chatItem.Detail);
        var toggle = Assert.Single(chatItem.Actions);
        Assert.Equal(ActionKind.StartupItemSetEnabled, toggle.Kind);
        Assert.False(toggle.Enable);
        Assert.Equal("Run", toggle.Args!["location"]);
        Assert.Equal("Chat", toggle.Args["name"]);
        Assert.Equal("HKCU", toggle.Args["hive"]);
        Assert.False(toggle.NeedsAdmin);
        Assert.Contains(result.Groups, g => g.Id == StartupScanner.AppsGroup && g.Items.Contains(chatItem));

        var tool32 = items.Single(i => i.Title == "ToolCo");
        var t = Assert.Single(tool32.Actions);
        Assert.Equal("Run32", t.Args!["location"]);
        Assert.Equal("HKLM", t.Args["hive"]);
        Assert.True(t.NeedsAdmin);
        Assert.Equal(tool, tool32.Detail);

        // Switched off → in "Already switched off", offered to switch back on, with the missing file explained.
        var gone = items.Single(i => i.Title == "Gone");
        Assert.Equal("Off", gone.CurrentState);
        Assert.True(Assert.Single(gone.Actions).Enable);
        Assert.Contains(result.Groups, g => g.Id == StartupScanner.OffGroup && g.Items.Contains(gone));
        Assert.Contains("gone", gone.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(gone.Details!.ContainsKey("Switched off"));

        // RunOnce: shown so you know, never tickable.
        var once = items.Single(i => i.Title == "FinishSetup");
        Assert.NotNull(once.DisabledReason);
        Assert.Empty(once.Actions);

        // Jump-to-key: the registry path is in the details for "Copy path".
        Assert.EndsWith(RunUser, chatItem.Details!["Registry key"]);
        Assert.Equal("Not measured", chatItem.Details["Startup impact"]);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(items, i => Assert.False(i.DefaultSelected)); // no preset, nothing measured
    }

    [Fact]
    public async Task Startup_folder_shortcuts_are_resolved_and_toggled_by_file_name()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var target = sb.MakeFile(@"C\Program Files\Notes App\notes.exe");
        var folder = Path.Combine(sb.Locations.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Notes.lnk"), LinkBuilder.Build(target, "--hidden"));
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "[.ShellClassInfo]");

        var scanner = new StartupScanner(_ => Env.Fake(registry: reg.View));
        var result = await scanner.ScanAsync(sb.Context(), null, CancellationToken.None);
        var item = Assert.Single(result.AllItems, i => i.Details?["Starts from"].StartsWith("Startup folder") == true);
        Assert.Equal("Notes", item.Title);
        Assert.Equal(target, item.Detail);
        Assert.Contains("--hidden", item.Details!["Command"]);
        var a = Assert.Single(item.Actions);
        Assert.Equal("StartupFolder", a.Args!["location"]);
        Assert.Equal("Notes.lnk", a.Args["name"]);
        Assert.Equal(folder, item.Details["Folder"]);
    }

    [Fact]
    public async Task Microsoft_entries_are_hidden_unless_asked_for()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var drive = sb.MakeFile(@"C\Users\tester\AppData\Local\Microsoft\CloudDrive\clouddrive.exe");
        reg.Set(Hive.CurrentUser, RunUser, "CloudDrive", $"\"{drive}\" /background");
        var files = new FixedFileFacts(new[] { FixedFileFacts.Microsoft(drive, "Cloud drive") });
        var scanner = new StartupScanner(_ => Env.Fake(registry: reg.View, files: files));

        var hidden = await scanner.ScanAsync(sb.Context(hideMicrosoft: true), null, CancellationToken.None);
        Assert.Empty(hidden.AllItems);
        Assert.Contains(hidden.Notes, n => n.Contains("Microsoft entry is hidden"));

        var shown = await scanner.ScanAsync(sb.Context(hideMicrosoft: false), null, CancellationToken.None);
        var item = Assert.Single(shown.AllItems);
        Assert.Single(item.Actions); // Microsoft apps outside Windows (cloud drive, chat) can still be switched off
        Assert.Equal("Signed by Microsoft Corporation (verified)", item.Details!["Signature"]);
    }

    [Fact]
    public async Task Security_and_anti_cheat_entries_are_shown_but_never_offered()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var ac = sb.MakeFile(@"C\Program Files\Riot Vanguard\vgtray.exe");
        var av = sb.MakeFile(@"C\Program Files\SomeAV\avtray.exe");
        reg.Set(Hive.CurrentUser, RunUser, "Vanguard", $"\"{ac}\"");
        reg.Set(Hive.CurrentUser, RunUser, "AV tray", $"\"{av}\"");
        var files = new FixedFileFacts(new[] { FixedFileFacts.Signed(av, "Bitdefender SRL") });
        var result = await new StartupScanner(_ => Env.Fake(registry: reg.View, files: files)).ScanAsync(sb.Context(), null, CancellationToken.None);
        Assert.Equal(2, result.AllItems.Count());
        Assert.All(result.AllItems, i =>
        {
            Assert.Empty(i.Actions);
            Assert.NotNull(i.DisabledReason);
        });
    }

    [Fact]
    public async Task High_impact_non_Microsoft_apps_are_pre_ticked_only_by_Recommended_and_Gamer()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        var heavy = sb.MakeFile(@"C\Program Files\Heavy\heavy.exe");
        var light = sb.MakeFile(@"C\Program Files\Light\light.exe");
        reg.Set(Hive.CurrentUser, RunUser, "Heavy", $"\"{heavy}\"");
        reg.Set(Hive.CurrentUser, RunUser, "Light", $"\"{light}\"");

        var signIn = DateTime.UtcNow.AddMinutes(-2);
        var clock = new FixedSessionClock(DateTime.UtcNow, signIn);
        var processes = new FixedProcessSource(new[]
        {
            new ProcessSnapshot(1001, "heavy", heavy, 1, signIn.AddSeconds(8), TimeSpan.FromSeconds(2.5), 50 << 20, 6L << 20, false, null),
            new ProcessSnapshot(1002, "light", light, 1, signIn.AddSeconds(9), TimeSpan.FromMilliseconds(50), 5 << 20, 1L << 10, false, null),
        });
        var scanner = new StartupScanner(_ => Env.Fake(registry: reg.View, clock: clock, processes: processes));

        foreach (var preset in new[] { OptimizePresets.Recommended, OptimizePresets.Gamer })
        {
            var r = await scanner.ScanAsync(sb.Context(preset), null, CancellationToken.None);
            var h = r.AllItems.Single(i => i.Title == "Heavy");
            Assert.StartsWith("High", h.Details!["Startup impact"]);
            Assert.Equal(Badge.Safe, h.Badge);
            Assert.True(h.DefaultSelected);
            Assert.True(OptimizePresets.IsTickableByDefault(h));
            var l = r.AllItems.Single(i => i.Title == "Light");
            Assert.StartsWith("Low", l.Details!["Startup impact"]);
            Assert.False(l.DefaultSelected);
            Assert.Equal(Badge.Check, l.Badge);
            Assert.Equal("Heavy", r.Groups.Single(g => g.Id == StartupScanner.AppsGroup).Items[0].Title); // heaviest first
        }

        foreach (var preset in new[] { null, OptimizePresets.Safe, OptimizePresets.Privacy })
        {
            var r = await scanner.ScanAsync(sb.Context(preset), null, CancellationToken.None);
            Assert.All(r.AllItems, i => Assert.False(i.DefaultSelected));
        }
    }

    [Fact]
    public void Impact_uses_Microsofts_thresholds_and_is_never_guessed()
    {
        Assert.Equal(ImpactLevel.High, StartupImpactMeter.Rate(TimeSpan.FromMilliseconds(1001), 0));
        Assert.Equal(ImpactLevel.High, StartupImpactMeter.Rate(TimeSpan.Zero, 3 * 1024 * 1024 + 1));
        Assert.Equal(ImpactLevel.Medium, StartupImpactMeter.Rate(TimeSpan.FromMilliseconds(300), 0));
        Assert.Equal(ImpactLevel.Medium, StartupImpactMeter.Rate(TimeSpan.Zero, 400 * 1024));
        Assert.Equal(ImpactLevel.Low, StartupImpactMeter.Rate(TimeSpan.FromMilliseconds(299), 100 * 1024));

        // Scanned long after sign-in: counters no longer describe startup → not measured.
        var late = new StartupImpactMeter(new FixedSessionClock(DateTime.UtcNow, DateTime.UtcNow.AddHours(-3)),
            new FixedProcessSource(new[] { new ProcessSnapshot(9, "x", @"C:\x.exe", 1, DateTime.UtcNow.AddHours(-3), TimeSpan.FromSeconds(9), 0, 1L << 30, false, null) }));
        Assert.False(late.CanMeasure);
        Assert.Equal(ImpactLevel.NotMeasured, late.Measure(@"C:\x.exe").Level);

        // Unknown sign-in time → not measured.
        Assert.False(new StartupImpactMeter(new FixedSessionClock(DateTime.UtcNow, null), new FixedProcessSource()).CanMeasure);

        // A process we couldn't read CPU time for → not measured, rather than "Low".
        var signIn = DateTime.UtcNow.AddMinutes(-1);
        var partial = new StartupImpactMeter(new FixedSessionClock(DateTime.UtcNow, signIn),
            new FixedProcessSource(new[] { new ProcessSnapshot(9, "x", @"C:\x.exe", 1, signIn.AddSeconds(5), null, 0, null, false, null) }));
        Assert.Equal(ImpactLevel.NotMeasured, partial.Measure(@"C:\x.exe").Level);
        // An app that started later (not with the sign-in) isn't counted.
        var later = new StartupImpactMeter(new FixedSessionClock(DateTime.UtcNow, signIn.AddMinutes(-3)),
            new FixedProcessSource(new[] { new ProcessSnapshot(9, "x", @"C:\x.exe", 1, DateTime.UtcNow, TimeSpan.FromSeconds(5), 0, 5, false, null) }));
        Assert.Equal(ImpactLevel.NotMeasured, later.Measure(@"C:\x.exe").Level);
    }

    [Fact]
    public async Task Scheduled_tasks_outside_Microsoft_are_listed_and_turned_off_never_deleted()
    {
        using var sb = new SpeedSandbox();
        var tasks = new FixedTaskSource(new[]
        {
            new TaskInfo(@"\ToolCo Updater", true, true, false, @"C:\Program Files\ToolCo\update.exe", "/check", "ToolCo", "SYSTEM", true),
            new TaskInfo(@"\Microsoft\Windows\Something", true, true, false, @"C:\Windows\x.exe", null, "Microsoft", null, false),
            new TaskInfo(@"\Vendor\Old helper", false, false, true, @"C:\Vendor\helper.exe", null, null, null, false),
        });
        var result = await new StartupScanner(_ => Env.Fake(tasks: tasks)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var items = result.AllItems.ToList();
        Assert.Equal(2, items.Count);
        var updater = items.Single(i => i.Title == "ToolCo Updater");
        var a = Assert.Single(updater.Actions);
        Assert.Equal(ActionKind.ScheduledTaskSetEnabled, a.Kind);
        Assert.Equal(@"\ToolCo Updater", a.TaskPath);
        Assert.False(a.Enable);
        Assert.Contains(result.Groups, g => g.Id == StartupScanner.TasksGroup && g.Items.Contains(updater));
        var old = items.Single(i => i.Title == "Old helper");
        Assert.True(Assert.Single(old.Actions).Enable);
        Assert.DoesNotContain(items, i => i.Actions.Any(x => x.TaskPath?.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public async Task Winlogon_values_are_read_only_and_odd_values_are_flagged()
    {
        using var sb = new SpeedSandbox();
        using var reg = new FakeRegistry();
        reg.Set(Hive.LocalMachine, AdvancedReader.WinlogonKeyMachine, "Shell", "explorer.exe, C:\\Users\\Public\\odd.exe");
        reg.Set(Hive.LocalMachine, AdvancedReader.WinlogonKeyMachine, "Userinit", Path.Combine(sb.Locations.WindowsDir, "system32", "userinit.exe") + ",");
        reg.Set(Hive.CurrentUser, AdvancedReader.PolicyRunUser, "Forced", @"C:\Corp\agent.exe");
        var result = await new StartupScanner(_ => Env.Fake(registry: reg.View)).ScanAsync(sb.Context(), null, CancellationToken.None);
        var advanced = result.Groups.Single(g => g.Id == StartupScanner.AdvancedGroup).Items;
        Assert.Equal(3, advanced.Count);
        Assert.All(advanced, i => { Assert.Empty(i.Actions); Assert.NotNull(i.DisabledReason); });
        Assert.Equal("Changed", advanced.Single(i => i.Title == "Winlogon Shell").CurrentState);
        Assert.Equal("Default", advanced.Single(i => i.Title == "Winlogon Userinit").CurrentState);
        Assert.True(AdvancedReader.IsDefaultShell("explorer.exe"));
        Assert.False(AdvancedReader.IsDefaultUserinit(@"C:\Windows\system32\userinit.exe,C:\bad.exe", @"C:\Windows"));
    }
}
