using PcTidy.Core;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>
/// Last line of defence inside the Cleanup feature (the SafetyGuard checks again before anything runs).
/// Every quarantine pick passes through here; anything that could hold a login, a password, a game or a
/// live log is dropped, even if a target root was somehow wrong.
/// </summary>
internal sealed class NeverTouch
{
    /// <summary>Browser / app files and folders that hold logins, passwords, settings or site data.</summary>
    private static readonly HashSet<string> AccountData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal",
        "Local State", "Local Storage", "IndexedDB", "Session Storage", "Sessions", "Extensions",
        "Web Data", "Web Data-journal", "Bookmarks", "Bookmarks.bak", "Preferences", "Secure Preferences",
        // Firefox (places.sqlite holds bookmarks too, so Firefox history is never offered)
        "key4.db", "logins.json", "cookies.sqlite", "places.sqlite", "formhistory.sqlite",
    };

    /// <summary>Only the opt-in "Privacy extras" group may touch these.</summary>
    private static readonly HashSet<string> PrivacyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookies", "Cookies-journal", "History", "History-journal",
    };

    /// <summary>Logs Windows is writing right now.</summary>
    private static readonly HashSet<string> LiveLogs = new(StringComparer.OrdinalIgnoreCase) { "CBS.log", "dism.log" };

    private readonly List<string> _neverUnder = new();
    private readonly string _telegramData;

    public NeverTouch(IKnownLocations k)
    {
        if (k.SteamRoot is { } steam)
        {
            _neverUnder.Add(Path.Combine(steam, "steamapps"));
            _neverUnder.Add(Path.Combine(steam, "userdata"));
            _neverUnder.Add(Path.Combine(steam, "config"));
        }
        _neverUnder.AddRange(k.SteamLibraries.Select(l => Path.Combine(l, "steamapps")));
        _neverUnder.AddRange(k.GameInstallDirs);
        _neverUnder.Add(k.DataRoot);
        _neverUnder.Add(k.ClaudeDir);
        _neverUnder.Add(k.CodexDir);
        _telegramData = Path.Combine(k.RoamingAppData, "Telegram Desktop", "tdata");
    }

    /// <param name="scanRoot">The cleanup root the pick was found under.</param>
    /// <param name="path">The file or folder that would be quarantined.</param>
    /// <param name="privacyExtras">True only for the opt-in History / Cookies items.</param>
    public bool Allows(string scanRoot, string path, bool privacyExtras)
    {
        var relative = Path.GetRelativePath(scanRoot, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false; // the root itself, or outside it

        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (AccountData.Contains(segment) || LiveLogs.Contains(segment)) return false;
            if (!privacyExtras && PrivacyFiles.Contains(segment)) return false;
        }

        foreach (var never in _neverUnder)
            if (PathUtil.IsUnder(path, never)) return false;

        // Telegram: only the media cache folders (user_data, user_data#2...). The rest of tdata is your login.
        if (PathUtil.IsUnder(path, _telegramData))
        {
            var inside = Path.GetRelativePath(_telegramData, path).Split('\\')[0];
            if (!inside.StartsWith("user_data", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
