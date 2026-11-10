using PcTidy.Safety.Detection;

namespace PcTidy.Features.Games.Optimize;

/// <summary>The signed-in Steam user's localconfig.vdf (where Steam keeps per-game Launch Options), read once per scan.</summary>
internal sealed record SteamUserConfig(string Path, string Text, DateTime LastWriteUtc, long Length)
{
    /// <summary>Steam's 64-bit ids are 76561197960265728 + the account id used for the userdata folder.</summary>
    private const ulong SteamId64Base = 76561197960265728UL;

    public static string[] AppPath(string appId) => VdfText.AppsPath.Append(appId).ToArray();

    public string? LaunchOptions(string appId) => VdfText.GetValue(Text, AppPath(appId), "LaunchOptions");

    /// <summary>
    /// Picks the most recent Steam user (config\loginusers.vdf "MostRecent"), else the newest localconfig.vdf.
    /// Null when Steam has no user data on this PC.
    /// </summary>
    public static SteamUserConfig? Load(string? steamRoot)
    {
        if (steamRoot is null) return null;
        var userdata = System.IO.Path.Combine(steamRoot, "userdata");
        string[] users;
        try { users = Directory.Exists(userdata) ? Directory.GetDirectories(userdata) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var candidates = users
            .Select(u => System.IO.Path.Combine(u, "config", "localconfig.vdf"))
            .Where(p => { try { return File.Exists(p); } catch { return false; } })
            .ToList();
        if (candidates.Count == 0) return null;

        var recent = MostRecentAccountId(steamRoot);
        var chosen = candidates.FirstOrDefault(p => recent is not null
                && string.Equals(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(p))), recent, StringComparison.Ordinal))
            ?? candidates.OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } }).First();
        try
        {
            var info = new FileInfo(chosen);
            return new SteamUserConfig(chosen, File.ReadAllText(chosen), info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? MostRecentAccountId(string steamRoot)
    {
        var users = Vdf.TryLoad(System.IO.Path.Combine(steamRoot, "config", "loginusers.vdf"))?["users"];
        if (users is null) return null;
        foreach (var (id64, node) in users.Children)
            if (node.GetString("MostRecent") == "1" && ulong.TryParse(id64, out var id) && id > SteamId64Base)
                return (id - SteamId64Base).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }
}
