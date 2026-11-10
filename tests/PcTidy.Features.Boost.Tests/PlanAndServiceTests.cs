using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Planning;
using PcTidy.Features.Boost.State;
using Xunit;

namespace PcTidy.Features.Boost.Tests;

public class PlanTests
{
    private static (GameMatch Match, IReadOnlyList<ObservedProcess> Snapshot) Running(BoostRig rig)
    {
        rig.StartGame();
        var snapshot = new ProcessPoller(rig.Processes).Snapshot();
        var match = GameMatcher.Match(new[] { rig.Game }, snapshot, null)!;
        return (match, snapshot);
    }

    [Fact]
    public void Boost_plan_is_onedrive_shutdown_plus_one_polite_close_per_picked_app()
    {
        using var rig = new BoostRig();
        var (match, snapshot) = Running(rig);

        var result = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow);

        var plan = result.Plan!;
        Assert.Equal("boost", plan.FeatureId);
        Assert.Equal(3, plan.Items.Count);
        Assert.All(plan.Items, i => Assert.Equal("boost", i.FeatureId));
        Assert.All(plan.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
        var od = plan.Items[0];
        Assert.Equal(ActionKind.RunSystemTool, od.Kind);
        Assert.Equal(ToolIds.OneDriveShutdown, od.ToolId);
        var closes = plan.Items.Skip(1).ToList();
        Assert.All(closes, c => { Assert.Equal(ActionKind.CloseApp, c.Kind); Assert.False(c.Reversible); Assert.False(c.NeedsAdmin); });
        // Discord's windowed process (not its helper), with the start time so a reused pid is refused by the handler.
        Assert.Equal(BoostRig.DiscordPid.ToString(), closes[0].Args!["pid"]);
        Assert.Equal("Discord", closes[0].Args!["processName"]);
        Assert.True(DateTime.TryParse(closes[0].Args!["startedUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.Equal(BoostRig.SpotifyPid.ToString(), closes[1].Args!["pid"]);
        // Windows Update: never a registry, service or task change.
        Assert.DoesNotContain(plan.Items, i => i.Kind is ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue or ActionKind.ServiceSetStartType or ActionKind.ScheduledTaskSetEnabled);

        Assert.True(result.State.Active);
        Assert.True(result.State.OneDrivePaused);
        Assert.Equal(new[] { "Discord", "Spotify" }, result.State.ClosedApps.Select(a => a.ProcessName));
        Assert.Equal("--start-minimized", result.State.ClosedApps[0].Arguments);
        // A command line that looks like it carries a secret is never stored.
        Assert.Null(result.State.ClosedApps[1].Arguments);
    }

    [Fact]
    public void Onedrive_is_left_alone_unless_ticked_and_running()
    {
        using var rig = new BoostRig();
        var (match, snapshot) = Running(rig);
        rig.Settings.PauseOneDrive = false;
        var plan = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow).Plan!;
        Assert.DoesNotContain(plan.Items, i => i.Kind == ActionKind.RunSystemTool);

        rig.Settings.PauseOneDrive = true;
        var noOneDrive = snapshot.Where(p => p.Name != "OneDrive").ToList();
        var result = BoostPlanner.PlanBoost(match, rig.Settings, noOneDrive, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow);
        Assert.DoesNotContain(result.Plan!.Items, i => i.Kind == ActionKind.RunSystemTool);
        Assert.False(result.State.OneDrivePaused);
    }

    [Fact]
    public void Deny_listed_launcher_and_game_processes_are_never_closed_even_if_picked()
    {
        using var rig = new BoostRig();
        rig.Processes.Add(950, "EasyAntiCheat_EOS", Path.Combine(rig.Game.InstallDir, "EasyAntiCheat", "EasyAntiCheat_EOS.exe"));
        rig.Processes.Add(960, "Helper", Path.Combine(rig.Game.InstallDir, "Helper.exe"));
        rig.Processes.Add(970, "notepad", Path.Combine(rig.WindowsDir, "notepad.exe"), window: true);
        var (match, snapshot) = Running(rig);
        rig.Settings.AppsToClose.Clear();
        foreach (var name in new[] { "EasyAntiCheat_EOS", "steam", "SpaceGame", "Helper", "explorer", "notepad" })
            rig.Settings.AppsToClose.Add(new BoostAppChoice { ProcessName = name, DisplayName = name });
        rig.Settings.PauseOneDrive = false;

        var result = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow);

        Assert.Null(result.Plan);
        Assert.Empty(result.State.ClosedApps);
        // The game, its anti-cheat and the launcher were never asked for their command line either.
        Assert.Empty(rig.CommandLines.Asked);
        // ...and the anti-cheat was never even opened with limited rights.
        Assert.DoesNotContain(rig.Processes.Opened, o => o.Pid == 950);
    }

    [Fact]
    public void Picked_apps_that_arent_running_are_skipped()
    {
        using var rig = new BoostRig();
        rig.Processes.Remove("Spotify");
        var (match, snapshot) = Running(rig);
        var plan = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow).Plan!;
        Assert.Single(plan.Items, i => i.Kind == ActionKind.CloseApp);
    }

    [Fact]
    public void Restore_plan_starts_onedrive_and_reopens_only_closed_apps_that_arent_running()
    {
        using var rig = new BoostRig();
        var state = new BoostState
        {
            Active = true, GameKey = rig.Game.Key, GameName = rig.Game.Name, OneDrivePaused = true,
            ClosedApps =
            {
                new ClosedApp { ProcessName = "Discord", DisplayName = "Discord", ExePath = rig.DiscordExe, Arguments = "--start-minimized" },
                new ClosedApp { ProcessName = "Spotify", DisplayName = "Spotify", ExePath = rig.SpotifyExe },
                new ClosedApp { ProcessName = "Gone", DisplayName = "Gone", ExePath = Path.Combine(rig.Folder.Root, "missing", "Gone.exe") },
            },
        };
        rig.Processes.Remove("OneDrive");
        rig.Processes.Remove("Discord");
        var snapshot = new ProcessPoller(rig.Processes).Snapshot(); // Spotify is still running: not started twice

        var restore = BoostPlanner.PlanRestore(state, snapshot, reopenApps: true, DateTime.UtcNow);

        var items = restore.Plan!.Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(ToolIds.OneDriveStart, items[0].ToolId);
        Assert.Equal(ActionKind.LaunchApp, items[1].Kind);
        Assert.Equal(rig.DiscordExe, items[1].Args!["exePath"]);
        Assert.Equal("--start-minimized", items[1].Args!["arguments"]);
        Assert.False(items[1].Reversible);
        Assert.False(items[1].NeedsAdmin);
        Assert.Empty(restore.Offers);
    }

    [Fact]
    public void With_reopening_off_closed_apps_become_reopen_buttons()
    {
        using var rig = new BoostRig();
        rig.Processes.Remove("Discord");
        var state = new BoostState
        {
            Active = true, GameName = "Space Game",
            ClosedApps = { new ClosedApp { ProcessName = "Discord", DisplayName = "Discord", ExePath = rig.DiscordExe } },
        };
        var restore = BoostPlanner.PlanRestore(state, new ProcessPoller(rig.Processes).Snapshot(), reopenApps: false, DateTime.UtcNow);
        Assert.Null(restore.Plan);
        var offer = Assert.Single(restore.Offers);
        var after = BoostPlanner.AfterRestoreRun(state, restore, null);
        Assert.False(after.Active);
        Assert.Single(after.ReopenOffers);

        var reopen = BoostPlanner.PlanReopen(offer);
        var item = Assert.Single(reopen.Items);
        Assert.Equal(ActionKind.LaunchApp, item.Kind);
        Assert.Equal("Reopen Discord", reopen.Title);
    }

    [Fact]
    public void Only_what_really_changed_is_kept_for_the_restore()
    {
        using var rig = new BoostRig();
        var (match, snapshot) = Running(rig);
        var planned = BoostPlanner.PlanBoost(match, rig.Settings, snapshot, rig.Processes.WindowPids, rig.CommandLines, rig.WindowsDir, DateTime.UtcNow);
        var steps = planned.Plan!.Items.Select((item, i) => new StepRecord
        {
            Index = i, Item = item,
            // OneDrive was already stopped by the user; Spotify refused (asked to save).
            Status = item.Kind == ActionKind.CloseApp && item.Args!["processName"] == "Discord" ? StepStatus.Done : StepStatus.Skipped,
        }).ToList();
        var run = new RunRecord { RunId = planned.Plan.RunId, FeatureId = "boost", Title = "t", Steps = steps };

        var after = BoostPlanner.AfterBoostRun(planned.State, run);

        Assert.True(after.Active);
        Assert.False(after.OneDrivePaused);
        Assert.Equal("Discord", Assert.Single(after.ClosedApps).ProcessName);
    }

    [Theory]
    [InlineData("\"C:\\A B\\app.exe\" --x 1", "--x 1")]
    [InlineData("C:\\app.exe --y", "--y")]
    [InlineData("\"C:\\A B\\app.exe\"", null)]
    [InlineData("", null)]
    public void Arguments_are_split_from_the_exe(string line, string? expected)
        => Assert.Equal(expected, CommandLineText.ArgumentsAfterExe(line));

    [Theory]
    [InlineData("--token=abc")]
    [InlineData("--password hunter2")]
    [InlineData("--auth-key=1")]
    public void Secret_looking_arguments_are_never_kept(string args)
        => Assert.Null(CommandLineText.SafeArguments("app.exe " + args));
}

public class ServiceTests
{
    [Fact]
    public async Task No_plan_when_the_running_game_isnt_enabled()
    {
        using var rig = new BoostRig();
        rig.Settings.SetGame(rig.Game.Key, rig.Game.Name, false);
        rig.StartGame();
        var service = rig.CreateService();

        Assert.Null(await service.PollOnceAsync(CancellationToken.None));

        Assert.Empty(rig.Flow.Approved);
        Assert.False(service.State.Active);
        Assert.False(File.Exists(rig.Store.FilePath));
    }

    [Fact]
    public async Task No_plan_while_game_boost_is_switched_off()
    {
        using var rig = new BoostRig();
        rig.Settings.Enabled = false;
        rig.StartGame();
        var service = rig.CreateService();
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(rig.Flow.Approved);
        Assert.Equal(BoostPhase.Off, service.Status.Phase);
    }

    [Fact]
    public async Task Game_start_runs_one_approved_plan_and_game_exit_restores_everything()
    {
        using var rig = new BoostRig();
        var service = rig.CreateService(exitGracePolls: 2);
        BoostState? onDiskWhenRunning = null;
        rig.Flow.BeforeRun = _ => onDiskWhenRunning ??= rig.Store.Load();

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(rig.Flow.Approved);
        Assert.Equal(BoostPhase.Watching, service.Status.Phase);

        rig.StartGame();
        var boostRun = await service.PollOnceAsync(CancellationToken.None);
        Assert.NotNull(boostRun);
        var boost = Assert.Single(rig.Flow.Approved);
        Assert.Equal(3, boost.Items.Count);
        // The state was on disk BEFORE anything changed (crash-safe).
        Assert.True(onDiskWhenRunning!.Active);
        Assert.True(onDiskWhenRunning.OneDrivePaused);
        Assert.Equal(2, onDiskWhenRunning.ClosedApps.Count);
        Assert.Equal(BoostPhase.Active, service.Status.Phase);
        Assert.Contains("Space Game", service.Status.Text);

        // The apps really closed; the game keeps running: nothing more happens.
        rig.Processes.Remove("Discord");
        rig.Processes.Remove("Spotify");
        rig.Processes.Remove("OneDrive");
        await service.PollOnceAsync(CancellationToken.None);
        Assert.Single(rig.Flow.Approved);

        rig.StopGame();
        Assert.Null(await service.PollOnceAsync(CancellationToken.None)); // grace poll (launchers restart exes)
        var restoreRun = await service.PollOnceAsync(CancellationToken.None);
        Assert.NotNull(restoreRun);
        var restore = rig.Flow.Approved[1];
        Assert.Equal(ToolIds.OneDriveStart, restore.Items[0].ToolId);
        Assert.Equal(new[] { rig.DiscordExe, rig.SpotifyExe }, restore.Items.Skip(1).Select(i => i.Args!["exePath"]));
        Assert.All(restore.Items.Skip(1), i => Assert.Equal(ActionKind.LaunchApp, i.Kind));
        Assert.False(service.State.Active);
        Assert.False(rig.Store.Load().Active);
        Assert.Equal(BoostPhase.Watching, service.Status.Phase);
    }

    [Fact]
    public async Task Crash_mid_game_is_put_right_at_the_next_start()
    {
        using var rig = new BoostRig();
        var first = rig.CreateService();
        rig.StartGame();
        await first.PollOnceAsync(CancellationToken.None);
        Assert.True(rig.Store.Load().Active);
        // "Crash": the service is gone without restoring. Meanwhile the game closed and the apps are still closed.
        rig.StopGame();
        rig.Processes.Remove("Discord");
        rig.Processes.Remove("Spotify");
        rig.Processes.Remove("OneDrive");
        rig.Flow.Approved.Clear();

        var second = rig.CreateService();
        Assert.Equal(BoostPhase.Active, second.Status.Phase);
        var run = await second.RecoverAtStartAsync(CancellationToken.None);

        Assert.NotNull(run);
        var restore = Assert.Single(rig.Flow.Approved);
        Assert.Contains(restore.Items, i => i.ToolId == ToolIds.OneDriveStart);
        Assert.Equal(2, restore.Items.Count(i => i.Kind == ActionKind.LaunchApp));
        Assert.False(rig.Store.Load().Active);
    }

    [Fact]
    public async Task Restart_while_the_game_still_runs_keeps_the_boost()
    {
        using var rig = new BoostRig();
        rig.StartGame();
        await rig.CreateService().PollOnceAsync(CancellationToken.None);
        rig.Flow.Approved.Clear();

        var second = rig.CreateService();
        Assert.Null(await second.RecoverAtStartAsync(CancellationToken.None));
        Assert.Empty(rig.Flow.Approved);
        Assert.True(second.State.Active);
        // ...and no second boost plan while it's still running.
        await second.PollOnceAsync(CancellationToken.None);
        Assert.Empty(rig.Flow.Approved);
    }

    [Fact]
    public async Task Crash_restore_from_a_hand_written_state_file()
    {
        using var rig = new BoostRig();
        rig.Processes.Remove("OneDrive");
        rig.Processes.Remove("Discord");
        Directory.CreateDirectory(rig.Store.Folder);
        File.WriteAllText(rig.Store.FilePath, JsonSerializer.Serialize(new BoostState
        {
            Active = true, GameKey = rig.Game.Key, GameName = rig.Game.Name, OneDrivePaused = true,
            ClosedApps = { new ClosedApp { ProcessName = "Discord", DisplayName = "Discord", ExePath = rig.DiscordExe } },
        }, AppSettings.Json));

        var service = rig.CreateService();
        await service.RecoverAtStartAsync(CancellationToken.None);

        var plan = Assert.Single(rig.Flow.Approved);
        Assert.Equal(new[] { ActionKind.RunSystemTool, ActionKind.LaunchApp }, plan.Items.Select(i => i.Kind));
        Assert.False(rig.Store.Load().Active);
    }

    [Fact]
    public async Task A_damaged_state_file_never_crashes()
    {
        using var rig = new BoostRig();
        Directory.CreateDirectory(rig.Store.Folder);
        File.WriteAllText(rig.Store.FilePath, "{ not json");
        var service = rig.CreateService();
        Assert.Null(await service.RecoverAtStartAsync(CancellationToken.None));
        Assert.False(service.State.Active);
        Assert.NotNull(rig.Store.LastProblem);
    }

    [Fact]
    public async Task Switching_boost_off_mid_game_puts_things_back_at_once()
    {
        using var rig = new BoostRig();
        var service = rig.CreateService();
        rig.StartGame();
        await service.PollOnceAsync(CancellationToken.None);
        rig.Processes.Remove("Discord");
        rig.Processes.Remove("OneDrive");

        rig.Settings.Enabled = false;
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, rig.Flow.Approved.Count);
        Assert.False(service.State.Active);
        Assert.Equal(BoostPhase.Off, service.Status.Phase);
    }

    [Fact]
    public async Task A_refused_restore_is_retried_then_offered_as_buttons()
    {
        using var rig = new BoostRig();
        var service = rig.CreateService(exitGracePolls: 1);
        rig.StartGame();
        await service.PollOnceAsync(CancellationToken.None);
        rig.Processes.Remove("Discord");
        rig.StopGame();
        rig.Flow.ReturnNull = true;

        for (int i = 0; i < 3; i++) await service.PollOnceAsync(CancellationToken.None);

        Assert.False(service.State.Active);
        Assert.Contains(service.State.ReopenOffers, a => a.ProcessName == "Discord");
        service.ForgetOffer(rig.DiscordExe);
        Assert.DoesNotContain(rig.Store.Load().ReopenOffers, a => a.ProcessName == "Discord");
    }

    [Fact]
    public async Task Start_and_stop_never_block_and_stop_doesnt_restore()
    {
        using var rig = new BoostRig();
        rig.StartGame();
        var service = rig.CreateService();
        service.Start();
        for (int i = 0; i < 100 && rig.Flow.Approved.Count == 0; i++) await Task.Delay(20);
        await service.StopAsync();
        Assert.Single(rig.Flow.Approved);
        Assert.False(service.IsRunning);
        Assert.True(rig.Store.Load().Active); // put back at the next start, not on exit
    }
}
