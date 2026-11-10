using PcTidy.Safety.Detection;
using Xunit;

namespace PcTidy.Safety.Tests;

public sealed class VdfTests
{
    [Fact]
    public void Reads_new_libraryfolders_format()
    {
        var root = Vdf.Parse("""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"X:\\Games\\Steam"
                    "apps" { "10" "123" }
                }
                "1" { "path" "Y:\\SteamLibrary" }
            }
            """);
        var lib = root["libraryfolders"]!;
        Assert.Equal(@"X:\Games\Steam", lib["0"]!.GetString("path"));
        Assert.Equal(@"Y:\SteamLibrary", lib["1"]!.GetString("path"));
        Assert.Equal(123, lib["0"]!["apps"]!.GetLong("10"));
    }

    [Fact]
    public void Tolerates_old_format_comments_conditionals_and_damage()
    {
        var root = Vdf.Parse("""
            // comment
            "LibraryFolders"
            {
                "TimeNextStatsReport" "1"
                "1"   "Z:\\Old Lib"   [$WIN32]
                bare  value
                "broken" { "x" "unterminated
            """);
        var lib = root["libraryfolders"]!;
        Assert.Equal(@"Z:\Old Lib", lib.GetString("1"));
        Assert.Equal("value", lib.GetString("bare"));
        Assert.NotNull(lib["broken"]);
        Assert.DoesNotContain(Vdf.Parse("}}}{").Children.SelectMany(c => c.Value.Children), c => c.Value.Value is not null);
        Assert.Empty(Vdf.Parse("").Children);
    }

    [Fact]
    public void Unknown_escapes_keep_their_backslash()
        => Assert.Equal(@"C:\Path", Vdf.Parse("\"k\" \"C:\\Path\"").GetString("k"));
}

public sealed class LauncherFileTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Steam_libraries_and_app_manifests_are_read()
    {
        var steam = _s.K.SteamRoot!;
        var second = _s.K.SteamLibraries[1];
        _s.File(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{second.Replace("\\", "\\\\")}}" }
                "2" { "path" "{{Path.Combine(_s.Root, "gone").Replace("\\", "\\\\")}}" }
            }
            """);
        _s.File(Path.Combine(second, "steamapps", "appmanifest_570.acf"), """
            "AppState"
            {
                "appid" "570"
                "name" "Test Game"
                "StateFlags" "4"
                "installdir" "Test Game"
                "LastPlayed" "1700000000"
                "SizeOnDisk" "123456789"
            }
            """);
        _s.File(Path.Combine(second, "steamapps", "appmanifest_666.acf"), "\"AppState\" { \"installdir\" \"..\\\\..\\\\Windows\" }");
        _s.File(Path.Combine(second, "steamapps", "appmanifest_1.acf"), "garbage {{{");

        var libs = SteamConfig.ReadLibraries(steam);
        Assert.Equal(2, libs.Count); // the missing library is dropped

        var games = new GameLibraryInfo(_s.K);
        var app = Assert.Single(games.SteamApps);
        Assert.Equal("570", app.AppId);
        Assert.Equal("Test Game", app.Name);
        Assert.Equal(123456789, app.SizeOnDisk);
        Assert.True(app.IsFullyInstalled);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), app.LastPlayedUtc);
        Assert.Equal(Path.Combine(second, "steamapps", "common", "Test Game"), app.InstallPath);
    }

    [Fact]
    public void Epic_manifests_are_read_and_damaged_ones_skipped()
    {
        var dir = _s.Dir(_s.K.EpicManifestsDir!);
        var install = Path.Combine(_s.K.ProgramFiles, "Epic Games", "Game");
        _s.File(Path.Combine(dir, "A.item"), $$"""
            { "AppName": "Game", "DisplayName": "Great Game", "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
              "InstallSize": 42, "LaunchExecutable": "Game.exe", "ProcessNames": "Game.exe,GameHelper.exe", }
            """);
        _s.File(Path.Combine(dir, "B.item"), "{ not json");
        _s.File(Path.Combine(dir, "C.item"), "{ \"AppName\": \"NoLocation\" }");

        var app = Assert.Single(new GameLibraryInfo(_s.K).EpicApps);
        Assert.Equal("Great Game", app.DisplayName);
        Assert.Equal(install, app.InstallLocation, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(42, app.InstallSize);
        Assert.Equal(2, app.ProcessNames.Count);
    }

    [Fact]
    public void Dropbox_and_syncthing_configs_are_parsed()
    {
        var dropbox = CloudSyncRoots.ParseDropboxInfo("""{"personal": {"path": "X:\\Dropbox", "host": 1}, "business": {"path": "X:\\Dropbox (Work)"}}""").ToList();
        Assert.Equal(new[] { @"X:\Dropbox", @"X:\Dropbox (Work)" }, dropbox);
        var syncthing = CloudSyncRoots.ParseSyncthingConfig("""
            <configuration version="37">
              <folder id="a" label="Docs" path="X:\Sync\Docs" type="sendreceive"></folder>
              <folder id="b" path="~\Sync\Photos"></folder>
              <device id="x"></device>
            </configuration>
            """).ToList();
        Assert.Equal(new[] { @"X:\Sync\Docs", @"~\Sync\Photos" }, syncthing);
    }
}

public sealed class ProjectDetectorTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Finds_the_nearest_project_root_and_caches_until_cleared()
    {
        var repo = _s.Dir(Path.Combine(_s.K.Downloads, "Repo"));
        var deep = _s.Dir(Path.Combine(repo, "src", "lib", "deep"));
        var detector = new ProjectDetector(_s.K);
        Assert.Null(detector.FindProjectRoot(deep));

        _s.Dir(Path.Combine(repo, ".git"));
        Assert.Null(detector.FindProjectRoot(deep)); // cached answer
        detector.ClearCache();
        Assert.Equal(repo, detector.FindProjectRoot(deep, out var why), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".git", why);
        Assert.True(detector.IsProjectOrAppFolder(repo));
        Assert.False(detector.IsProjectOrAppFolder(deep));
    }

    [Fact]
    public void Containers_are_never_projects()
    {
        _s.Dir(Path.Combine(_s.K.Documents, ".git"));
        var detector = new ProjectDetector(_s.K);
        Assert.False(detector.IsProjectOrAppFolder(_s.K.Documents));
        Assert.Null(detector.FindProjectRoot(_s.Dir(Path.Combine(_s.K.Documents, "Letters"))));
    }
}
