using Microsoft.Win32;

namespace PcTidy.Safety.Detection;

/// <summary>One entry from the Uninstall registry lists (only what the safety rules need).</summary>
public sealed record InstalledAppEntry(string DisplayName, string? Publisher, string InstallLocation);

/// <summary>Reads InstallLocation from the three Uninstall lists: machine 64-bit, machine 32-bit (WOW6432Node), current user.</summary>
public static class InstalledApps
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledAppEntry> Read()
    {
        var hives = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };
        var result = new List<InstalledAppEntry>();
        foreach (var (hive, view) in hives)
        {
            try { result.AddRange(ReadHive(hive, view)); }
            catch { /* one unreadable hive never hides the others */ }
        }
        return result;
    }

    private static IEnumerable<InstalledAppEntry> ReadHive(RegistryHive hive, RegistryView view)
    {
        using var list = DetectionUtil.OpenKey(hive, view, UninstallKey);
        foreach (var name in DetectionUtil.SubKeyNames(list))
        {
            using var app = DetectionUtil.OpenKey(hive, view, $@"{UninstallKey}\{name}");
            var location = InstallFolder(DetectionUtil.ReadString(app, "InstallLocation"));
            if (location is null) continue;
            yield return new InstalledAppEntry(DetectionUtil.ReadString(app, "DisplayName") ?? name,
                DetectionUtil.ReadString(app, "Publisher"), location);
        }
    }

    /// <summary>Some installers write a file (…\app.exe) instead of a folder: use its folder.</summary>
    private static string? InstallFolder(string? raw)
    {
        var path = DetectionUtil.CleanPath(raw);
        if (path is null) return null;
        try
        {
            if (File.Exists(path)) return Path.GetDirectoryName(path);
        }
        catch { /* treat as a folder */ }
        return path;
    }
}
