using Microsoft.Win32;

namespace PcTidy.Setup.Install;

/// <summary>What an earlier install left in the Uninstall key.</summary>
public sealed record ExistingInstall(string InstallDir, string Version);

/// <summary>
/// The "Installed apps" entry (Settings → Apps): machine-wide under HKLM\...\Uninstall\PCTidy (tests: a key under
/// HKCU\Software\PCTidyTest). Also reads and removes the entry an older per-user install left in HKCU, and removes
/// everything the app itself may have added to the user's HKCU (right-click verbs, Run value).
/// Only the keys in <see cref="RegistryPaths"/>.
/// </summary>
public sealed class Registration
{
    private readonly RegistryPaths _paths;

    public Registration(RegistryPaths paths) => _paths = paths;

    public ExistingInstall? FindExisting()
    {
        using var root = _paths.OpenMachineRoot();
        return Read(root, _paths.UninstallKey);
    }

    /// <summary>An older per-user install (version 1: %LOCALAPPDATA%\Programs\PC Tidy, entry in HKCU).</summary>
    public ExistingInstall? FindLegacy() => Read(Registry.CurrentUser, _paths.LegacyUninstallKey);

    private static ExistingInstall? Read(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key?.GetValue("InstallLocation") is not string dir || dir.Length == 0) return null;
        return new ExistingInstall(dir, key.GetValue("DisplayVersion") as string ?? "");
    }

    public void Write(InstallEnvironment env, string version, long installedBytes)
    {
        using var root = _paths.OpenMachineRoot();
        using var key = root.CreateSubKey(_paths.UninstallKey, writable: true);
        var uninstaller = Quote(env.UninstallerPath);
        key.SetValue("DisplayName", Product.Name);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Product.Publisher);
        key.SetValue("DisplayIcon", Quote(env.AppPath) + ",0");
        key.SetValue("InstallLocation", env.InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", uninstaller + " --uninstall");
        key.SetValue("QuietUninstallString", uninstaller + " --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)Math.Clamp(installedBytes / 1024, 0, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (Version.TryParse(version, out var v))
        {
            key.SetValue("VersionMajor", v.Major, RegistryValueKind.DWord);
            key.SetValue("VersionMinor", v.Minor, RegistryValueKind.DWord);
        }
    }

    public void RemoveUninstallEntry()
    {
        using var root = _paths.OpenMachineRoot();
        root.DeleteSubKeyTree(_paths.UninstallKey, throwOnMissingSubKey: false);
    }

    public void RemoveLegacyEntry() => Registry.CurrentUser.DeleteSubKeyTree(_paths.LegacyUninstallKey, throwOnMissingSubKey: false);

    /// <summary>
    /// Removes what PC Tidy itself may have added for this user: right-click verbs named PCTidy.* (files, folders, folder
    /// background, drives) and the "PC Tidy" start-with-Windows value (+ its Task Manager on/off flag). Returns what was removed.
    /// </summary>
    public IReadOnlyList<string> RemoveAppFootprint()
    {
        var removed = new List<string>();
        foreach (var shell in new[] { @"*\shell", @"Directory\shell", @"Directory\Background\shell", @"Drive\shell" })
        {
            var shellPath = _paths.Classes + "\\" + shell;
            using var key = Registry.CurrentUser.OpenSubKey(shellPath, writable: true);
            if (key is null) continue;
            foreach (var verb in key.GetSubKeyNames().Where(n => n.StartsWith(Product.ShellVerbPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                key.DeleteSubKeyTree(verb, throwOnMissingSubKey: false);
                removed.Add($@"HKCU\{shellPath}\{verb}");
            }
        }
        foreach (var path in new[] { _paths.Run, _paths.StartupApprovedRun })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key?.GetValue(Product.RunValueName) is null) continue;
            key.DeleteValue(Product.RunValueName, throwOnMissingValue: false);
            removed.Add($@"HKCU\{path}\{Product.RunValueName}");
        }
        return removed;
    }

    /// <summary>The same right-click verbs and Run value, inside a user hive (HKEY_USERS\&lt;SID&gt;) laid out like HKCU.</summary>
    private static readonly string[] HiveShellKeys = { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell", @"Software\Classes\Directory\Background\shell", @"Software\Classes\Drive\shell" };
    private static readonly string[] HiveRunKeys = { RegistryPaths.Real.Run, RegistryPaths.Real.StartupApprovedRun };

    /// <summary>
    /// The admin part of an uninstall: what PC Tidy added for OTHER accounts whose registry is loaded (signed in, or
    /// signed out recently), so their right-click menu doesn't keep entries pointing at a deleted PcTidy.exe.
    /// <paramref name="usersRoot"/> is HKEY_USERS for real (tests: a key under HKCU\Software\PCTidyTest). Only
    /// PCTidy.* verbs and the "PC Tidy" Run value are removed; nothing else in those hives is read or changed.
    /// </summary>
    public static IReadOnlyList<string> RemoveOtherUsersFootprint(RegistryKey usersRoot, string label)
    {
        var removed = new List<string>();
        foreach (var sid in usersRoot.GetSubKeyNames().Where(IsUserHive))
        {
            foreach (var shell in HiveShellKeys)
            {
                try
                {
                    using var key = usersRoot.OpenSubKey(sid + "\\" + shell, writable: true);
                    if (key is null) continue;
                    foreach (var verb in key.GetSubKeyNames().Where(n => n.StartsWith(Product.ShellVerbPrefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        key.DeleteSubKeyTree(verb, throwOnMissingSubKey: false);
                        removed.Add($@"{label}\{sid}\{shell}\{verb}");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* not ours to change */ }
            }
            foreach (var path in HiveRunKeys)
            {
                try
                {
                    using var key = usersRoot.OpenSubKey(sid + "\\" + path, writable: true);
                    if (key?.GetValue(Product.RunValueName) is null) continue;
                    key.DeleteValue(Product.RunValueName, throwOnMissingValue: false);
                    removed.Add($@"{label}\{sid}\{path}\{Product.RunValueName}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* not ours to change */ }
            }
        }
        return removed;
    }

    /// <summary>A person's hive (S-1-5-21-…), not a service account's or the separate _Classes view of one.</summary>
    private static bool IsUserHive(string name)
        => name.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string path) => "\"" + path + "\"";
}
