using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>A Chromium browser and where its profiles live.</summary>
internal sealed record ChromiumBrowser(string Name, string Process, Func<IKnownLocations, string> UserData)
{
    public static readonly IReadOnlyList<ChromiumBrowser> All = new ChromiumBrowser[]
    {
        new("Chrome", RunningApps.Chrome, k => Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data")),
        new("Edge", RunningApps.Edge, k => Path.Combine(k.LocalAppData, "Microsoft", "Edge", "User Data")),
        new("Brave", RunningApps.Brave, k => Path.Combine(k.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")),
    };

    /// <summary>"Default" and "Profile N" folders: the real user profiles (Guest/System profiles hold nothing of yours).</summary>
    public IReadOnlyList<FileEntry> Profiles(ScanSession s, CancellationToken ct)
        => s.ChildFolders(UserData(s.Locations), ct)
            .Where(d => d.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                        || d.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsInstalled(ScanSession s) => Directory.Exists(UserData(s.Locations));
}

/// <summary>
/// Â§7 #3: Chrome / Edge / Brave caches for every profile. Only cache folders are ever listed: the profile's
/// logins, cookies, history, bookmarks, settings and site data are not looked at.
/// Deep adds each profile's Service Worker cache (offline copies websites keep) as an unticked Check row.
/// </summary>
internal sealed class ChromiumCacheTarget : ICleanupTarget
{
    private static readonly string[] ProfileCaches = { Path.Combine("Cache", "Cache_Data"), "Code Cache", "GPUCache" };
    private static readonly string[] SharedCaches = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };

    public CleanupTargetInfo Info => CleanupTargets.BrowserCache;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var installed = ChromiumBrowser.All.Where(b => b.IsInstalled(s)).ToList();
        if (installed.Count == 0) return null;

        var items = new List<ScanItem?>();
        foreach (var browser in installed)
        {
            var apps = new[] { browser.Process };
            var userData = browser.UserData(s.Locations);
            foreach (var profile in browser.Profiles(s, ct))
            {
                items.Add(ItemFactory.Quarantine(s, Info, new ItemSpec
                {
                    Title = $"{browser.Name} cache ({profile.Name})",
                    Detail = profile.FullPath,
                    Apps = apps,
                    OwnerApp = browser.Name,
                }, CachePicks(s, profile.FullPath, ProfileCaches, ct)));
                // Service Worker storage is deliberately NOT offered, even in Deep mode: it holds web apps'
                // offline data (mail, docs, PWAs), not just a cache. The carve-out deny-list blocks it too.
            }
            items.Add(ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = $"{browser.Name} graphics caches",
                Detail = userData,
                Apps = apps,
                OwnerApp = browser.Name,
            }, CachePicks(s, userData, SharedCaches, ct)));
        }
        return items.Present();
    }

    /// <summary>Everything inside each cache folder (the folders themselves stay).</summary>
    internal static List<Pick> CachePicks(ScanSession s, string parent, IEnumerable<string> cacheFolders, CancellationToken ct)
    {
        var picks = new List<Pick>();
        foreach (var name in cacheFolders)
        {
            var tree = s.Tree(Path.Combine(parent, name), ct);
            if (tree is not null) picks.AddRange(TreePlanner.Plan(tree, _ => true, MixedFolders.Descend));
        }
        return picks;
    }
}

/// <summary>
/// Â§7 #6, opt-in privacy extras: only each profile's History and Cookies database files. Never ticked.
/// Saved passwords, Local State, Local Storage and IndexedDB are never offered (losing Local State makes every
/// saved password unreadable). Firefox isn't offered: its history file also holds your bookmarks.
/// </summary>
/// <remarks>
/// Cookie keeper (v2): cookies will become "remove all except these sites", which needs a new engine action
/// that edits the cookie database. Until then the whole Cookies file is listed as one opt-in row.
/// </remarks>
internal sealed class BrowserPrivacyTarget : ICleanupTarget
{
    private static readonly string[] HistoryFiles = { "History", "History-journal" };
    private static readonly string[] CookieFiles =
    {
        Path.Combine("Network", "Cookies"), Path.Combine("Network", "Cookies-journal"), "Cookies", "Cookies-journal",
    };

    public CleanupTargetInfo Info => CleanupTargets.BrowserPrivacy;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var installed = ChromiumBrowser.All.Where(b => b.IsInstalled(s)).ToList();
        if (installed.Count == 0) return null;

        var items = new List<ScanItem?>();
        foreach (var browser in installed)
        foreach (var profile in browser.Profiles(s, ct))
        {
            var apps = new[] { browser.Process };
            items.Add(Row(s, browser, profile, apps, "cookies", CleanupTargets.CookiesReason, CookieFiles,
                new Dictionary<string, string> { ["Later"] = "A future version will let you keep cookies for chosen sites." }));
            items.Add(Row(s, browser, profile, apps, "history", CleanupTargets.HistoryReason, HistoryFiles, null));
        }
        return items.Present();
    }

    private ScanItem? Row(ScanSession s, ChromiumBrowser browser, FileEntry profile, string[] apps, string what, string reason,
        IEnumerable<string> files, IReadOnlyDictionary<string, string>? extra)
    {
        var picks = files
            .Select(f => ScanSession.EntryOf(Path.Combine(profile.FullPath, f)))
            .Where(e => e is { } x && !x.IsReparsePoint && !x.IsCloudPlaceholder && !x.IsSystem)
            .Select(e => new Pick(profile.FullPath, e!.Value, e.Value.Size, 1, false));
        return ItemFactory.Quarantine(s, Info, new ItemSpec
        {
            Title = $"{browser.Name} {what} ({profile.Name})",
            Detail = profile.FullPath,
            Badge = Badge.Check,
            DefaultSelected = false,
            Reason = reason + " Saved passwords are not touched.",
            Apps = apps,
            OwnerApp = browser.Name,
            PrivacyExtras = true,
            ExtraDetails = extra,
        }, picks);
    }
}

/// <summary>Â§7 #4: Firefox's cache2, startupCache and thumbnails in each local profile folder. Nothing else.</summary>
internal sealed class FirefoxCacheTarget : ICleanupTarget
{
    private static readonly string[] CacheFolders = { "cache2", "startupCache", "thumbnails" };

    public CleanupTargetInfo Info => CleanupTargets.FirefoxCache;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var profilesRoot = Path.Combine(s.Locations.LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesRoot)) return null;

        var apps = new[] { RunningApps.Firefox };
        return s.ChildFolders(profilesRoot, ct)
            .Select(profile => ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = $"Firefox cache ({profile.Name})",
                Detail = profile.FullPath,
                Apps = apps,
                OwnerApp = "Firefox",
            }, ChromiumCacheTarget.CachePicks(s, profile.FullPath, CacheFolders, ct)))
            .Present();
    }
}
