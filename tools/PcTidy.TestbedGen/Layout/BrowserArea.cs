using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Chrome / Edge / Brave (Default + "Profile 1"), Firefox, legacy INetCache.
/// Caches are cleanup targets; logins, cookies, history, Local State, site storage are canaries.</summary>
internal static class BrowserArea
{
    public static readonly string[] ChromiumProfiles = { "Default", "Profile 1" };

    /// <summary>User Data folders, relative to LocalAppData. Bulk cache files go into their profiles' Cache_Data.</summary>
    public static readonly string[] ChromiumUserData =
    {
        Path.Combine("Google", "Chrome", "User Data"),
        Path.Combine("Microsoft", "Edge", "User Data"),
        Path.Combine("BraveSoftware", "Brave-Browser", "User Data"),
    };

    public const string FirefoxProfile = "k3x9q2ab.default-release";

    public static void Build(Ctx c)
    {
        foreach (var userData in ChromiumUserData) Chromium(c, Path.Combine(c.K.LocalAppData, userData));
        Firefox(c);
        InetCache(c);
    }

    private static void Chromium(Ctx c, string userData)
    {
        c.Canary(Path.Combine(userData, "Local State"), Fake.Text("{\"os_crypt\":{\"encrypted_key\":\"RFBBUEkBAAAA-fake\"}}"), 0.2,
            "Local State holds the key for saved passwords: losing it makes them unreadable");
        foreach (var folder in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
        {
            c.Put(Path.Combine(userData, folder, "index"), c.F.Blob(256, 2_000), 3, "cleanup,browser-cache");
            c.Put(Path.Combine(userData, folder, "data_0"), c.F.Blob(8_000, 40_000), 40, "cleanup,browser-cache,old");
        }

        foreach (var profile in ChromiumProfiles)
        {
            var p = Path.Combine(userData, profile);
            var cache = Path.Combine(p, "Cache", "Cache_Data");
            c.Put(Path.Combine(cache, "index"), c.F.Blob(256, 1_000), 0.1, "cleanup,browser-cache,fresh");
            for (int i = 0; i < 4; i++) c.Put(Path.Combine(cache, $"data_{i}"), c.F.Blob(8_000, 80_000), 1 + i * 10, "cleanup,browser-cache");
            for (int i = 1; i <= 6; i++) c.Put(Path.Combine(cache, $"f_{i:x6}"), c.F.Blob(2_000, 90_000), i * 7, "cleanup,browser-cache");
            c.Put(Path.Combine(p, "Code Cache", "js", "index"), c.F.Blob(256, 1_000), 2, "cleanup,browser-cache");
            for (int i = 0; i < 3; i++) c.Put(Path.Combine(p, "Code Cache", "js", $"{c.F.Hex(16)}_0"), c.F.Blob(1_000, 40_000), 5 + i * 9, "cleanup,browser-cache");
            c.Put(Path.Combine(p, "Code Cache", "wasm", "index"), c.F.Blob(256, 1_000), 15, "cleanup,browser-cache");
            c.Put(Path.Combine(p, "GPUCache", "index"), c.F.Blob(256, 1_000), 1, "cleanup,browser-cache");
            c.Put(Path.Combine(p, "GPUCache", "data_0"), c.F.Blob(8_000, 30_000), 25, "cleanup,browser-cache");

            // Never offered at all: passwords, form data, bookmarks, settings, site storage, extensions.
            c.Canary(Path.Combine(p, "Login Data"), c.F.Sqlite(), 1, "Saved passwords");
            c.Canary(Path.Combine(p, "Login Data For Account"), c.F.Sqlite(), 1, "Saved passwords (account)");
            c.Canary(Path.Combine(p, "Web Data"), c.F.Sqlite(), 2, "Autofill, cards, addresses");
            c.Canary(Path.Combine(p, "Bookmarks"), Fake.Text("{\"roots\":{\"bookmark_bar\":{\"children\":[]}},\"version\":1}"), 6, "Bookmarks");
            c.Canary(Path.Combine(p, "Preferences"), Fake.Text("{\"profile\":{\"name\":\"Person 1\"}}"), 0.2, "Browser settings");
            c.Canary(Path.Combine(p, "Secure Preferences"), Fake.Text("{\"protection\":{}}"), 0.2, "Browser settings");
            c.Canary(Path.Combine(p, "Local Storage", "leveldb", "000003.log"), c.F.LevelDb(), 1, "Site storage (logins)");
            c.Canary(Path.Combine(p, "Local Storage", "leveldb", "CURRENT"), Fake.Text("MANIFEST-000001\n"), 1, "Site storage (logins)");
            c.Canary(Path.Combine(p, "IndexedDB", "https_www.example.com_0.indexeddb.leveldb", "000003.log"), c.F.LevelDb(), 3, "Site databases");
            c.Canary(Path.Combine(p, "Service Worker", "CacheStorage", c.F.Hex(40), "index.txt"), c.F.Blob(200, 2_000), 9, "Offline site data (not a cache target)");
            c.Canary(Path.Combine(p, "Extensions", "aapbdbdomjkkjkaonfhkkikfgjllcleb", "2.0.16_0", "manifest.json"),
                Fake.Text("{\"name\":\"Fake Extension\",\"version\":\"2.0.16\"}"), 80, "Installed extension");
            // Privacy extras: offered unticked ("signs you out"), so they survive every default run.
            c.Canary(Path.Combine(p, "Network", "Cookies"), c.F.Sqlite(), 0.3, "Cookies: privacy extra, never default-ticked", CanaryTier.Default);
            c.Canary(Path.Combine(p, "History"), c.F.Sqlite(), 0.3, "History: privacy extra, never default-ticked", CanaryTier.Default);
        }
    }

    private static void Firefox(Ctx c)
    {
        var roamingFx = Path.Combine(c.K.RoamingAppData, "Mozilla", "Firefox");
        c.Canary(Path.Combine(roamingFx, "profiles.ini"),
            Fake.Text($"[Profile0]\r\nName=default-release\r\nIsRelative=1\r\nPath=Profiles/{FirefoxProfile}\r\nDefault=1\r\n"), 200, "Firefox profile list");
        var roaming = Path.Combine(roamingFx, "Profiles", FirefoxProfile);
        c.Canary(Path.Combine(roaming, "logins.json"), Fake.Text("{\"nextId\":2,\"logins\":[{\"id\":1,\"hostname\":\"https://example.com\",\"encryptedUsername\":\"fake\"}]}"), 4, "Firefox saved passwords");
        c.Canary(Path.Combine(roaming, "key4.db"), c.F.Sqlite(), 4, "Firefox password key");
        c.Canary(Path.Combine(roaming, "places.sqlite"), c.F.Sqlite(), 0.5, "Firefox bookmarks + history");
        c.Canary(Path.Combine(roaming, "prefs.js"), Fake.Text("user_pref(\"browser.startup.page\", 3);\n"), 0.5, "Firefox settings");
        c.Canary(Path.Combine(roaming, "cookies.sqlite"), c.F.Sqlite(), 0.3, "Firefox cookies: privacy extra", CanaryTier.Default);

        var local = Path.Combine(c.K.LocalAppData, "Mozilla", "Firefox", "Profiles", FirefoxProfile);
        for (int i = 0; i < 6; i++)
            c.Put(Path.Combine(local, "cache2", "entries", c.F.Hex(40).ToUpperInvariant()), c.F.Blob(1_000, 60_000), 2 + i * 8, "cleanup,firefox-cache");
        c.Put(Path.Combine(local, "cache2", "index"), c.F.Blob(200, 2_000), 0.2, "cleanup,firefox-cache");
        c.Put(Path.Combine(local, "startupCache", "startupCache.8.little"), c.F.Blob(20_000, 80_000), 1, "cleanup,firefox-cache");
        c.Put(Path.Combine(local, "thumbnails", $"{c.F.Hex(32)}.png"), c.Png(c.Seed + 1, 48, 32), 12, "cleanup,firefox-cache");
    }

    private static void InetCache(Ctx c)
    {
        var inet = Path.Combine(c.K.LocalAppData, "Microsoft", "Windows", "INetCache");
        c.Put(Path.Combine(inet, "IE", "4XKZ1Q2B", "page[1].htm"), Fake.Text("<html><body>cached</body></html>"), 70, "cleanup,inetcache,old");
        c.Put(Path.Combine(inet, "IE", "4XKZ1Q2B", "logo[1].png"), c.Png(c.Seed + 2, 32, 32), 70, "cleanup,inetcache,old");
        c.Put(Path.Combine(inet, "IE", "9QWE7RTY", "script[2].js"), Fake.Text("var cached = true;"), 3, "cleanup,inetcache");
    }
}
