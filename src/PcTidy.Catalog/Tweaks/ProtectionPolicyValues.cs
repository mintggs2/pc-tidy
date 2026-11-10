using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Registry values that switch Microsoft Defender or Windows Update off or hold updates back: every value under the
/// Defender policy key, and the Windows Update pause, deferral, "no automatic updates", blocked-access and update-server
/// policies the bad-tweak finder removes. Undo must never put one back (ELV-1, UNDO-1): the undo data comes from a file the
/// user's programs can change, and "put back what was there" must not mean PC Tidy switching protection off again.
/// </summary>
public static class ProtectionPolicyValues
{
    private const string DefenderPolicy = BadTweakCatalog.Defender;
    private const string UpdatePolicy = BadTweakCatalog.WindowsUpdatePolicy;

    /// <summary>(hive, key, value names; empty = every value under the key and its subkeys).</summary>
    private static readonly (RegistryHive Hive, string Key, string[] Names)[] Values =
    {
        (RegistryHive.LocalMachine, DefenderPolicy, Array.Empty<string>()),
        (RegistryHive.LocalMachine, UpdatePolicy, new[]
        {
            "PauseQualityUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdates", "PauseFeatureUpdates",
            "PauseQualityUpdatesEndTime", "PauseFeatureUpdatesEndTime", "PauseUpdatesStartTime", "PauseUpdatesExpiryTime",
            "DeferQualityUpdates", "DeferFeatureUpdates", "DeferQualityUpdatesPeriodInDays", "DeferFeatureUpdatesPeriodInDays",
            "DisableWindowsUpdateAccess", "DoNotConnectToWindowsUpdateInternetLocations", "WUServer", "WUStatusServer",
        }),
        (RegistryHive.LocalMachine, UpdatePolicy + @"\AU", new[] { "NoAutoUpdate", "AUOptions", "UseWUServer" }),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", new[] { "NoWindowsUpdate" }),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\WindowsUpdate", new[] { "DisableWindowsUpdateAccess" }),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", new[] { "FlightSettingsMaxPauseDays" }),
    };

    /// <summary>The test area practice runs redirect a logical key into: <c>HKCU\Software\PCTidyTest\…\HKLM\&lt;key&gt;</c>.</summary>
    private const string TestArea = @"Software\PCTidyTest\";

    /// <summary>
    /// A plain-English reason when putting <paramref name="valueName"/> back under <paramref name="keyPath"/> would switch
    /// Defender or Windows Update off again; null otherwise. Keys redirected into the test area are judged by the logical
    /// key they stand for, and a <c>WOW6432Node</c> view counts the same.
    /// </summary>
    public static string? WhyNeverRestore(RegistryHive hive, string keyPath, string? valueName)
    {
        var (h, key) = Logical(hive, keyPath.Trim('\\'));
        key = key.Replace(@"SOFTWARE\WOW6432Node\", @"SOFTWARE\", StringComparison.OrdinalIgnoreCase);
        foreach (var (vHive, vKey, names) in Values)
        {
            if (vHive != h) continue;
            if (names.Length == 0)
            {
                if (key.Equals(vKey, StringComparison.OrdinalIgnoreCase) || key.StartsWith(vKey + "\\", StringComparison.OrdinalIgnoreCase))
                    return "PC Tidy didn't put this Microsoft Defender policy back: it would switch part of Defender's protection off again.";
            }
            else if (key.Equals(vKey, StringComparison.OrdinalIgnoreCase) && valueName is { } n && names.Contains(n, StringComparer.OrdinalIgnoreCase))
                return "PC Tidy didn't put this Windows Update policy back: it would pause or block updates again.";
        }
        return null;
    }

    private static (RegistryHive Hive, string Key) Logical(RegistryHive hive, string key)
    {
        if (hive != RegistryHive.CurrentUser || !key.StartsWith(TestArea, StringComparison.OrdinalIgnoreCase)) return (hive, key);
        var parts = key.Split('\\');
        for (int i = 2; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("HKLM", StringComparison.OrdinalIgnoreCase)) return (RegistryHive.LocalMachine, string.Join('\\', parts[(i + 1)..]));
            if (parts[i].Equals("HKCU", StringComparison.OrdinalIgnoreCase)) return (RegistryHive.CurrentUser, string.Join('\\', parts[(i + 1)..]));
        }
        return (hive, key);
    }
}
