using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Optimize;
using Xunit;

namespace PcTidy.Features.Games.Tests;

[Collection("games-testbed")]
public class OptimizerTests
{
    /// <summary>The row names the option in its title or (for launch options kept out of the title) in its details.</summary>
    private static bool Mentions(ScanItem i, string text)
        => i.Title.Contains(text, StringComparison.Ordinal) || (i.Details?.Values.Any(v => v.Contains(text, StringComparison.Ordinal)) ?? false);

    private const long Updated = 1735732800;

    private static string LocalConfig(string appsBody) =>
        "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        appsBody + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    private static string App(string id, string? launchOptions) =>
        $"\t\t\t\t\t\"{id}\"\n\t\t\t\t\t{{\n" + (launchOptions is null ? "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n" : $"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{launchOptions}\"\n") + "\t\t\t\t\t}\n";

    // ---------------- engine and anti-cheat detection ----------------

    [Fact]
    public void Engine_detection_reads_folder_layouts()
    {
        using var s = new GamesSandbox("engines");
        var w = s.Host.Walker;
        var lib = Path.Combine(s.K.SystemDrive, "Games");
        GameLayout Make(string name, params string[] files)
        {
            foreach (var f in files) s.Put(Path.Combine(lib, name, f), "x");
            return GameInspector.Inspect(w, Path.Combine(lib, name), CancellationToken.None);
        }

        var unity = Make("UnityGame", "UnityGame.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", @"UnityGame_Data\globalgamemanagers");
        Assert.Equal(GameEngine.Unity, unity.Engine);
        Assert.Equal(new[] { "UnityGame" }, unity.ProcessNames); // the crash handler is not the game

        var unreal = Make("UnrealGame", "UnrealGame.exe", @"Engine\Binaries\ThirdParty\x.dll", @"Shooter\Binaries\Win64\Shooter-Win64-Shipping.exe", @"Shooter\Content\Paks\Shooter-WindowsClient.pak");
        Assert.Equal(GameEngine.Unreal, unreal.Engine);
        Assert.Contains("Shooter-Win64-Shipping", unreal.ProcessNames);
        Assert.Contains("Shooter", unreal.UnrealProjects);

        Assert.Equal(GameEngine.Source, Make("SourceGame", "hl2.exe", @"bin\engine.dll", @"tf\gameinfo.txt").Engine);
        Assert.Equal(GameEngine.Source2, Make("Source2Game", @"game\bin\win64\engine2.dll", @"game\bin\win64\cs2.exe").Engine);
        Assert.Equal(GameEngine.Godot, Make("GodotGame", "GodotGame.exe", "GodotGame.pck").Engine);
        Assert.Equal(GameEngine.Unknown, Make("Mystery", "mystery.exe", "data.bin").Engine);

        Assert.Equal(AntiCheat.EasyAntiCheat, Make("Protected", "Protected.exe", "UnityPlayer.dll", @"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe").AntiCheat);
        Assert.Equal(AntiCheat.BattlEye, Make("Protected2", "Game.exe", @"Game\BattlEye\BEClient_x64.dll").AntiCheat);
        Assert.Equal(AntiCheat.None, unity.AntiCheat);
    }

    // ---------------- planning ----------------

    [Fact]
    public async Task Unity_game_gets_an_exact_localconfig_edit_that_needs_steam_and_the_game_closed()
    {
        using var s = new GamesSandbox("opt-unity");
        var install = s.SteamGame(s.SteamRoot, "1234560", "Some Game", "SomeGame", null, Updated, "SomeGame.exe", "UnityPlayer.dll");
        var original = LocalConfig(App("1234560", null));
        s.Put(s.LocalConfigPath(), original);

        var result = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        var group = Assert.Single(result.Groups);
        Assert.Contains("Unity", group.Description);
        var item = group.Items.Single(i => Mentions(i, "-window-mode exclusive"));
        Assert.Equal("Use exclusive fullscreen", item.Title); // the option itself is in the details, not the title
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.Equal("https://docs.unity.com/en-us/engine/current/manual/unity-editor/command-line-arguments/player", item.Details!["Source"]);

        var edit = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.EditTextFile, edit.Kind);
        Assert.Equal(s.LocalConfigPath(), edit.SourcePath);
        Assert.Equal(original.Replace(
                "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n",
                "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-window-mode exclusive\"\n"),
            edit.Args![EditArgs.Content]);
        Assert.Contains("steam", edit.RequiresClosedApps!);
        Assert.Contains("SomeGame", edit.RequiresClosedApps!);
        Assert.Equal(File.GetLastWriteTimeUtc(s.LocalConfigPath()), edit.ExpectedLastWriteUtc);
        Assert.Equal(0, edit.ExpectedSize);
        Assert.False(PathUtil.IsUnder(edit.SourcePath!, install));
    }

    [Fact]
    public async Task Placebo_options_are_offered_for_removal_and_wrong_engine_options_are_never_added()
    {
        using var s = new GamesSandbox("opt-source");
        s.SteamGame(s.SteamRoot, "440", "Team Fortress 2", "Team Fortress 2", null, Updated, "hl2.exe", @"bin\engine.dll");
        s.SteamGame(s.SteamRoot, "730", "Counter-Strike 2", "Counter-Strike Global Offensive", null, Updated, @"game\bin\win64\engine2.dll", @"game\bin\win64\cs2.exe");
        var original = LocalConfig(App("440", "-high -threads 8") + App("730", null));
        s.Put(s.LocalConfigPath(), original);

        var result = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        var tf2 = result.Groups.Single(g => g.Title == "Team Fortress 2").Items;
        var novid = tf2.Single(i => Mentions(i, "-novid"));
        Assert.Contains("\"LaunchOptions\"\t\t\"-high -threads 8 -novid\"", novid.Actions.Single().Args![EditArgs.Content]);
        var removeHigh = tf2.Single(i => i.Title.Contains("\"-high\""));
        Assert.Contains("\"LaunchOptions\"\t\t\"-threads 8\"", removeHigh.Actions.Single().Args![EditArgs.Content]);
        var removeThreads = tf2.Single(i => i.Title.Contains("\"-threads\""));
        Assert.Contains("\"LaunchOptions\"\t\t\"-high\"", removeThreads.Actions.Single().Args![EditArgs.Content]);

        var cs2 = result.Groups.Single(g => g.Title == "Counter-Strike 2").Items;
        Assert.DoesNotContain(cs2, i => Mentions(i, "-novid")); // CS2 has no intro video
        Assert.DoesNotContain(cs2, i => Mentions(i, "window-mode")); // a Unity option on a Source 2 game: never
        Assert.Contains(cs2, i => Mentions(i, "+fps_max 0") && i.Actions.Count == 1);
        Assert.All(result.AllItems, i => Assert.True(i.Details!.ContainsKey("Source") && i.Details["Source"].StartsWith("https://")));
    }

    [Fact]
    public async Task Ticked_edits_of_the_same_file_are_merged_into_one_exact_edit()
    {
        using var s = new GamesSandbox("opt-merge");
        s.SteamGame(s.SteamRoot, "440", "Team Fortress 2", "Team Fortress 2", null, Updated, "hl2.exe", @"bin\engine.dll");
        var original = LocalConfig(App("440", "-high"));
        s.Put(s.LocalConfigPath(), original);
        var result = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        var picked = result.AllItems.Where(i => i.Actions.Count > 0).ToList();
        Assert.Equal(2, picked.Count); // add -novid, remove -high

        var plan = PlanBuilder.FromSelection("games", "Games", picked);
        var merged = Assert.Single(EditMerger.Merge(plan.Items));
        Assert.Equal(LocalConfig(App("440", "-novid")), merged.Args![EditArgs.Content]);
        Assert.Contains("steam", merged.RequiresClosedApps!);

        // Steam saved its settings after the scan: refuse instead of overwriting them.
        File.SetLastWriteTimeUtc(s.LocalConfigPath(), DateTime.UtcNow.AddMinutes(5));
        Assert.Throws<EditMerger.FileChangedException>(() => EditMerger.Merge(plan.Items));
    }

    [Fact]
    public async Task Unreal_game_config_edit_targets_only_the_users_ini_outside_the_install_folder()
    {
        using var s = new GamesSandbox("opt-unreal");
        var install = s.SteamGame(s.SteamRoot, "555", "Shooter", "Shooter", null, Updated,
            @"Shooter\Binaries\Win64\Shooter-Win64-Shipping.exe", @"Engine\Binaries\Win64\x.dll", @"Shooter\Content\Paks\Shooter-WindowsClient.pak");
        // A config inside the game folder must never be targeted.
        s.Put(Path.Combine(install, "Shooter", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), "[/Script/Engine.GameUserSettings]\nbUseVSync=True\n");
        var userIni = Path.Combine(s.K.LocalAppData, "Shooter", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
        var text = "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=0.000000\r\n";
        s.Put(userIni, text);

        var result = await new OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var vsync = result.AllItems.Single(i => i.Title.Contains("VSync"));
        var edit = Assert.Single(vsync.Actions);
        Assert.Equal(userIni, edit.SourcePath);
        Assert.Equal(text.Replace("bUseVSync=True", "bUseVSync=False"), edit.Args![EditArgs.Content]);
        Assert.Contains("Shooter-Win64-Shipping", edit.RequiresClosedApps!);
        Assert.Null(vsync.DisabledReason); // the real guard allows the game's own user config
    }

    [Fact]
    public async Task Games_with_an_anti_cheat_get_information_only()
    {
        using var s = new GamesSandbox("opt-anticheat");
        // Detected from the folder: Unity + Easy Anti-Cheat.
        s.SteamGame(s.SteamRoot, "9001", "Protected Game", "Protected", null, Updated, "Protected.exe", "UnityPlayer.dll", @"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe");
        // Known from the catalog: Apex Legends (EAC) with -high already set.
        s.SteamGame(s.SteamRoot, "1172470", "Apex Legends", "Apex Legends", null, Updated, "r5apex.exe", @"bin\x64_retail\engine.dll");
        var unrealIni = Path.Combine(s.K.LocalAppData, "Protected", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
        s.Put(unrealIni, "[/Script/Engine.GameUserSettings]\nbUseVSync=True\n");
        s.Put(s.LocalConfigPath(), LocalConfig(App("9001", null) + App("1172470", "-high")));

        var result = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        Assert.NotEmpty(result.AllItems);
        Assert.All(result.AllItems, i => Assert.Empty(i.Actions));
        var exclusive = result.Groups.Single(g => g.Title == "Protected Game").Items.Single(i => Mentions(i, "window-mode"));
        Assert.Contains("Easy Anti-Cheat", exclusive.DisabledReason);
        var removeHigh = result.Groups.Single(g => g.Title == "Apex Legends").Items.Single(i => i.Title.Contains("\"-high\""));
        Assert.NotNull(removeHigh.DisabledReason);
    }

    [Fact]
    public async Task With_the_real_guard_a_launch_option_row_is_either_allowed_exactly_or_shown_with_the_guards_reason()
    {
        using var s = new GamesSandbox("opt-realguard");
        s.SteamGame(s.SteamRoot, "1234560", "Some Game", "SomeGame", null, Updated, "SomeGame.exe", "UnityPlayer.dll");
        s.Put(s.LocalConfigPath(), LocalConfig(App("1234560", null)));
        var result = await new OptimizerScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var row = result.AllItems.Single(i => Mentions(i, "-window-mode exclusive"));
        if (row.DisabledReason is null)
            Assert.Equal(ActionKind.EditTextFile, Assert.Single(row.Actions).Kind);
        else
        {
            // Today Steam's folder is "never touched" for every feature: the row says so and can't be ticked.
            Assert.Empty(row.Actions);
            Assert.False(row.DefaultSelected);
        }
    }

    [Fact]
    public void Planner_never_targets_binaries_or_files_inside_the_game()
    {
        var install = @"C:\Games\Shooter";
        Assert.Null(ConfigEditPlanner.SetIni(@"C:\Games\Shooter\Shooter.exe", "", DateTime.UtcNow, "S", "K", "V", @"C:\Elsewhere", "d", Array.Empty<string>()));
        Assert.Null(ConfigEditPlanner.SetIni(@"C:\Games\Shooter\Engine.dll", "", DateTime.UtcNow, "S", "K", "V", @"C:\Elsewhere", "d", Array.Empty<string>()));
        Assert.Null(ConfigEditPlanner.SetIni(@"C:\Games\Shooter\Content\Paks\Shooter.pak", "", DateTime.UtcNow, "S", "K", "V", @"C:\Elsewhere", "d", Array.Empty<string>()));
        Assert.Null(ConfigEditPlanner.SetIni(@"C:\Games\Shooter\Saved\Engine.ini", "", DateTime.UtcNow, "S", "K", "V", install, "d", Array.Empty<string>()));
        Assert.NotNull(ConfigEditPlanner.SetIni(@"C:\Users\tester\AppData\Local\Shooter\Saved\Config\WindowsClient\Engine.ini", "", DateTime.UtcNow, "S", "K", "V", install, "d", Array.Empty<string>()));

        var steam = @"C:\Program Files (x86)\Steam";
        Assert.True(ConfigEditPlanner.IsSteamLocalConfig(@"C:\Program Files (x86)\Steam\userdata\123\config\localconfig.vdf", steam));
        Assert.False(ConfigEditPlanner.IsSteamLocalConfig(@"C:\Program Files (x86)\Steam\config\config.vdf", steam));
        Assert.False(ConfigEditPlanner.IsSteamLocalConfig(@"C:\Program Files (x86)\Steam\userdata\123\730\local\cfg\localconfig.vdf", steam));
        Assert.False(ConfigEditPlanner.IsSteamLocalConfig(@"C:\Program Files (x86)\Steam\steamapps\common\x\config\localconfig.vdf", steam));
    }

    [Fact]
    public async Task Every_planned_edit_in_a_generated_testbed_is_a_config_file_outside_game_folders()
    {
        using var s = new GamesSandbox("opt-testbed", generate: true);
        s.Put(s.LocalConfigPath(), LocalConfig(App("1234560", "-high -USEALLAVAILABLECORES")));
        var result = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        var edits = result.AllItems.SelectMany(i => i.Actions).ToList();
        Assert.NotEmpty(edits);
        foreach (var e in edits)
        {
            Assert.Equal(ActionKind.EditTextFile, e.Kind);
            Assert.True(ConfigEditPlanner.IsEditableConfig(e.SourcePath!));
            Assert.All(s.K.GameInstallDirs, d => Assert.False(PathUtil.IsUnder(e.SourcePath!, d)));
            Assert.DoesNotContain(".exe", e.SourcePath!, StringComparison.OrdinalIgnoreCase);
        }
        // "-USEALLAVAILABLECORES" is an Unreal option: on this Unity game it's not flagged as an Unreal placebo.
        Assert.DoesNotContain(result.AllItems, i => i.Title.Contains("USEALLAVAILABLECORES"));
        Assert.Contains(result.AllItems, i => i.Title.Contains("\"-high\"") && i.Actions.Count == 1);
    }

    /// <summary>GAM-2: Steam rewrites localconfig.vdf when it exits, so an edit planned while it runs could only be skipped.</summary>
    [Fact]
    public async Task While_Steam_is_open_launch_options_wait_for_a_scan_with_Steam_closed()
    {
        using var s = new GamesSandbox("opt-steam-open");
        s.SteamGame(s.SteamRoot, "1234560", "Some Game", "SomeGame", null, Updated, "SomeGame.exe", "UnityPlayer.dll");
        s.Put(s.LocalConfigPath(), LocalConfig(App("1234560", null)));

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam" };
        var result = await new OptimizerScanner().ScanAsync(s.Context(running: running, guard: new AllowAllGuard()), null, CancellationToken.None);
        var item = result.AllItems.Single(i => Mentions(i, "-window-mode exclusive"));
        Assert.Empty(item.Actions);
        Assert.Equal(RecommendationBuilder.SteamOpenReason, item.DisabledReason);
        Assert.Contains("then scan again", item.DisabledReason);
        Assert.Contains(result.Notes, n => n.Contains("Exit Steam") && n.Contains("scan again"));

        var closed = await new OptimizerScanner().ScanAsync(s.Context(guard: new AllowAllGuard()), null, CancellationToken.None);
        Assert.Single(closed.AllItems.Single(i => Mentions(i, "-window-mode exclusive")).Actions);
    }
}
