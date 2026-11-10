using PcTidy.Core;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Games;
using Xunit;

namespace PcTidy.Features.Boost.Tests;

public class DetectionTests
{
    private static ObservedProcess P(int pid, string name, string? path, bool inspected = true) => new(pid, name, path, null, inspected);

    private static BoostGame SteamGame(string dir) => new("steam:4000", "Space Game", "Steam", dir, "4000", Array.Empty<string>());

    [Fact]
    public void Exe_inside_the_steam_game_folder_matches()
    {
        var game = SteamGame(@"X:\Lib\steamapps\common\Space Game");
        var match = GameMatcher.Match(new[] { game }, new[] { P(10, "SpaceGame", @"X:\Lib\steamapps\common\Space Game\Bin\SpaceGame.exe") }, null);
        Assert.NotNull(match);
        Assert.Equal(game.Key, match!.Game.Key);
        Assert.Single(match.Processes);
    }

    [Fact]
    public void Exe_outside_the_folder_or_in_a_sibling_with_a_similar_name_doesnt_match()
    {
        var game = SteamGame(@"X:\Lib\steamapps\common\Space Game");
        var processes = new[]
        {
            P(10, "SpaceGame", @"X:\Lib\steamapps\common\Space Game 2\SpaceGame.exe"),
            P(11, "SpaceGame", @"X:\Elsewhere\SpaceGame.exe"),
        };
        Assert.Null(GameMatcher.Match(new[] { game }, processes, null));
    }

    [Theory]
    [InlineData("UnityCrashHandler64")]
    [InlineData("CrashReportClient")]
    [InlineData("EasyAntiCheat_EOS_Setup")]
    [InlineData("unins000")]
    public void Helpers_in_the_game_folder_are_not_the_game(string helper)
    {
        var game = SteamGame(@"X:\Lib\steamapps\common\Space Game");
        var p = P(10, helper, $@"X:\Lib\steamapps\common\Space Game\{helper}.exe");
        Assert.Null(GameMatcher.Match(new[] { game }, new[] { p }, null));
    }

    [Fact]
    public void Steam_running_app_id_matches_only_while_the_steam_client_runs()
    {
        var game = SteamGame(@"X:\Lib\steamapps\common\Space Game");
        Assert.Null(GameMatcher.Match(new[] { game }, Array.Empty<ObservedProcess>(), 4000));
        var match = GameMatcher.Match(new[] { game }, new[] { P(9, "steam", @"X:\Steam\steam.exe") }, 4000);
        Assert.NotNull(match);
        Assert.Null(GameMatcher.Match(new[] { game }, new[] { P(9, "steam", @"X:\Steam\steam.exe") }, 4001));
        Assert.Null(GameMatcher.Match(new[] { game }, new[] { P(9, "steam", @"X:\Steam\steam.exe") }, 0));
    }

    [Fact]
    public void Epic_process_name_matches_when_the_path_cant_be_read()
    {
        var game = new BoostGame("epic:fn", "Fort Game", "Epic Games", @"X:\Epic\FortGame", null, new[] { "FortGameClient" });
        Assert.NotNull(GameMatcher.Match(new[] { game }, new[] { P(10, "FortGameClient", null) }, null));
        // A same-named exe somewhere else is NOT that game.
        Assert.Null(GameMatcher.Match(new[] { game }, new[] { P(10, "FortGameClient", @"X:\Other\FortGameClient.exe") }, null));
        // Never by an uninspected (deny-listed) entry.
        Assert.Null(GameMatcher.Match(new[] { game }, new[] { P(10, "FortGameClient", null, inspected: false) }, null));
    }

    [Fact]
    public void No_match_when_no_game_is_enabled()
        => Assert.Null(GameMatcher.Match(Array.Empty<BoostGame>(), new[] { P(10, "SpaceGame", @"X:\Lib\steamapps\common\Space Game\SpaceGame.exe") }, 4000));

    [Theory]
    [InlineData("EasyAntiCheat")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("EAAntiCheat.GameServiceLauncher")]
    [InlineData("BEService_x64")]
    [InlineData("BattlEye")]
    [InlineData("vgc")]
    [InlineData("vgk")]
    [InlineData("vgtray")]
    [InlineData("FACEITClient")]
    [InlineData("ESEADriver2")]
    [InlineData("PnkBstrA")]
    [InlineData("Ricochet")]
    [InlineData("vgc.exe")]
    public void Anti_cheat_is_never_inspected_or_closed(string name)
    {
        Assert.True(BoostDenyList.IsAntiCheat(name));
        Assert.True(BoostDenyList.NeverInspect(name));
        Assert.True(BoostDenyList.NeverClose(name));
    }

    [Theory]
    [InlineData("lsass")]
    [InlineData("csrss")]
    [InlineData("explorer")]
    [InlineData("MsMpEng")]
    [InlineData("SecurityHealthSystray")]
    [InlineData("svchost")]
    [InlineData("PcTidy")]
    public void Windows_and_security_are_never_inspected(string name) => Assert.True(BoostDenyList.NeverInspect(name));

    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("EADesktop")]
    [InlineData("Battle.net")]
    [InlineData("GalaxyClient")]
    public void Launchers_are_never_closed(string name)
    {
        Assert.True(BoostDenyList.NeverClose(name));
        Assert.False(BoostDenyList.NeverInspect(name));
    }

    [Theory]
    [InlineData("Discord")]
    [InlineData("Spotify")]
    [InlineData("chrome")]
    public void Ordinary_apps_can_be_picked(string name) => Assert.False(BoostDenyList.NeverClose(name));

    [Fact]
    public void Poller_only_ever_asks_for_limited_query_rights_and_never_opens_denied_or_other_sessions()
    {
        var q = new FakeProcessQuery()
            .Add(10, "SpaceGame", @"X:\g\SpaceGame.exe")
            .Add(11, "EasyAntiCheat_EOS", @"X:\g\EasyAntiCheat\EasyAntiCheat_EOS.exe")
            .Add(12, "vgc", @"C:\Program Files\Riot Vanguard\vgc.exe")
            .Add(13, "lsass", @"C:\Windows\System32\lsass.exe")
            .Add(14, "Discord", @"X:\d\Discord.exe")
            .Add(15, "Spotify", @"X:\s\Spotify.exe", session: 2)
            .Add(4, "System", null);
        var poller = new ProcessPoller(q);

        var snapshot = poller.Snapshot();

        Assert.All(q.Opened, o => Assert.Equal(ProcessAccess.QueryLimitedInformation, o.Access));
        Assert.Equal(0x1000u, ProcessAccess.QueryLimitedInformation);
        Assert.Equal(new[] { 10, 14 }, q.Opened.Select(o => o.Pid).OrderBy(x => x));
        Assert.False(snapshot.Single(p => p.Pid == 11).Inspected);
        Assert.Null(snapshot.Single(p => p.Pid == 11).ImagePath);
        Assert.DoesNotContain(snapshot, p => p.Pid is 15 or 4);
    }

    [Fact]
    public void Poller_opens_each_process_once_and_forgets_gone_ones()
    {
        var q = new FakeProcessQuery().Add(10, "SpaceGame", @"X:\g\SpaceGame.exe").Add(14, "Discord", @"X:\d\Discord.exe");
        var poller = new ProcessPoller(q);
        poller.Snapshot();
        poller.Snapshot();
        Assert.Equal(2, q.Opened.Count);
        q.Remove("Discord");
        q.Add(14, "Other", @"X:\o\Other.exe"); // pid reused by another program
        var snap = poller.Snapshot();
        Assert.Equal(3, q.Opened.Count);
        Assert.Equal(@"X:\o\Other.exe", snap.Single(p => p.Pid == 14).ImagePath);
    }

    [Fact]
    public void Launcher_source_reads_fake_steam_and_epic_manifests()
    {
        using var t = new TestFolder();
        var loc = new SandboxKnownLocations(t.Root, SandboxProfile.Default);
        t.File(Path.Combine(Path.GetRelativePath(t.Root, loc.SteamRoot!), "steamapps", "appmanifest_4000.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"4000\"\n\t\"name\"\t\t\"Space Game\"\n\t\"installdir\"\t\t\"Space Game\"\n\t\"StateFlags\"\t\t\"4\"\n}\n");
        t.File(Path.Combine(Path.GetRelativePath(t.Root, loc.SteamRoot!), "steamapps", "appmanifest_228980.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"228980\"\n\t\"name\"\t\t\"Steamworks Common Redistributables\"\n\t\"installdir\"\t\t\"Steamworks Shared\"\n}\n");
        var epicDir = Path.Combine(loc.ProgramFiles, "Epic Games", "FortGame");
        t.File(Path.Combine(Path.GetRelativePath(t.Root, loc.EpicManifestsDir!), "ABC.item"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                AppName = "FortGame", DisplayName = "Fort Game", InstallLocation = epicDir,
                LaunchExecutable = "Binaries/Win64/FortGameLauncher.exe", ProcessNames = "FortGameClient,FortGameServer",
            }));

        var games = new LauncherGameSource(loc).Games();

        var steam = Assert.Single(games, g => g.Launcher == "Steam");
        Assert.Equal("steam:4000", steam.Key);
        Assert.Equal("4000", steam.SteamAppId);
        Assert.Equal(Path.Combine(loc.SteamRoot!, "steamapps", "common", "Space Game"), steam.InstallDir);
        var epic = Assert.Single(games, g => g.Launcher == "Epic Games");
        Assert.Equal("epic:fortgame", epic.Key);
        Assert.Equal(new[] { "FortGameClient", "FortGameServer", "FortGameLauncher" }, epic.ProcessNames);

        // End to end: a process inside the manifest's folder is recognised.
        var exe = Path.Combine(steam.InstallDir, "SpaceGame.exe");
        Assert.Equal("steam:4000", GameMatcher.Match(games, new[] { P(10, "SpaceGame", exe) }, null)?.Game.Key);
        Assert.Equal("epic:fortgame", GameMatcher.Match(games, new[] { P(11, "FortGameClient", Path.Combine(epicDir, "Binaries", "Win64", "FortGameClient.exe")) }, null)?.Game.Key);
    }

    [Fact]
    public void Launcher_source_is_empty_on_a_pc_without_launchers()
    {
        using var t = new TestFolder();
        Assert.Empty(new LauncherGameSource(new SandboxKnownLocations(t.Root, SandboxProfile.Minimal)).Games());
    }
}
