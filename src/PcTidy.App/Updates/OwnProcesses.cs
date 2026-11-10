using System.Diagnostics;
using Microsoft.Win32;

namespace PcTidy.App.Updates;

/// <summary>PC Tidy's own uninstaller, as the "Installed apps" entry Setup wrote names it.</summary>
public sealed record UninstallEntry(string UninstallerPath, string InstallLocation);

/// <summary>
/// The programs of PC Tidy's own that the shell may start besides Explorer and Rescue (see FlowHost): a downloaded
/// update installer (<c>--silent</c>, only through <see cref="VerifiedInstaller"/>), the installed uninstaller
/// (<c>--uninstall</c>), and the browser for a release page (https only). All only after the user's OK, never in demo
/// or practice mode, and every path is checked right before.
/// </summary>
public static class OwnProcesses
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCTidy";
    public const string UninstallerName = "uninstall.exe";

    /// <summary>
    /// Reads PC Tidy's own "Installed apps" entry (read-only): the per-machine install's HKLM entry (64-bit view) first,
    /// then HKCU for an older per-user install. Null when PC Tidy wasn't installed with Setup.
    /// </summary>
    public static UninstallEntry? FindUninstaller() => FindUninstaller(Read, File.Exists);

    /// <summary>
    /// Testable form: <paramref name="read"/> returns (UninstallString, InstallLocation) for a hive, or nulls when the
    /// key is missing or unreadable. The first entry that passes <see cref="Parse"/> wins.
    /// </summary>
    public static UninstallEntry? FindUninstaller(Func<RegistryHive, (string? UninstallString, string? InstallLocation)> read, Func<string, bool> fileExists)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            var (uninstall, location) = read(hive);
            if (Parse(uninstall, location, fileExists) is { } entry) return entry;
        }
        return null;
    }

    private static (string?, string?) Read(RegistryHive hive)
    {
        try
        {
            // HKLM: always the 64-bit view (Setup writes there); HKCU has no 32/64-bit split for this key.
            using var root = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
            using var key = root.OpenSubKey(UninstallKey);
            return (key?.GetValue("UninstallString") as string, key?.GetValue("InstallLocation") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return (null, null); }
    }

    /// <summary>
    /// Accepts only <c>"&lt;InstallLocation&gt;\uninstall.exe" --uninstall</c>: a quoted, fully qualified path to a file named
    /// uninstall.exe directly in the install folder, which exists. Anything else (a hand-edited entry, another program) is refused.
    /// </summary>
    public static UninstallEntry? Parse(string? uninstallString, string? installLocation, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(uninstallString) || string.IsNullOrWhiteSpace(installLocation)) return null;
        var s = uninstallString.Trim();
        if (!s.StartsWith('"')) return null;
        int close = s.IndexOf('"', 1);
        if (close < 2) return null;
        var exe = s[1..close];
        var rest = s[(close + 1)..].Trim();
        if (rest != "--uninstall") return null;
        try
        {
            if (!Path.IsPathFullyQualified(exe) || exe.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var full = Path.GetFullPath(exe);
            var folder = Path.GetFullPath(installLocation.Trim().Trim('"')).TrimEnd('\\');
            if (!string.Equals(Path.GetFileName(full), UninstallerName, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)) return null;
            return fileExists(full) ? new UninstallEntry(full, folder) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// Opens a release page in the default browser (only a plain https:// address). Used when this PC Tidy can't install
    /// an update by itself (it isn't signed): the user downloads and runs the installer themselves.
    /// </summary>
    public static void OpenReleasePage(Uri page)
    {
        if (page.Scheme != Uri.UriSchemeHttps || page.UserInfo.Length > 0 || !page.IsAbsoluteUri)
            throw new InvalidOperationException("That release page isn't a plain https:// address, so PC Tidy didn't open it.");
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>The Windows Settings page where System Protection is reached (System → About → System protection).</summary>
    public const string SystemProtectionSettings = "ms-settings:about";

    /// <summary>Opens Windows Settings at <see cref="SystemProtectionSettings"/> (a fixed address; nothing else).</summary>
    public static void OpenSystemProtectionSettings()
        => Process.Start(new ProcessStartInfo(SystemProtectionSettings) { UseShellExecute = true })?.Dispose();

    /// <summary>Starts PC Tidy's own uninstaller window. The caller closes PC Tidy straight after.</summary>
    public static void StartUninstaller(UninstallEntry entry)
    {
        if (Parse($"\"{entry.UninstallerPath}\" --uninstall", entry.InstallLocation, File.Exists) is null)
            throw new InvalidOperationException("PC Tidy's uninstaller isn't where it should be.");
        Process.Start(new ProcessStartInfo(entry.UninstallerPath) { UseShellExecute = false, ArgumentList = { "--uninstall" }, WorkingDirectory = entry.InstallLocation })?.Dispose();
    }
}
