using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Cleanup;
using PcTidy.Features.Games.Clips;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Games.Tests;

[Collection("games-testbed")]
public class ClipsAndCleanupTests
{
    private static readonly DateTime Old = GamesSandbox.Now.AddDays(-200);
    private static readonly DateTime Fresh = GamesSandbox.Now.AddDays(-3);

    [Fact]
    public async Task Exact_duplicates_are_safe_and_ticked_old_and_near_duplicates_are_check()
    {
        using var s = new GamesSandbox("clips-kinds", SandboxProfile.Minimal);
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var captures = Path.Combine(s.K.Videos, "Captures");
        var clip = GamesSandbox.RandomBytes(50_000, 1);
        var original = s.Put(Path.Combine(captures, "Game 2026-01-01 20-00-00.mp4"), clip, Old);
        var copy = s.Put(Path.Combine(captures, "Game 2026-01-01 20-00-00 (2).mp4"), clip, Fresh);
        var old = s.Put(Path.Combine(captures, "Old moment.mp4"), GamesSandbox.RandomBytes(40_000, 2), Old);
        var bestTake = s.Put(Path.Combine(s.K.Videos, "Medal", "Clips", "Ace.mp4"), GamesSandbox.RandomBytes(90_000, 3), Fresh);
        var worseTake = s.Put(Path.Combine(captures, "Ace (Game Bar).mp4"), GamesSandbox.RandomBytes(60_000, 4), Fresh);
        s.Put(Path.Combine(s.K.Videos, "Holiday.mp4"), GamesSandbox.RandomBytes(10_000, 5), Old); // not a capture folder: never listed

        var probe = new FakeMediaProbe();
        probe.Info["Ace.mp4"] = new MediaInfo(TimeSpan.FromSeconds(30.2), 1920, 1080);
        probe.Info["Ace (Game Bar).mp4"] = new MediaInfo(TimeSpan.FromSeconds(29.6), 1920, 1080);
        probe.Info["Old moment.mp4"] = new MediaInfo(TimeSpan.FromSeconds(30.0), 1280, 720); // different resolution
        probe.Hashes["Ace.mp4"] = 0xF0F0_F0F0_F0F0_F0F0;
        probe.Hashes["Ace (Game Bar).mp4"] = 0xF0F0_F0F0_F0F0_F0F1;
        s.Media = probe;

        var result = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);

        var dup = Assert.Single(result.Groups.Single(g => g.Id == "clip-duplicates").Items);
        Assert.Equal(copy, dup.Detail);                     // the newer copy goes, the original stays
        Assert.Equal(Badge.Safe, dup.Badge);
        Assert.True(dup.DefaultSelected);
        Assert.Equal(original, dup.Details!["Copy of"]);
        Assert.Equal(copy, dup.PreviewPath);
        var move = Assert.Single(dup.Actions);
        Assert.Equal(ActionKind.QuarantinePath, move.Kind);
        // GAM-1: pre-ticked only with the engine's re-check of both copies right before the move.
        Assert.Equal(PcTidy.Core.FileHash.Full(original), move.Args!["verifyHash"]);
        Assert.Equal(original, move.Args["keepPath"]);
        Assert.Equal("file", move.Args["verifyMode"]);

        var near = Assert.Single(result.Groups.Single(g => g.Id == "clip-near-duplicates").Items);
        Assert.Equal(worseTake, near.Detail);               // the bigger (better) take stays
        Assert.Equal(Badge.Check, near.Badge);
        Assert.False(near.DefaultSelected);

        var olds = result.Groups.Single(g => g.Id == "clip-old").Items;
        Assert.Contains(olds, i => i.Detail == old);
        // GAM-1: the copy that stays is never offered as "old" (ticking both lists can't remove every copy).
        Assert.DoesNotContain(olds, i => i.Detail == original);
        Assert.DoesNotContain(olds, i => i.Detail == copy);
        Assert.DoesNotContain(olds, i => i.Detail == bestTake);
        Assert.All(olds, i => Assert.False(i.DefaultSelected));

        Assert.DoesNotContain(result.AllItems, i => i.Detail?.EndsWith("Holiday.mp4") == true);
        Assert.Contains(result.Groups.Single(g => g.Id == "clip-sources").Items, i => i.Title == "Medal clips");
        Assert.All(result.AllItems.Where(i => i.Actions.Count > 0), i => Assert.NotNull(i.PreviewPath));
        Assert.All(result.AllItems.Where(i => i.Badge is Badge.Check or Badge.Permanent), i => Assert.False(i.DefaultSelected));
        Assert.Contains(bestTake, near.Details!["Looks like"]);
    }

    [Fact]
    public async Task Online_only_clips_are_never_hashed_or_probed()
    {
        using var s = new GamesSandbox("clips-cloud", SandboxProfile.Minimal);
        var captures = Path.Combine(s.K.Videos, "Captures");
        var bytes = GamesSandbox.RandomBytes(30_000, 9);
        s.Put(Path.Combine(captures, "a.mp4"), bytes, Old);
        var b = s.Put(Path.Combine(captures, "b.mp4"), bytes, Old);
        File.SetAttributes(b, FileAttributes.Offline); // simulated OneDrive online-only file
        var probe = new FakeMediaProbe();
        s.Media = probe;

        var result = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(result.Groups, g => g.Id == "clip-duplicates");
        Assert.DoesNotContain(b, probe.Asked);
        Assert.Contains(result.Groups.Single(g => g.Id == "clip-old").Items, i => i.Detail == b && i.Details!.ContainsKey("OneDrive"));
        File.SetAttributes(b, FileAttributes.Normal);
    }

    [Fact]
    public async Task Clip_rows_in_videos_honour_the_real_guards_folder_permission()
    {
        using var s = new GamesSandbox("clips-permission", SandboxProfile.Minimal);
        var bytes = GamesSandbox.RandomBytes(20_000, 7);
        s.Put(Path.Combine(s.K.Videos, "Captures", "x.mp4"), bytes, Old);
        s.Put(Path.Combine(s.K.Videos, "Captures", "x (1).mp4"), bytes, Fresh);

        var before = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var blocked = before.Groups.Single(g => g.Id == "clip-duplicates").Items.Single();
        if (blocked.DisabledReason is not null)
        {
            Assert.Empty(blocked.Actions);
            Assert.False(blocked.DefaultSelected); // a row that can't be picked is never ticked
            Assert.Contains(before.Notes, n => n.Contains("can't be picked", StringComparison.Ordinal));
        }

        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var after = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var allowed = after.Groups.Single(g => g.Id == "clip-duplicates").Items.Single();
        Assert.Null(allowed.DisabledReason);
        Assert.True(allowed.DefaultSelected);
    }

    [Fact]
    public async Task Nvidia_obs_and_steam_folders_come_from_their_own_settings()
    {
        using var s = new GamesSandbox("clips-sources");
        var nvidiaFolder = Path.Combine(s.K.FixedDrives[1], "Clips", "NVIDIA");
        s.Put(Path.Combine(nvidiaFolder, "Game", "Game 2026.10.02 - 21.10.00.01.mp4"), GamesSandbox.RandomBytes(1000, 1), Fresh);
        ((FakeEnvironment)s.Environment).Nvidia = Encoding.Unicode.GetBytes(nvidiaFolder + "\0");

        s.Put(Path.Combine(s.K.RoamingAppData, "obs-studio", "basic", "profiles", "Untitled", "basic.ini"),
            $"[SimpleOutput]\nFilePath={s.K.Videos.Replace('\\', '/')}\n");
        s.Put(Path.Combine(s.K.Videos, "2026-10-02 21-10-00.mkv"), GamesSandbox.RandomBytes(1000, 2), Fresh);
        s.Put(Path.Combine(s.K.Videos, "Wedding.mkv"), GamesSandbox.RandomBytes(1000, 3), Fresh); // in Videos, but not an OBS recording

        var shot = s.Put(Path.Combine(s.SteamRoot, "userdata", "12345678", "760", "remote", "440", "screenshots", "20261002_1.jpg"), GamesSandbox.RandomBytes(1000, 4), Old);

        var result = await new ClipsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var sources = result.Groups.Single(g => g.Id == "clip-sources").Items;
        Assert.Contains(sources, i => i.Title.StartsWith("NVIDIA") && i.Detail == nvidiaFolder && i.FileCount == 1);
        Assert.Contains(sources, i => i.Title == "OBS Studio recordings" && i.FileCount == 1);
        var steam = sources.Single(i => i.Title == "Steam screenshots");
        Assert.Empty(steam.Actions);
        Assert.Contains("Steam", steam.Reason);
        // Steam's own folder is never offered for removal, even when old.
        Assert.DoesNotContain(result.AllItems.Where(i => i.Actions.Count > 0), i => i.Detail == shot);
        Assert.DoesNotContain(result.AllItems, i => i.Detail?.EndsWith("Wedding.mkv") == true);
    }

    [Fact]
    public async Task Per_game_cleanup_offers_only_logs_and_caches_never_saves_or_settings()
    {
        using var s = new GamesSandbox("cleanup-targets");
        var saved = Path.Combine(s.K.LocalAppData, "FortniteGame", "Saved");
        var log = s.Put(Path.Combine(saved, "Logs", "FortniteGame.log"), new string('l', 4000), Old);
        s.Put(Path.Combine(saved, "Logs", "backup", "FortniteGame-backup.log"), new string('b', 1000), Old);
        s.Put(Path.Combine(saved, "D3D12Cache", "cache.bin"), new string('c', 3000), Old);
        var config = s.Put(Path.Combine(saved, "Config", "WindowsClient", "GameUserSettings.ini"), "[x]\na=1\n", Old);
        var demo = s.Put(Path.Combine(saved, "Demos", "replay.replay"), "r", Old);
        var mcLog = s.Put(Path.Combine(s.K.RoamingAppData, ".minecraft", "logs", "latest.log"), "m", Old);
        var mcSave = s.Put(Path.Combine(s.K.RoamingAppData, ".minecraft", "saves", "World", "level.dat"), "w", Old);
        var unityLog = s.Put(Path.Combine(s.K.UserProfile, "AppData", "LocalLow", "Indie Co", "Tiny Game", "Player.log"), "u", Old);
        var unitySave = s.Put(Path.Combine(s.K.UserProfile, "AppData", "LocalLow", "Indie Co", "Tiny Game", "save.dat"), "s", Old);

        var result = await new GameCleanupScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var actions = result.AllItems.SelectMany(i => i.Actions).ToList();
        var targets = actions.Select(a => a.SourcePath!).ToList();

        Assert.Contains(log, targets);
        Assert.Contains(Path.Combine(saved, "Logs", "backup"), targets);
        Assert.Contains(Path.Combine(saved, "D3D12Cache", "cache.bin"), targets);
        Assert.Contains(mcLog, targets);
        Assert.Contains(unityLog, targets);
        foreach (var never in new[] { config, demo, mcSave, unitySave, Path.Combine(saved, "Logs") })
            Assert.DoesNotContain(targets, t => PathUtil.IsUnder(never, t)); // neither the file nor anything holding it
        Assert.All(actions, a => Assert.Equal(ActionKind.QuarantinePath, a.Kind));

        Assert.All(result.AllItems, i => { Assert.Equal(Badge.Check, i.Badge); Assert.False(i.DefaultSelected); });
        var fortniteLogs = result.AllItems.Single(i => i.Title == "Fortnite: logs");
        Assert.Null(fortniteLogs.DisabledReason); // the real guard allows the game's own log folder
        Assert.Equal(5000, fortniteLogs.SizeBytes);
        Assert.Contains("FortniteClient-Win64-Shipping", fortniteLogs.RequiresClosedApps!);
        var shader = result.AllItems.Single(i => i.Title == "Fortnite: shader cache");
        Assert.Contains("Games rebuild this on first launch; that can take 1 to 2 hours", shader.Reason);
        Assert.All(result.AllItems, i => Assert.StartsWith("https://", i.Details!["Source"]));
    }

    [Fact]
    public async Task Steam_shader_pre_cache_is_shown_but_never_offered()
    {
        using var s = new GamesSandbox("cleanup-steam", generate: true);
        var result = await new GameCleanupScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var row = result.AllItems.Single(i => i.Title == "Some Game: Steam shader pre-cache");
        Assert.Empty(row.Actions);
        Assert.Equal(0, row.SizeBytes);
        Assert.NotNull(row.CurrentState);
    }
}
