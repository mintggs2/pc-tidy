namespace PcTidy.Features.Privacy.Sweep;

/// <summary>
/// What the Privacy sweep remembers: the sites whose cookies are kept (you stay signed in there).
/// Stored with <c>AppSettings.SetFeature("privacy", …)</c>; the shell saves settings.
/// </summary>
public sealed class PrivacySettings
{
    public List<string> KeepSites { get; set; } = new();

    public static PrivacySettings Load(AppSettings settings)
    {
        try { return settings.GetFeature<PrivacySettings>(PrivacyIds.Privacy) ?? new PrivacySettings(); }
        catch (System.Text.Json.JsonException) { return new PrivacySettings(); }
    }

    public void Store(AppSettings settings) => settings.SetFeature(PrivacyIds.Privacy, this);

    /// <summary>The keep-list as hosts ("https://www.Example.com/x" → "example.com"), distinct, sorted.</summary>
    public IReadOnlyList<string> KeepHosts()
        => KeepSites.Select(CookieKeeper.NormalizeSite).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>A browser profile with a cookie database PC Tidy can edit (Chromium Network\Cookies, Firefox cookies.sqlite).</summary>
public sealed record BrowserProfile(string Browser, string BrowserArg, string Process, string ProfileName, string ProfileDir, string CookieFile);

/// <summary>
/// The v2 Cookie keeper, reused: "delete cookies except these sites" for each browser profile, via the engine's
/// CookiesDeleteExcept handler (it edits a verified copy, keeps the old database in quarantine, undo puts it back).
/// Only the cookie database is involved: history, saved passwords, bookmarks and settings are never touched here.
/// </summary>
public static class CookieKeeper
{
    /// <summary>Sites people usually want to stay signed in to, offered as ticks (none ticked until you choose).</summary>
    public static readonly IReadOnlyList<string> CommonSites = new[]
    {
        "google.com", "youtube.com", "microsoft.com", "live.com", "office.com", "github.com", "amazon.com",
        "apple.com", "discord.com", "reddit.com", "x.com", "facebook.com", "instagram.com", "linkedin.com",
        "netflix.com", "twitch.tv", "steampowered.com", "steamcommunity.com", "paypal.com", "dropbox.com",
    };

    private sealed record Chromium(string Name, string Arg, string Process, string[] UserData);

    private static readonly Chromium[] ChromiumBrowsers =
    {
        new("Chrome", "chrome", "chrome", new[] { "Google", "Chrome", "User Data" }),
        new("Edge", "edge", "msedge", new[] { "Microsoft", "Edge", "User Data" }),
        new("Brave", "brave", "brave", new[] { "BraveSoftware", "Brave-Browser", "User Data" }),
    };

    /// <summary>"Default" and "Profile N" (Chromium), every Firefox profile folder: those that have a cookie database.</summary>
    public static IReadOnlyList<BrowserProfile> FindProfiles(IKnownLocations k)
    {
        var list = new List<BrowserProfile>();
        foreach (var b in ChromiumBrowsers)
        {
            var userData = Path.Combine(new[] { k.LocalAppData }.Concat(b.UserData).ToArray());
            foreach (var dir in ChildFolders(userData).Where(d => d.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                                                                  || d.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
            {
                var cookies = Path.Combine(dir.FullName, "Network", "Cookies");
                if (IsPlainFile(cookies)) list.Add(new BrowserProfile(b.Name, b.Arg, b.Process, dir.Name, dir.FullName, cookies));
            }
        }
        var firefox = Path.Combine(k.RoamingAppData, "Mozilla", "Firefox", "Profiles");
        foreach (var dir in ChildFolders(firefox))
        {
            var cookies = Path.Combine(dir.FullName, "cookies.sqlite");
            if (IsPlainFile(cookies)) list.Add(new BrowserProfile("Firefox", "firefox", "firefox", FirefoxName(dir.Name), dir.FullName, cookies));
        }
        return list;
    }

    /// <summary>"https://www.Example.com:443/x" → "example.com" ("www." dropped: a kept site keeps its subdomains). Empty if unusable.</summary>
    public static string NormalizeSite(string? raw)
    {
        var h = (raw ?? "").Trim().ToLowerInvariant();
        if (h.Contains("://") && Uri.TryCreate(h, UriKind.Absolute, out var uri)) h = uri.Host;
        var cut = h.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) h = h[..cut];
        var colon = h.LastIndexOf(':');
        if (colon > 0) h = h[..colon];
        h = h.Trim('.');
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
        // A real host name: letters, digits, dots and dashes, with at least one dot.
        return h.Length is > 2 and <= 253 && h.Contains('.') && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? h : "";
    }

    /// <summary>"k3x9q2ab.default-release" → "default-release".</summary>
    private static string FirefoxName(string folder) => folder.IndexOf('.') is var i and > 0 && i < folder.Length - 1 ? folder[(i + 1)..] : folder;

    private static IEnumerable<DirectoryInfo> ChildFolders(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<DirectoryInfo>();
            return new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static bool IsPlainFile(string path)
    {
        try
        {
            var f = new FileInfo(path);
            return f.Exists && (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0 && !CloudFile.IsPlaceholder(f.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
