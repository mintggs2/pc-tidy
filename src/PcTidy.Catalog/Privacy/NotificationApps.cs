using System.Security;
using Microsoft.Win32;

namespace PcTidy.Catalog.Privacy;

/// <summary>
/// Windows' per-app notification switches (Settings → System → Notifications): one key per app under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\&lt;AppId&gt;</c>, DWORD <c>Enabled</c>
/// (0 = off; 1 or missing = on). Shared by the allowance source (what may be written) and the Notifications
/// scanner (what is shown), so both always agree on which apps exist. Read-only: nothing here writes.
/// </summary>
public static class NotificationApps
{
    public const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    public const string EnabledValue = "Enabled";
    public const string Off = "0";
    public const string On = "1";

    /// <summary>
    /// Security alerts stay on: hiding them means you'd miss "a threat was found" or "your firewall is off".
    /// They are shown greyed and never get an allowance.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.Defender.SecurityCenter",
        "Windows.SystemToast.SecurityAndMaintenance",
    };

    public static bool IsProtected(string appId) => ProtectedAppIds.Contains(appId);

    /// <summary>A subkey name PC Tidy is willing to name in a plan: short, printable, a single key (no separators).</summary>
    public static bool IsUsableAppId(string? appId)
        => !string.IsNullOrWhiteSpace(appId) && appId.Length <= 255 && !appId.Any(char.IsControl) && !appId.Contains('\\')
           && appId.Trim() == appId;

    /// <summary>
    /// The app ids registered under <paramref name="settingsKeyPath"/> (HKCU, 64-bit view), sorted, read-only.
    /// Unusable names are left out; an unreadable or missing key gives an empty list, never an exception.
    /// </summary>
    public static IReadOnlyList<string> ReadAppIds(string settingsKeyPath = SettingsKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(settingsKeyPath.Trim('\\'), writable: false);
            if (key is null) return Array.Empty<string>();
            return key.GetSubKeyNames().Where(IsUsableAppId).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or ObjectDisposedException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The key of one app's switch.</summary>
    public static string AppKey(string settingsKeyPath, string appId) => $@"{settingsKeyPath.Trim('\\')}\{appId}";
}
