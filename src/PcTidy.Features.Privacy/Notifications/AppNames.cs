using System.Globalization;
using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>
/// A readable name for a notification app id (an AppUserModelID such as "Microsoft.WindowsStore_8wekyb3d8bbwe!App",
/// "com.squirrel.Discord.Discord" or "Windows.SystemToast.Suggested"). Order: the app's own registered DisplayName
/// (HKCU\Software\Classes\AppUserModelId), Windows' built-in senders, then a cautious guess from the id itself.
/// The raw id is always shown as the row's second line, so a guess never hides what it is.
/// </summary>
public static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows.SystemToast.Suggested"] = "Suggestions from Windows",
        ["Windows.SystemToast.WindowsTip"] = "Windows tips",
        ["Windows.ActionCenter.SmartOptOut"] = "\"Turn off notifications for this app?\" prompts",
        ["Windows.SystemToast.AutoPlay"] = "AutoPlay",
        ["Windows.SystemToast.StartupApp"] = "New startup apps",
        ["Windows.SystemToast.WiFiNetworkManager"] = "Wi-Fi",
        ["Windows.SystemToast.DefaultAudioEndpoint"] = "Audio devices",
        ["Windows.SystemToast.BackgroundAccess"] = "Battery saver",
        ["Windows.SystemToast.BdeUnlock"] = "BitLocker",
        ["Windows.SystemToast.Calling"] = "Calls",
        ["Windows.SystemToast.Devices"] = "Devices",
        ["Windows.SystemToast.Print.Notification"] = "Printing",
        ["Windows.SystemToast.HelloFace"] = "Windows Hello",
        ["Windows.SystemToast.SecurityAndMaintenance"] = "Security and maintenance",
        ["Windows.Defender.SecurityCenter"] = "Windows Security",
        ["windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"] = "Settings",
        ["Microsoft.WindowsStore_8wekyb3d8bbwe!App"] = "Microsoft Store",
        ["Microsoft.SkyDrive.Desktop"] = "OneDrive",
        ["Microsoft.ScreenSketch_8wekyb3d8bbwe!App"] = "Snipping Tool",
        ["Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows"] = "Outlook",
        ["Microsoft.Windows.Explorer"] = "File Explorer",
        ["Chrome"] = "Google Chrome",
        ["MSEdge"] = "Microsoft Edge",
    };

    /// <summary>Generic last parts of an id that say nothing about the app ("com.Roblox.Player" → "Roblox").</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "client", "desktop", "player", "systray", "tray", "exe", "launcher", "main", "notifications", "com", "org", "net", "ai", "io",
    };

    public const string TrayIconPrefix = "NotifyIconGeneratedAumid_";

    public static string Friendly(string appId, UserRegistry registry)
    {
        if (OwnDisplayName(appId, registry) is { } own) return own;
        if (Known.TryGetValue(appId, out var known)) return known;
        if (appId.StartsWith(TrayIconPrefix, StringComparison.OrdinalIgnoreCase)) return "An app's tray icon (name not recorded)";
        return Guess(appId);
    }

    /// <summary>The DisplayName an app registered for its notifications, if it's plain text (not a resource reference).</summary>
    private static string? OwnDisplayName(string appId, UserRegistry registry)
    {
        var name = registry.ReadString($@"Software\Classes\AppUserModelId\{appId}", "DisplayName")?.Trim();
        return string.IsNullOrEmpty(name) || name.StartsWith('@') || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || name.Length > 80
            ? null
            : name;
    }

    /// <summary>Best effort from the id: package name before "_", file name before ".exe", else the last meaningful dotted part.</summary>
    public static string Guess(string appId)
    {
        var id = appId;
        var bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        var underscore = id.IndexOf('_');
        if (underscore > 0) id = id[..underscore];

        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var exe = Array.FindLastIndex(parts, p => p.Equals("exe", StringComparison.OrdinalIgnoreCase));
        if (exe > 0) return Pretty(parts[exe - 1]);
        var meaningful = parts.Where(p => !Generic.Contains(p) && !p.All(char.IsDigit) && !p.StartsWith('{')).ToList();
        if (meaningful.Count == 0) return appId;
        // "Microsoft.WindowsCalculator" → "Windows Calculator"; "com.squirrel.Discord.Discord" → "Discord".
        var last = meaningful[^1];
        return Pretty(last);
    }

    /// <summary>"WindowsCalculator" → "Windows Calculator"; "opencode" → "Opencode".</summary>
    private static string Pretty(string word)
    {
        if (word.Length == 0) return word;
        var spaced = new System.Text.StringBuilder();
        for (int i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(word[i - 1])) spaced.Append(' ');
            spaced.Append(c);
        }
        var s = spaced.ToString().Replace('-', ' ');
        return char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
    }
}
