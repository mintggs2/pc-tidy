using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Per-user junk and app caches: user Temp, crash dumps, WER, GPU shader caches (by simulated GPU vendor),
/// thumbnail cache, Discord, Telegram, Spotify.</summary>
internal static class AppsArea
{
    public static void Build(Ctx c)
    {
        UserTemp(c);
        CrashAndErrorReports(c);
        ShaderCaches(c);
        Explorer(c);
        Discord(c);
        Telegram(c);
        Spotify(c);
    }

    private static void UserTemp(Ctx c)
    {
        var t = c.K.UserTemp;
        string[] oldNames = { "~DF1A2B3C4D.tmp", "chrome_installer.log", "msedge_installer.log", "wct8F21.tmp", "dd_vcredist_amd64_20250301.log", "Excel8.0\\fake.exd" };
        for (int i = 0; i < oldNames.Length; i++) c.Put(Path.Combine(t, oldNames[i]), c.F.Blob(), 5 + i * 30, "cleanup,user-temp,old");
        var guidDir = Path.Combine(t, "{" + c.F.Guid().ToString().ToUpperInvariant() + "}");
        c.Put(Path.Combine(guidDir, "setup.exe"), c.F.Exe(), 45, "cleanup,user-temp,old");
        c.Put(Path.Combine(guidDir, "setup.ini"), c.F.Lines("[Setup]"), 45, "cleanup,user-temp,old");
        c.Put(Path.Combine(t, "zero-byte.tmp"), Array.Empty<byte>(), 9, "cleanup,user-temp,old,zero-byte");
        c.EmptyDir(Path.Combine(t, "old-empty-folder"), 30, "cleanup,user-temp,old,empty-folder");
        c.EmptyDir(Path.Combine(t, "fresh-empty-folder"), 0.2, "cleanup,user-temp,fresh,empty-folder");
        c.Put(Path.Combine(t, "fresh-download.tmp"), c.F.Blob(), 0.05, "cleanup,user-temp,fresh");
        c.Put(Path.Combine(t, "yesterday.tmp"), c.F.Blob(), 1, "cleanup,user-temp,fresh");
        c.Put(Path.Combine(t, "in-use.tmp"), c.F.Blob(), 10, "cleanup,user-temp,old,lock-candidate");
    }

    private static void CrashAndErrorReports(Ctx c)
    {
        var dumps = Path.Combine(c.K.LocalAppData, "CrashDumps");
        c.Put(Path.Combine(dumps, "FakeApp.exe.10432.dmp"), c.F.Dump(), 60, "cleanup,crash-dump,old");
        c.Put(Path.Combine(dumps, "explorer.exe.7720.dmp"), c.F.Dump(), 20, "cleanup,crash-dump,old");
        c.Put(Path.Combine(dumps, "FakeApp.exe.22011.dmp"), c.F.Dump(), 2, "cleanup,crash-dump,fresh");
        var wer = Path.Combine(c.K.LocalAppData, "Microsoft", "Windows", "WER");
        c.Put(Path.Combine(wer, "ReportArchive", "AppCrash_FakeUserApp.exe_aa11bb22cc33_00112233_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Report.wer"),
            c.F.Lines("Version=1 EventType=APPCRASH"), 90, "cleanup,error-reports,old");
        c.Put(Path.Combine(wer, "ReportArchive", "AppHang_FakeUserApp.exe_dd44ee55ff66_44556677_ffffffff-0000-1111-2222-333333333333", "Report.wer"),
            c.F.Lines("Version=1 EventType=AppHangB1"), 4, "cleanup,error-reports,fresh");
        c.Put(Path.Combine(wer, "Temp", "WER5678.tmp.csv"), c.F.Blob(), 35, "cleanup,error-reports,old");
    }

    private static void ShaderCaches(Ctx c)
    {
        var la = c.K.LocalAppData;
        switch (c.Hw.GpuVendor)
        {
            case GpuVendor.NVIDIA:
                for (int i = 0; i < 4; i++)
                    c.Put(Path.Combine(la, "NVIDIA", "DXCache", $"{c.F.Hex(16)}_{c.F.Hex(8)}.nvph"), c.F.Blob(10_000, 90_000), 3 + i * 20, "cleanup,shader-cache");
                c.Put(Path.Combine(la, "NVIDIA", "GLCache", c.F.Hex(32), c.F.Hex(4), $"{c.F.Hex(16)}.toc"), c.F.Blob(), 15, "cleanup,shader-cache");
                c.Put(Path.Combine(la, "NVIDIA", "GLCache", c.F.Hex(32), c.F.Hex(4), $"{c.F.Hex(16)}.bin"), c.F.Blob(10_000, 60_000), 15, "cleanup,shader-cache");
                c.Put(Path.Combine(c.K.RoamingAppData, "NVIDIA", "ComputeCache", "0", "0", "index"), c.F.Blob(1_000, 8_000), 50, "cleanup,shader-cache");
                c.Put(Path.Combine(c.K.ProgramData, "NVIDIA Corporation", "NV_Cache", "fake.nvph"), c.F.Blob(10_000, 50_000), 40, "cleanup,shader-cache");
                c.Canary(Path.Combine(c.K.ProgramData, "NVIDIA Corporation", "Drs", "nvdrsdb0.bin"), c.F.Blob(), 30, "NVIDIA driver profile settings (next to NV_Cache, not a cache)");
                break;
            case GpuVendor.AMD:
                foreach (var folder in new[] { "DxCache", "DxcCache", "VkCache" })
                    c.Put(Path.Combine(la, "AMD", folder, $"{c.F.Hex(16)}.bin"), c.F.Blob(10_000, 90_000), 12, "cleanup,shader-cache");
                c.Canary(Path.Combine(la, "AMD", "CN", "gmdb.blb"), c.F.Blob(), 30, "AMD Software settings (next to caches, not a cache)");
                break;
            case GpuVendor.Intel:
                c.Put(Path.Combine(la, "Intel", "ShaderCache", $"{c.F.Hex(16)}.bin"), c.F.Blob(10_000, 60_000), 12, "cleanup,shader-cache");
                break;
        }
        var d3d = Path.Combine(la, "D3DSCache", c.F.Hex(16));
        c.Put(Path.Combine(d3d, "fakegame.idx"), c.F.Blob(1_000, 8_000), 8, "cleanup,shader-cache");
        c.Put(Path.Combine(d3d, "fakegame.val"), c.F.Blob(10_000, 90_000), 8, "cleanup,shader-cache");
    }

    private static void Explorer(Ctx c)
    {
        var ex = Path.Combine(c.K.LocalAppData, "Microsoft", "Windows", "Explorer");
        foreach (var n in new[] { "thumbcache_32.db", "thumbcache_96.db", "thumbcache_256.db", "thumbcache_idx.db", "iconcache_32.db" })
            c.Put(Path.Combine(ex, n), c.F.Bin("CMMM", c.F.Next(20_000, 200_000)), 0.3, "cleanup,thumbnail-cache,windows-tool-only");
    }

    private static void Discord(Ctx c)
    {
        var d = Path.Combine(c.K.RoamingAppData, "discord");
        for (int i = 0; i < 5; i++) c.Put(Path.Combine(d, "Cache", "Cache_Data", $"f_{i + 1:x6}"), c.F.Blob(2_000, 120_000), 1 + i * 6, "cleanup,discord");
        c.Put(Path.Combine(d, "Cache", "Cache_Data", "index"), c.F.Blob(256, 1_000), 0.2, "cleanup,discord");
        c.Put(Path.Combine(d, "Code Cache", "js", $"{c.F.Hex(16)}_0"), c.F.Blob(1_000, 30_000), 9, "cleanup,discord");
        c.Put(Path.Combine(d, "GPUCache", "data_0"), c.F.Blob(8_000, 30_000), 9, "cleanup,discord");
        c.Canary(Path.Combine(d, "Local Storage", "leveldb", "000005.ldb"), c.F.LevelDb(), 0.5, "Discord login token");
        c.Canary(Path.Combine(d, "Local Storage", "leveldb", "LOG"), c.F.Lines("leveldb log"), 0.5, "Discord login token");
        c.Canary(Path.Combine(d, "IndexedDB", "https_discord.com_0.indexeddb.leveldb", "000003.log"), c.F.LevelDb(), 0.5, "Discord site data");
        c.Canary(Path.Combine(d, "settings.json"), Fake.Text("{\"BACKGROUND_COLOR\":\"#202225\"}"), 20, "Discord settings");
        c.Put(Path.Combine(c.K.LocalAppData, "Discord", "app-1.0.9170", "Discord.exe"), c.F.Exe(), 30, "app");
        c.NeverTree(Path.Combine(c.K.LocalAppData, "Discord"), "Discord app install");
    }

    private static void Telegram(Ctx c)
    {
        var tdata = Path.Combine(c.K.RoamingAppData, "Telegram Desktop", "tdata");
        for (int i = 0; i < 4; i++)
            c.Put(Path.Combine(tdata, "user_data", "cache", "0", c.F.Hex(16).ToUpperInvariant()), c.F.Blob(5_000, 150_000), 3 + i * 25, "cleanup,telegram");
        c.Put(Path.Combine(tdata, "user_data", "media_cache", "0", c.F.Hex(16).ToUpperInvariant()), c.F.Blob(20_000, 200_000), 40, "cleanup,telegram");
        c.Canary(Path.Combine(tdata, "key_datas"), c.F.Blob(300, 400), 100, "Telegram login (session key)");
        c.Canary(Path.Combine(tdata, "D877F783D5D3EF8Cs"), c.F.Blob(500, 900), 2, "Telegram login (session)");
        c.Canary(Path.Combine(tdata, "D877F783D5D3EF8C", "maps"), c.F.Blob(200, 600), 2, "Telegram login (session map)");
        c.Canary(Path.Combine(tdata, "settingss"), c.F.Blob(300, 900), 2, "Telegram settings");
        c.Put(Path.Combine(c.K.RoamingAppData, "Telegram Desktop", "Telegram.exe"), c.F.Exe(), 30, "app");
    }

    private static void Spotify(Ctx c)
    {
        // Plan §7 #17: Spotify is cleaned only with Spotify's own button, so nothing here may be touched.
        var local = Path.Combine(c.K.LocalAppData, "Spotify");
        for (int i = 0; i < 3; i++)
            c.Put(Path.Combine(local, "Storage", c.F.Hex(2), $"{c.F.Hex(32)}.file"), c.F.Blob(30_000, 200_000), 2 + i * 15, "spotify-storage");
        c.Put(Path.Combine(local, "Data", c.F.Hex(2), $"{c.F.Hex(32)}.file"), c.F.Blob(30_000, 200_000), 10, "spotify-offline");
        c.NeverTree(local, "Spotify storage + offline songs: only Spotify's own Clear cache button");
        c.Canary(Path.Combine(c.K.RoamingAppData, "Spotify", "prefs"), Fake.Text("storage.size=10240\n"), 5, "Spotify settings");
    }
}
