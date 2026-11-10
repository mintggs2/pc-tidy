using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>Why an Uninstall entry isn't shown as an app (Research §4: hide system parts, updates and nameless entries).</summary>
public enum HiddenReason { None, NoDisplayName, SystemComponent, ChildOfAnotherEntry, Update, KbPatch }

/// <summary>
/// Reads the three Uninstall registry lists: machine 64-bit, machine 32-bit (WOW6432Node) and the current user.
/// One unreadable list or entry never hides the others.
/// </summary>
public sealed partial class UninstallRegistryReader
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (Hive Hive, bool View64, AppScope Scope, string Label)[] Lists =
    {
        (Hive.LocalMachine, true, AppScope.Machine, "HKLM64"),
        (Hive.LocalMachine, false, AppScope.Machine, "HKLM32"),
        (Hive.CurrentUser, true, AppScope.User, "HKCU"),
    };

    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack", "Critical Update" };

    [GeneratedRegex(@"\bKB\d{6,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    private readonly IRegistryReader _registry;

    public UninstallRegistryReader(IRegistryReader registry) => _registry = registry;

    /// <summary>Visible apps from all three lists, in list order (64-bit machine, 32-bit machine, user).</summary>
    public IReadOnlyList<InstalledApp> Read(CancellationToken ct = default)
    {
        var apps = new List<InstalledApp>();
        foreach (var list in Lists)
        {
            ct.ThrowIfCancellationRequested();
            using var root = _registry.Open(list.Hive, list.View64, UninstallPath);
            if (root is null) continue;
            foreach (var name in RegistryValues.SubKeyNames(root))
            {
                using var entry = RegistryValues.OpenChild(root, name);
                if (entry is null) continue;
                var app = ReadEntry(entry, name, list.Hive, list.View64, list.Scope, list.Label);
                if (app is not null) apps.Add(app);
            }
        }
        return apps;
    }

    /// <summary>Hide rules for one entry, from its raw values. Public for tests.</summary>
    public static HiddenReason HideReason(string? displayName, long? systemComponent, string? parentKeyName, string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return HiddenReason.NoDisplayName;
        if (systemComponent == 1) return HiddenReason.SystemComponent;
        if (!string.IsNullOrWhiteSpace(parentKeyName)) return HiddenReason.ChildOfAnotherEntry;
        if (releaseType is not null && UpdateReleaseTypes.Contains(releaseType)) return HiddenReason.Update;
        if (KbNumber().IsMatch(displayName)) return HiddenReason.KbPatch;
        return HiddenReason.None;
    }

    private static InstalledApp? ReadEntry(RegistryKey entry, string keyName, Hive hive, bool view64, AppScope scope, string label)
    {
        var displayName = RegistryValues.String(entry, "DisplayName");
        var hidden = HideReason(displayName, RegistryValues.Number(entry, "SystemComponent"),
            RegistryValues.String(entry, "ParentKeyName"), RegistryValues.String(entry, "ReleaseType"));
        if (hidden != HiddenReason.None) return null;

        var uninstall = RegistryValues.String(entry, "UninstallString");
        var quiet = RegistryValues.String(entry, "QuietUninstallString");
        var isMsi = RegistryValues.Number(entry, "WindowsInstaller") == 1;
        // MSI entries are keyed by their ProductCode; otherwise the command itself may name it.
        var productCode = isMsi && UninstallCommand.IsGuidKeyName(keyName) ? keyName.ToUpperInvariant() : UninstallCommand.MsiProductCode(uninstall);

        var (installDate, approximate) = InstallDate(entry);
        var estimatedKb = RegistryValues.Number(entry, "EstimatedSize");
        var view = hive == Hive.LocalMachine && !view64 ? @"SOFTWARE\WOW6432Node" : "SOFTWARE";
        var hivePrefix = hive == Hive.LocalMachine ? "HKLM" : "HKCU";

        return new InstalledApp
        {
            Id = $"desktop:{label}:{keyName}",
            Name = displayName!,
            Source = AppSource.Desktop,
            Scope = scope,
            Publisher = RegistryValues.String(entry, "Publisher"),
            Version = RegistryValues.String(entry, "DisplayVersion"),
            InstallLocation = CleanFolder(RegistryValues.String(entry, "InstallLocation")),
            ReportedSizeBytes = estimatedKb is > 0 and < int.MaxValue ? estimatedKb * 1024 : null,
            InstallDate = installDate,
            InstallDateIsApproximate = approximate,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            MsiProductCode = productCode,
            WingetId = WingetIdOf(entry, keyName),
            DisplayIcon = RegistryValues.String(entry, "DisplayIcon"),
            RegistryKey = $@"{hivePrefix}\{view}{UninstallPath["SOFTWARE".Length..]}\{keyName}",
            RegistryHive = hive,
            RegistryView64 = view64,
            NoRemove = RegistryValues.Number(entry, "NoRemove") == 1,
            LauncherId = UninstallCommand.SteamAppId(uninstall),
        };
    }

    /// <summary>InstallDate "yyyyMMdd" (sometimes with separators); else the key's last-change time, marked approximate.</summary>
    private static (DateTime? Date, bool Approximate) InstallDate(RegistryKey entry)
    {
        var raw = RegistryValues.String(entry, "InstallDate");
        if (raw is not null)
        {
            var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length == 8 && DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d.Year >= 1995 && d <= DateTime.Now.AddDays(1))
                return (d, false);
        }
        var written = RegistryValues.LastWriteUtc(entry);
        return written is null ? (null, false) : (written.Value.ToLocalTime().Date, true);
    }

    /// <summary>winget writes its id into the entries it creates (portable / zip installs).</summary>
    private static string? WingetIdOf(RegistryKey entry, string keyName)
    {
        var id = RegistryValues.String(entry, "WinGetPackageIdentifier");
        if (id is not null) return id;
        const string suffix = "_Microsoft.Winget.Source_8wekyb3d8bbwe";
        return keyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && keyName.Length > suffix.Length ? keyName[..^suffix.Length] : null;
    }

    /// <summary>Quotes, env vars and a trailing file name removed ("C:\App\app.exe" → "C:\App"). Null if unusable.</summary>
    public static string? CleanFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"').Trim();
        try { s = Environment.ExpandEnvironmentVariables(s); } catch (ArgumentException) { return null; }
        if (!Path.IsPathFullyQualified(s)) return null;
        try
        {
            s = Path.GetFullPath(s);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(s) is { } dir) s = dir;
            return s.Length > 3 ? s.TrimEnd('\\') : s;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
