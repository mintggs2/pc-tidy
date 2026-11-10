namespace PcTidy.Core;

/// <summary>
/// A cleanup target allowed to touch the CONTENTS (never the folder itself) of specific folders
/// that are otherwise inside a protected area (e.g. C:\Windows\Temp). Single source of truth:
/// the SafetyGuard honours ONLY these, and the Cleanup feature uses the same roots to scan.
/// </summary>
/// <param name="DeniedNames">
/// Folder/file names that must never be touched anywhere below the roots, even though the root is allowed
/// (e.g. browser "Login Data", "Local State"). Checked per path segment, case-insensitive.
/// </param>
/// <param name="AllowedExtensions">If set, only files with these extensions (".msi") may be touched (no folders).</param>
public sealed record CarveOut(string Id, string Title, bool NeedsAdmin, Func<IKnownLocations, IEnumerable<string>> Roots,
    IReadOnlySet<string>? DeniedNames = null, IReadOnlySet<string>? AllowedExtensions = null)
{
    /// <summary>True if the carve-out restricts by extension and this path's extension isn't allowed.</summary>
    public bool ExtensionNotAllowed(string path)
        => AllowedExtensions is { Count: > 0 } a && !a.Contains(Path.GetExtension(path));

    /// <summary>True if any segment of <paramref name="relativePath"/> (path below the root) is a denied name.</summary>
    public bool IsDenied(string relativePath)
        => DeniedNames is { Count: > 0 } d &&
           relativePath.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).Any(d.Contains);
}

public static class CarveOuts
{
    public const string UserTemp = "cleanup.user-temp";
    public const string WindowsTemp = "cleanup.windows-temp";
    public const string BrowserCache = "cleanup.browser-cache";
    public const string FirefoxCache = "cleanup.firefox-cache";
    public const string InetCache = "cleanup.inetcache";
    public const string BrowserPrivacy = "cleanup.browser-privacy";
    public const string CrashDumps = "cleanup.crash-dumps";
    public const string ErrorReports = "cleanup.error-reports";
    public const string WindowsLogs = "cleanup.windows-logs";
    public const string UpdateDownloads = "cleanup.update-downloads";
    public const string DeliveryOptimization = "cleanup.delivery-optimization";
    public const string ShaderCache = "cleanup.shader-cache";
    public const string ThumbnailCache = "cleanup.thumbnail-cache";
    public const string RecycleBin = "cleanup.recycle-bin";
    public const string Discord = "cleanup.discord";
    public const string SteamCache = "cleanup.steam-cache";
    public const string Spotify = "cleanup.spotify";
    public const string Telegram = "cleanup.telegram";
    public const string OldInstallers = "cleanup.old-installers";
    public const string WindowsOld = "cleanup.windows-old";
    public const string ComponentStore = "cleanup.component-store";

    private static string P(params string[] parts) => Path.Combine(parts);

    private static HashSet<string> Names(params string[] n) => new(n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Browser files that hold logins, passwords, saved data and settings: never touched by any cache clean.</summary>
    public static readonly IReadOnlySet<string> BrowserNeverTouch = Names(
        "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal",
        "Web Data", "Web Data-journal", "Local State", "Local Storage", "IndexedDB", "Session Storage", "Sessions",
        "Bookmarks", "Bookmarks.bak", "Preferences", "Secure Preferences", "Extensions", "Local Extension Settings",
        "Extension State", "File System", "databases", "Sync Data", "Wallet", "Affiliation Database",
        "logins.json", "key4.db", "key3.db", "places.sqlite", "formhistory.sqlite", "permissions.sqlite",
        "cert9.db", "sessionstore.jsonlz4", "sessionstore-backups", "storage",
        // Web apps' offline data (mail, docs, installed web apps): never treated as cache.
        "Service Worker", "WebStorage", "shared_proto_db", "Shared Dictionary");

    /// <summary>Cookies and history: only the explicit "Privacy extras" target may touch these.</summary>
    public static readonly IReadOnlySet<string> BrowserPrivacyFiles = Names(
        "Cookies", "Cookies-journal", "History", "History-journal", "cookies.sqlite", "Network");

    /// <summary>Telegram's media caches: tdata\user_data plus extra accounts' user_data#2, #3… (read-only listing).</summary>
    public static IEnumerable<string> TelegramUserDataDirs(IKnownLocations k)
    {
        var tdata = P(k.RoamingAppData, "Telegram Desktop", "tdata");
        var list = new List<string> { P(tdata, "user_data") };
        try
        {
            if (Directory.Exists(tdata))
                list.AddRange(Directory.EnumerateDirectories(tdata, "user_data#*")
                    .Where(d => int.TryParse(Path.GetFileName(d)["user_data#".Length..], out _)));
        }
        catch { /* unreadable: just the default account */ }
        return list;
    }

    public static IEnumerable<string> ChromiumUserDataDirs(IKnownLocations k) => new[]
    {
        P(k.LocalAppData, "Google", "Chrome", "User Data"),
        P(k.LocalAppData, "Microsoft", "Edge", "User Data"),
        P(k.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
    };

    public static readonly IReadOnlyList<CarveOut> All = new List<CarveOut>
    {
        // ---- ordinary per-user places: the carve-out simply pins Cleanup to exactly these folders ----
        new(UserTemp, "Your temp files", false, k => new[] { k.UserTemp }),
        new(BrowserCache, "Browser caches", false, ChromiumUserDataDirs,
            new HashSet<string>(BrowserNeverTouch.Concat(BrowserPrivacyFiles), StringComparer.OrdinalIgnoreCase)),
        new(FirefoxCache, "Firefox cache", false, k => new[] { P(k.LocalAppData, "Mozilla", "Firefox", "Profiles") },
            new HashSet<string>(BrowserNeverTouch.Concat(BrowserPrivacyFiles), StringComparer.OrdinalIgnoreCase)),
        new(InetCache, "Old Windows web cache", false, k => new[] { P(k.LocalAppData, "Microsoft", "Windows", "INetCache") }),
        // Privacy extras: Chromium History/Cookies only. Logins and saved data stay denied.
        new(BrowserPrivacy, "Browser history and cookies", false, ChromiumUserDataDirs, BrowserNeverTouch),
        new(Discord, "Discord cache", false, k => new[]
        {
            P(k.RoamingAppData, "discord"), P(k.RoamingAppData, "discordptb"), P(k.RoamingAppData, "discordcanary"),
        }, Names("Local Storage", "IndexedDB", "Session Storage", "settings.json", "Cookies", "Network")),
        new(Telegram, "Telegram media cache", false, TelegramUserDataDirs),
        new(OldInstallers, "Old installers in Downloads", false, k => new[] { k.Downloads },
            AllowedExtensions: Names(".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".exe")),
        new(ThumbnailCache, "Thumbnail and icon cache", false, k => new[] { P(k.LocalAppData, "Microsoft", "Windows", "Explorer") }),
        // ---- Windows-tool targets: no file paths at all (Windows' own cleaners do the work) ----
        new(Spotify, "Spotify cache", false, _ => Array.Empty<string>()),
        new(RecycleBin, "Recycle Bin", false, _ => Array.Empty<string>()),
        new(DeliveryOptimization, "Delivery Optimization cache", true, _ => Array.Empty<string>()),
        new(WindowsOld, "Previous Windows installation", true, _ => Array.Empty<string>()),
        new(ComponentStore, "Windows component cleanup", true, _ => Array.Empty<string>()),

        // ---- protected areas opened only for these exact folders ----
        new(WindowsTemp, "Windows temp files", true, k => new[] { k.WindowsTemp }),
        new(CrashDumps, "Crash dumps", true, k => new[]
        {
            P(k.WindowsDir, "Minidump"), P(k.WindowsDir, "LiveKernelReports"), P(k.LocalAppData, "CrashDumps"),
        }),
        new(ErrorReports, "Error reports", true, k => new[]
        {
            P(k.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
            P(k.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
            P(k.ProgramData, "Microsoft", "Windows", "WER", "Temp"),
            P(k.LocalAppData, "Microsoft", "Windows", "WER"),
        }),
        new(WindowsLogs, "Old Windows logs", true, k => new[] { P(k.WindowsDir, "Logs", "CBS"), P(k.WindowsDir, "Logs", "DISM") }),
        new(UpdateDownloads, "Windows Update downloads", true, k => new[] { P(k.WindowsDir, "SoftwareDistribution", "Download") }),
        new(ShaderCache, "GPU shader caches", false, k => new[]
        {
            P(k.LocalAppData, "NVIDIA", "DXCache"), P(k.LocalAppData, "NVIDIA", "GLCache"), P(k.RoamingAppData, "NVIDIA", "ComputeCache"),
            P(k.LocalAppData, "D3DSCache"), P(k.LocalAppData, "AMD", "DxCache"), P(k.LocalAppData, "AMD", "DxcCache"),
            P(k.LocalAppData, "AMD", "VkCache"), P(k.LocalAppData, "Intel", "ShaderCache"),
            P(k.ProgramData, "NVIDIA Corporation", "NV_Cache"),
        }),
        new(SteamCache, "Steam web cache & dumps", false, k =>
        {
            var l = new List<string> { P(k.LocalAppData, "Steam", "htmlcache") };
            if (k.SteamRoot is { } s) { l.Add(P(s, "appcache", "httpcache")); l.Add(P(s, "dumps")); l.Add(P(s, "logs")); }
            return l;
        }, Names("Local Storage", "IndexedDB", "Cookies", "Network")),
    };

    /// <summary>Single file allowed by a carve-out (MEMORY.DMP is a file directly in the Windows folder).</summary>
    public static IEnumerable<(string CarveOutId, string File)> AllowedSingleFiles(IKnownLocations k)
    {
        yield return (CrashDumps, P(k.WindowsDir, "MEMORY.DMP"));
    }

    public static CarveOut? Find(string? id) => id is null ? null : All.FirstOrDefault(c => c.Id == id);
}
