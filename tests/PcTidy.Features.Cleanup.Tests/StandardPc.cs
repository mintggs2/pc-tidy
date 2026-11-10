using static PcTidy.Features.Cleanup.Tests.FakePc;

namespace PcTidy.Features.Cleanup.Tests;

/// <summary>
/// A fake PC with something in every §7 area, plus canary files that must never appear in any action.
/// Sizes are chosen so expected totals can be checked exactly.
/// </summary>
internal sealed class StandardPc
{
    public readonly List<string> Canaries = new();
    /// <summary>Opt-in privacy files: allowed only in the Privacy extras group, never ticked.</summary>
    public readonly List<string> PrivacyFiles = new();

    public string ChromeDefault = "", TempOldFile = "", TempOldFolder = "", TempNewFile = "", TempBusyOld = "", TempBusyNew = "", TempEmptyOld = "";
    public string CrashAppOld = "", CrashAppNew = "", MinidumpOld = "", MemoryDmp = "";
    public string WerOld = "", WerNew = "", CbsOld = "", CbsNewPersist = "", DismOld = "";
    public string UpdateOld = "", UpdateRecentFolder = "", UpdateMixedOld = "";
    public string SetupMsi = "", NewMsi = "", SetupExe = "", PlainExe = "", Zip = "", DeepSetup = "", CloudMsi = "", VersionInfoInstaller = "";

    public const long ChromeDefaultCacheBytes = 4096 + 1024 + 512 + 256;
    public const long TempQuickBytes = 1000 + 300 + 200; // old.tmp + OldFolder (a + b)
    public const long SpotifyBytes = 4000 + 1000;
    public const long RecycleBytes = 1234 + 10;

    public static StandardPc Build(FakePc pc)
    {
        var s = new StandardPc();
        var k = pc.K;
        string Canary(string path, long size = 10) { s.Canaries.Add(pc.File(path, size, Old)); return path; }

        // #1 user temp
        var t = k.UserTemp;
        s.TempOldFile = pc.File(Path.Combine(t, "old.tmp"), 1000, Old);
        s.TempNewFile = pc.File(Path.Combine(t, "new.tmp"), 500, Recent);
        s.TempOldFolder = Path.Combine(t, "OldFolder");
        pc.File(Path.Combine(s.TempOldFolder, "a.txt"), 300, Old);
        pc.File(Path.Combine(s.TempOldFolder, "sub", "b.txt"), 200, Old);
        s.TempBusyOld = pc.File(Path.Combine(t, "Busy", "old.log"), 700, Old);
        s.TempBusyNew = pc.File(Path.Combine(t, "Busy", "live.log"), 100, Recent);
        s.TempEmptyOld = pc.Folder(Path.Combine(t, "EmptyOld"));
        Canary(Path.Combine(t, "Placeholder", "cloud.bin"), 50);
        System.IO.File.SetAttributes(s.Canaries[^1], FileAttributes.Offline);

        // #2 Windows temp
        pc.File(Path.Combine(k.WindowsTemp, "wold.tmp"), 2000, Old);
        pc.File(Path.Combine(k.WindowsTemp, "wnew.tmp"), 10, Recent);

        // #3 / #6 Chrome + Edge (Brave not installed)
        var chrome = Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data");
        s.ChromeDefault = Path.Combine(chrome, "Default");
        var d = s.ChromeDefault;
        pc.File(Path.Combine(d, "Cache", "Cache_Data", "f_000001"), 4096, Recent);
        pc.File(Path.Combine(d, "Cache", "Cache_Data", "f_000002"), 1024, Old);
        pc.File(Path.Combine(d, "Code Cache", "js", "index"), 512, Recent);
        pc.File(Path.Combine(d, "GPUCache", "data_0"), 256, Recent);
        pc.File(Path.Combine(d, "Service Worker", "CacheStorage", "abc", "entry"), 2048, Old);
        pc.File(Path.Combine(chrome, "Profile 1", "Cache", "Cache_Data", "f_1"), 100, Recent);
        pc.File(Path.Combine(chrome, "ShaderCache", "data_1"), 128, Recent);
        pc.File(Path.Combine(chrome, "GrShaderCache", "data_2"), 64, Recent);
        Canary(Path.Combine(chrome, "Local State"));
        Canary(Path.Combine(chrome, "Guest Profile", "Cache", "Cache_Data", "g"));
        foreach (var name in new[] { "Login Data", "Web Data", "Bookmarks", "Preferences", "Secure Preferences" })
            Canary(Path.Combine(d, name));
        Canary(Path.Combine(d, "Local Storage", "leveldb", "000003.log"));
        Canary(Path.Combine(d, "IndexedDB", "https_example.com_0.indexeddb.leveldb", "LOG"));
        Canary(Path.Combine(d, "Session Storage", "CURRENT"));
        Canary(Path.Combine(d, "Extensions", "abc", "1.0", "manifest.json"));
        s.PrivacyFiles.Add(pc.File(Path.Combine(d, "History"), 900, Old));
        s.PrivacyFiles.Add(pc.File(Path.Combine(d, "Network", "Cookies"), 800, Old));
        pc.File(Path.Combine(k.LocalAppData, "Microsoft", "Edge", "User Data", "Default", "Cache", "Cache_Data", "e1"), 300, Recent);

        // #4 Firefox
        var ff = Path.Combine(k.LocalAppData, "Mozilla", "Firefox", "Profiles", "abcd.default-release");
        pc.File(Path.Combine(ff, "cache2", "entries", "X"), 1500, Recent);
        pc.File(Path.Combine(ff, "startupCache", "s"), 100, Recent);
        pc.File(Path.Combine(ff, "thumbnails", "t"), 50, Recent);
        Canary(Path.Combine(ff, "cookies.sqlite"));
        Canary(Path.Combine(ff, "places.sqlite"));

        // #5 INetCache
        pc.File(Path.Combine(k.LocalAppData, "Microsoft", "Windows", "INetCache", "IE", "x.dat"), 400, Recent);

        // #7 crash dumps
        s.CrashAppOld = pc.File(Path.Combine(k.LocalAppData, "CrashDumps", "app_old.dmp"), 5000, 30);
        s.CrashAppNew = pc.File(Path.Combine(k.LocalAppData, "CrashDumps", "app_new.dmp"), 3000, 3);
        s.MinidumpOld = pc.File(Path.Combine(k.WindowsDir, "Minidump", "old.dmp"), 6000, 30);
        pc.File(Path.Combine(k.WindowsDir, "LiveKernelReports", "WATCHDOG", "wd.dmp"), 700, Old);
        s.MemoryDmp = pc.File(Path.Combine(k.WindowsDir, "MEMORY.DMP"), 10000, 5);

        // #8 error reports
        s.WerOld = pc.File(Path.Combine(k.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_x", "Report.wer"), 100, 40);
        s.WerNew = pc.File(Path.Combine(k.LocalAppData, "Microsoft", "Windows", "WER", "ReportQueue", "AppCrash_y", "Report.wer"), 100, 10);
        pc.File(Path.Combine(k.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive", "k", "Report.wer"), 200, 45);

        // #9 logs
        Canary(Path.Combine(k.WindowsDir, "Logs", "CBS", "CBS.log"), 999);
        Canary(Path.Combine(k.WindowsDir, "Logs", "DISM", "dism.log"), 50);
        s.CbsOld = pc.File(Path.Combine(k.WindowsDir, "Logs", "CBS", "CbsPersist_20240101.log"), 1200, 40);
        s.CbsNewPersist = pc.File(Path.Combine(k.WindowsDir, "Logs", "CBS", "CbsPersist_20990101.log"), 100, 10);
        s.DismOld = pc.File(Path.Combine(k.WindowsDir, "Logs", "DISM", "old.log"), 300, 40);

        // #10 update downloads
        var dl = Path.Combine(k.WindowsDir, "SoftwareDistribution", "Download");
        s.UpdateOld = Path.Combine(dl, "aaa");
        pc.File(Path.Combine(s.UpdateOld, "upd.cab"), 8000, 20);
        s.UpdateRecentFolder = Path.Combine(dl, "bbb");
        pc.File(Path.Combine(s.UpdateRecentFolder, "x.cab"), 100, 2);
        s.UpdateMixedOld = pc.File(Path.Combine(dl, "ccc", "old.cab"), 400, 20);
        pc.File(Path.Combine(dl, "ccc", "new.cab"), 10, Recent);

        // #11 Delivery Optimization, #12 shaders, #13 thumbnails
        pc.File(Path.Combine(k.WindowsDir, "SoftwareDistribution", "DeliveryOptimization", "Cache", "piece"), 777, Old);
        pc.File(Path.Combine(k.LocalAppData, "NVIDIA", "DXCache", "s1"), 1000, Recent);
        pc.File(Path.Combine(k.LocalAppData, "D3DSCache", "x", "y"), 300, Recent);
        var explorer = Path.Combine(k.LocalAppData, "Microsoft", "Windows", "Explorer");
        pc.File(Path.Combine(explorer, "thumbcache_256.db"), 640, Recent);
        pc.File(Path.Combine(explorer, "iconcache_32.db"), 32, Recent);
        pc.File(Path.Combine(explorer, "other.db"), 10, Recent);

        // #14 Recycle Bin (sandbox: the folder; real PC: the Recycle Bin API)
        pc.File(Path.Combine(k.SystemDrive, "$Recycle.Bin", "S-1-5-21-0", "$RABC.txt"), 1234, Old);
        pc.File(Path.Combine(k.SystemDrive, "$Recycle.Bin", "S-1-5-21-0", "$IABC.txt"), 10, Old);

        // #15 Discord
        var discord = Path.Combine(k.RoamingAppData, "discord");
        pc.File(Path.Combine(discord, "Cache", "Cache_Data", "d1"), 2500, Recent);
        pc.File(Path.Combine(discord, "Code Cache", "c"), 100, Recent);
        Canary(Path.Combine(discord, "Local Storage", "leveldb", "l.log"));
        Canary(Path.Combine(discord, "IndexedDB", "i"));
        Canary(Path.Combine(discord, "Network", "Cookies"));

        // #16 Steam
        pc.File(Path.Combine(k.LocalAppData, "Steam", "htmlcache", "h1"), 600, Recent);
        if (k.SteamRoot is { } steam)
        {
            pc.File(Path.Combine(steam, "appcache", "httpcache", "x"), 300, Recent);
            pc.File(Path.Combine(steam, "dumps", "crash.dmp"), 200, Recent);
            pc.File(Path.Combine(steam, "logs", "old.txt"), 100, 40);
            pc.File(Path.Combine(steam, "logs", "new.txt"), 50, Recent);
            Canary(Path.Combine(steam, "steamapps", "common", "Game", "game.exe"), 5000);
            Canary(Path.Combine(steam, "userdata", "123", "save.dat"));
            Canary(Path.Combine(steam, "config", "config.vdf"));
            Canary(Path.Combine(steam, "appcache", "appinfo.vdf"));
        }

        // #17 Spotify
        pc.File(Path.Combine(k.LocalAppData, "Spotify", "Data", "sp"), 4000, Recent);
        pc.File(Path.Combine(k.LocalAppData, "Spotify", "Storage", "st"), 1000, Recent);

        // #18 Telegram
        var tdata = Path.Combine(k.RoamingAppData, "Telegram Desktop", "tdata");
        pc.File(Path.Combine(tdata, "user_data", "cache", "0", "m"), 1500, Recent);
        pc.File(Path.Combine(tdata, "user_data#2", "cache", "x"), 99, Recent);
        Canary(Path.Combine(tdata, "key_datas"));
        Canary(Path.Combine(tdata, "D877F783D5D3EF8C", "maps"));

        // #19 installers
        var downloads = k.Downloads;
        s.SetupMsi = pc.File(Path.Combine(downloads, "Tool.msi"), 3000, Old);
        s.NewMsi = pc.File(Path.Combine(downloads, "New.msi"), 100, 5);
        s.SetupExe = pc.File(Path.Combine(downloads, "MyAppSetup.exe"), 2000, Old);
        s.PlainExe = pc.File(Path.Combine(downloads, "notes.exe"), 100, Old);
        s.Zip = pc.File(Path.Combine(downloads, "photos.zip"), 300, Old);
        s.DeepSetup = pc.File(Path.Combine(downloads, "Sub", "deep_setup.exe"), 400, Old);
        s.CloudMsi = pc.File(Path.Combine(downloads, "Cloud.msi"), 50, Old, FileAttributes.Offline);
        Canary(Path.Combine(downloads, "MyRepo", "installer.msi"));
        pc.Folder(Path.Combine(downloads, "MyRepo", ".git"));
        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        if (System.IO.File.Exists(msiexec))
        {
            // Read-only copy of a real signed exe: its version info says "installer" though its name doesn't.
            s.VersionInfoInstaller = Path.Combine(downloads, "tool.exe");
            System.IO.File.Copy(msiexec, s.VersionInfoInstaller);
            System.IO.File.SetCreationTimeUtc(s.VersionInfoInstaller, DateTime.UtcNow.AddDays(-Old));
            System.IO.File.SetLastWriteTimeUtc(s.VersionInfoInstaller, DateTime.UtcNow.AddDays(-Old));
        }

        // #20 / #21
        pc.File(Path.Combine(k.SystemDrive, "Windows.old", "Windows", "file"), 5555, Old);
        pc.File(Path.Combine(k.WindowsDir, "WinSxS", "x"), 1, Old);

        pc.Seal();
        return s;
    }
}
