using System.Text;
using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Steam (root + optional D:\SteamLibrary, real-format libraryfolders.vdf / appmanifest .acf) and Epic
/// (manifest .item + install folder). Games and saves are canaries; only Steam's web cache, dumps and logs are targets.</summary>
internal static class GamesArea
{
    public const string SomeGameId = "1234560";
    public const string OtherGameId = "7654320";

    public static void Build(Ctx c)
    {
        if (c.P.Steam) Steam(c);
        if (c.P.Epic) Epic(c);
    }

    private static void Steam(Ctx c)
    {
        var root = c.K.SteamRoot!;
        var apps = Path.Combine(root, "steamapps");
        c.Canary(Path.Combine(root, "steam.exe"), c.F.Exe(), 10, "Steam itself");
        c.Canary(Path.Combine(root, "config", "config.vdf"), Fake.Text("\"InstallConfigStore\"\n{\n}\n"), 1, "Steam settings");
        c.Canary(Path.Combine(root, "userdata", "12345678", SomeGameId, "remote", "save1.sav"), c.F.Blob(2_000, 20_000), 3, "Game save (Steam Cloud)");
        c.NeverTree(Path.Combine(root, "userdata"), "Steam user data and saves");

        var game = Path.Combine(apps, "common", "SomeGame");
        c.Put(Path.Combine(game, "SomeGame.exe"), c.F.Exe(), 40, "game");
        c.Put(Path.Combine(game, "UnityPlayer.dll"), c.F.Dll(), 40, "game");
        c.Put(Path.Combine(game, "SomeGame_Data", "level1.assets"), c.F.Blob(50_000, 300_000), 40, "game");
        c.Put(Path.Combine(game, "SomeGame_Data", "Logs", "old.log"), c.F.Blob(), 200, "game");   // looks like junk; inside a game: never
        c.NeverTree(game, "Installed Steam game");
        c.Canary(Path.Combine(apps, $"appmanifest_{SomeGameId}.acf"), Acf(SomeGameId, "Some Game", "SomeGame"), 40, "Steam app manifest");
        c.Canary(Path.Combine(apps, "libraryfolders.vdf"), LibraryFolders(c), 5, "Steam library list");
        c.Put(Path.Combine(apps, "shadercache", SomeGameId, "fozpipelinesv6", "steamapprun_pipeline_cache.foz"), c.F.Blob(10_000, 80_000), 20, "game,steam-shadercache");

        // The only carve-outs: appcache\httpcache, dumps, logs (+ %LOCALAPPDATA%\Steam\htmlcache).
        for (int i = 0; i < 4; i++)
            c.Put(Path.Combine(root, "appcache", "httpcache", c.F.Hex(2), c.F.Hex(32)), c.F.Blob(2_000, 50_000), 5 + i * 15, "cleanup,steam-cache");
        c.Canary(Path.Combine(root, "appcache", "appinfo.vdf"), c.F.Blob(20_000, 80_000), 1, "Steam app info (appcache itself is not a target)");
        c.Put(Path.Combine(root, "dumps", "crash_20250412101010_1.dmp"), c.F.Dump(), 200, "cleanup,steam-cache,old");
        c.Put(Path.Combine(root, "dumps", "crash_20261108101010_1.dmp"), c.F.Dump(), 2, "cleanup,steam-cache,fresh");
        c.Put(Path.Combine(root, "logs", "content_log.txt"), c.F.Lines("[2025-01-01] content"), 0.1, "cleanup,steam-cache,fresh");
        c.Put(Path.Combine(root, "logs", "bootstrap_log.old.txt"), c.F.Lines("[2025-01-01] bootstrap"), 90, "cleanup,steam-cache,old");
        var html = Path.Combine(c.K.LocalAppData, "Steam", "htmlcache");
        c.Put(Path.Combine(html, "Cache", "Cache_Data", "f_000001"), c.F.Blob(2_000, 50_000), 12, "cleanup,steam-cache");
        c.Put(Path.Combine(html, "Cookies"), c.F.Sqlite(), 1, "cleanup,steam-cache");

        if (!c.P.SecondDrive) return;
        var lib = Path.Combine(c.DriveD, "SteamLibrary");
        c.Canary(Path.Combine(lib, "libraryfolder.vdf"), Fake.Text("\"libraryfolder\"\n{\n\t\"contentid\"\t\t\"987654321\"\n\t\"label\"\t\t\"\"\n}\n"), 100, "Steam library marker");
        var other = Path.Combine(lib, "steamapps", "common", "AnotherGame");
        c.Put(Path.Combine(other, "AnotherGame.exe"), c.F.Exe(), 70, "game");
        c.Put(Path.Combine(other, "Content", "Paks", "AnotherGame-WindowsNoEditor.pak"), c.F.Blob(100_000, 400_000), 70, "game");
        c.Put(Path.Combine(other, "Saved", "Crashes", "old-crash.log"), c.F.Blob(), 300, "game");
        c.Canary(Path.Combine(lib, "steamapps", $"appmanifest_{OtherGameId}.acf"), Acf(OtherGameId, "Another Game", "AnotherGame"), 70, "Steam app manifest");
        c.NeverTree(Path.Combine(lib, "steamapps"), "Steam library on D:");
    }

    private static byte[] Acf(string id, string name, string dir) => Fake.Text(
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"universe\"\t\t\"1\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"4\"\n" +
        $"\t\"installdir\"\t\t\"{dir}\"\n\t\"LastUpdated\"\t\t\"1735732800\"\n\t\"SizeOnDisk\"\t\t\"524288\"\n}}\n");

    private static byte[] LibraryFolders(Ctx c)
    {
        static string Esc(string p) => p.Replace("\\", "\\\\");
        var sb = new StringBuilder("\"libraryfolders\"\n{\n");
        void Lib(int index, string path, string appId)
            => sb.Append($"\t\"{index}\"\n\t{{\n\t\t\"path\"\t\t\"{Esc(path)}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"contentid\"\t\t\"{100 + index}\"\n" +
                         $"\t\t\"totalsize\"\t\t\"0\"\n\t\t\"apps\"\n\t\t{{\n\t\t\t\"{appId}\"\t\t\"524288\"\n\t\t}}\n\t}}\n");
        Lib(0, c.K.SteamRoot!, SomeGameId);
        if (c.P.SecondDrive) Lib(1, Path.Combine(c.DriveD, "SteamLibrary"), OtherGameId);
        return Fake.Text(sb.Append("}\n").ToString());
    }

    private static void Epic(Ctx c)
    {
        var install = Path.Combine(c.K.ProgramFiles, "Epic Games", "FakeGame");
        c.Put(Path.Combine(install, "FakeGame.exe"), c.F.Exe(), 50, "game");
        c.Put(Path.Combine(install, "FakeGame", "Content", "Paks", "FakeGame.pak"), c.F.Blob(100_000, 400_000), 50, "game");
        c.Put(Path.Combine(install, ".egstore", "FakeGame.manifest"), c.F.Blob(), 50, "game");
        c.NeverTree(Path.Combine(c.K.ProgramFiles, "Epic Games"), "Epic game install");
        var json = "{\n  \"FormatVersion\": 0,\n  \"DisplayName\": \"Fake Game\",\n  \"AppName\": \"FakeGameApp\",\n" +
                   $"  \"InstallLocation\": \"{install.Replace("\\", "\\\\")}\",\n  \"LaunchExecutable\": \"FakeGame.exe\",\n" +
                   "  \"CatalogItemId\": \"0123456789abcdef0123456789abcdef\",\n  \"InstallSize\": 524288\n}\n";
        c.Canary(Path.Combine(c.K.EpicManifestsDir!, "0A1B2C3D4E5F60718293A4B5C6D7E8F9.item"), Fake.Text(json), 50, "Epic install manifest");
    }
}
