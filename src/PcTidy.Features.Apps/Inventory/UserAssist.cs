using System.Buffers.Binary;
using PcTidy.Core;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>One program launch record from UserAssist: what was started, how often, and when last.</summary>
/// <param name="Target">Decoded name: a path (known-folder GUIDs resolved where possible) or a Store app id ("Family!App").</param>
public sealed record UserAssistEntry(string Target, int RunCount, DateTime? LastRunUtc);

/// <summary>
/// Windows' per-user record of programs started from Start, the taskbar and Explorer
/// (HKCU\...\Explorer\UserAssist\{GUID}\Count). Value names are ROT13-encoded; the 72-byte data holds the run count
/// at offset 4 and the last run time (FILETIME) at offset 60. Read-only, no admin.
/// </summary>
public sealed class UserAssistReader
{
    public const string UserAssistPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    /// <summary>Known-folder GUIDs Windows writes at the start of UserAssist paths, mapped to this PC's folders.</summary>
    private readonly Dictionary<string, string> _knownFolders;
    private readonly IRegistryReader _registry;

    public UserAssistReader(IRegistryReader registry, IKnownLocations locations)
    {
        _registry = registry;
        var system32 = Path.Combine(locations.WindowsDir, "System32");
        _knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = locations.ProgramFiles,      // ProgramFilesX64
            ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = locations.ProgramFiles,      // ProgramFiles
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = locations.ProgramFilesX86,   // ProgramFilesX86
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = locations.WindowsDir,        // Windows
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = system32,                    // System
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Path.Combine(locations.WindowsDir, "SysWOW64"),
            ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = locations.LocalAppData,      // LocalAppData
            ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = locations.RoamingAppData,    // RoamingAppData
            ["{5E6C858F-0E22-4760-9AFE-EA3317B67173}"] = locations.UserProfile,       // Profile
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = locations.Downloads,         // Downloads
            ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = locations.Desktop,           // Desktop
        };
    }

    /// <summary>Every readable entry, newest launch first. An unreadable or damaged value is skipped.</summary>
    public IReadOnlyList<UserAssistEntry> Read(CancellationToken ct = default)
    {
        var result = new List<UserAssistEntry>();
        using var root = _registry.Open(Hive.CurrentUser, true, UserAssistPath);
        if (root is null) return result;
        foreach (var guid in RegistryValues.SubKeyNames(root))
        {
            ct.ThrowIfCancellationRequested();
            using var count = _registry.Open(Hive.CurrentUser, true, $@"{UserAssistPath}\{guid}\Count");
            if (count is null) continue;
            foreach (var valueName in RegistryValues.ValueNames(count))
            {
                var data = RegistryValues.Binary(count, valueName);
                if (data is null) continue;
                var parsed = Parse(data);
                if (parsed is null) continue;
                result.Add(new UserAssistEntry(ResolveKnownFolder(Rot13(valueName)), parsed.Value.RunCount, parsed.Value.LastRunUtc));
            }
        }
        return result.OrderByDescending(e => e.LastRunUtc ?? DateTime.MinValue).ToList();
    }

    /// <summary>ROT13 on ASCII letters only (UserAssist's encoding); everything else is kept.</summary>
    public static string Rot13(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }

    /// <summary>
    /// Run count (offset 4) and last run (FILETIME at offset 60) of a Windows 7+ 72-byte record.
    /// Null for other sizes (old XP-style records, the "UEME_CTLSESSION" bookkeeping value).
    /// A zero time means "not recorded".
    /// </summary>
    public static (int RunCount, DateTime? LastRunUtc)? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 68) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[60..]);
        DateTime? last = null;
        // Reject zero and nonsense values (before 1995 or in the far future) instead of showing a wrong date.
        if (fileTime > 0)
        {
            try
            {
                var d = DateTime.FromFileTimeUtc(fileTime);
                if (d.Year >= 1995 && d <= DateTime.UtcNow.AddDays(2)) last = d;
            }
            catch (ArgumentOutOfRangeException) { /* not a valid time */ }
        }
        if (count < 0) count = 0;
        return (count, last);
    }

    /// <summary>"{6D809377-...}\Vendor\App.exe" → "C:\Program Files\Vendor\App.exe" for the GUIDs this PC can map.</summary>
    public string ResolveKnownFolder(string target)
    {
        if (target.Length > 38 && target[0] == '{' && target[37] == '}' && _knownFolders.TryGetValue(target[..38], out var folder))
            return folder + target[38..];
        return target;
    }
}

/// <summary>Matches UserAssist launches and Steam's LastPlayed to apps. Never guesses: no match means "unknown".</summary>
public static class LastUsedMatcher
{
    public static IReadOnlyList<InstalledApp> Apply(IReadOnlyList<InstalledApp> apps, IReadOnlyList<UserAssistEntry> entries)
    {
        if (entries.Count == 0) return apps;
        var byFamily = new Dictionary<string, UserAssistEntry>(StringComparer.OrdinalIgnoreCase);
        var programs = new List<UserAssistEntry>();
        var shortcuts = new Dictionary<string, UserAssistEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var bang = e.Target.IndexOf('!');
            if (bang > 0 && !e.Target.Contains('\\')) Keep(byFamily, e.Target[..bang], e);
            else if (e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) Keep(shortcuts, Path.GetFileNameWithoutExtension(e.Target), e);
            else if (Path.IsPathFullyQualified(e.Target)) programs.Add(e);
        }

        return apps.Select(app =>
        {
            if (app.LastUsedSource == LastUsedSource.Steam) return app;
            var match = Best(app, byFamily, programs, shortcuts);
            return match is null ? app : app with { LastUsedUtc = match.LastRunUtc, RunCount = match.RunCount, LastUsedSource = LastUsedSource.UserAssist };
        }).ToList();
    }

    private static UserAssistEntry? Best(InstalledApp app, Dictionary<string, UserAssistEntry> byFamily,
        List<UserAssistEntry> programs, Dictionary<string, UserAssistEntry> shortcuts)
    {
        if (app.Source == AppSource.Store)
            return app.PackageFamilyName is { } f && byFamily.TryGetValue(f, out var s) ? s : null;

        var candidates = new List<UserAssistEntry>();
        // A program inside the app's own folder: its install folder, or (many installers leave InstallLocation empty)
        // the folder its icon or uninstaller sits in. Broad or shared folders (a drive, Program Files, Windows,
        // installer caches) prove nothing and are skipped.
        foreach (var folder in AppFolders(app))
            candidates.AddRange(programs.Where(p => PathUtil.IsUnder(p.Target, folder)));
        if (UninstallCommand.IconPath(app.DisplayIcon) is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange(programs.Where(p => p.Target.Equals(icon, StringComparison.OrdinalIgnoreCase)));
        // A Start menu shortcut with exactly the app's name (or its name without version and "(64-bit)").
        if (shortcuts.TryGetValue(app.Name, out var lnk)) candidates.Add(lnk);
        else if (Leftovers.AppNames.Clean(app.Name) is { } clean && shortcuts.TryGetValue(clean, out var cleanLnk)) candidates.Add(cleanLnk);
        return candidates.Where(c => c.LastRunUtc is not null).OrderByDescending(c => c.LastRunUtc).FirstOrDefault();
    }

    private static IEnumerable<string> AppFolders(InstalledApp app)
    {
        var folders = new List<string?> { app.InstallLocation };
        if (app.InstallLocation is null)
        {
            folders.Add(Path.GetDirectoryName(UninstallCommand.IconPath(app.DisplayIcon) ?? ""));
            folders.Add(Path.GetDirectoryName(UninstallCommand.ProgramPath(app.UninstallString) ?? ""));
        }
        return folders.OfType<string>().Where(f => f.Length > 3 && !IsBroadFolder(f)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] BroadNames =
        { "Program Files", "Program Files (x86)", "Windows", "System32", "SysWOW64", "Programs", "Local", "Roaming", "ProgramData", "AppData" };

    private static bool IsBroadFolder(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        if (BroadNames.Any(b => name.Equals(b, StringComparison.OrdinalIgnoreCase))) return true;
        // Installer caches hold many products' setup programs.
        return trimmed.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(@"\Windows\Installer", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(@"\Installshield Installation Information", StringComparison.OrdinalIgnoreCase);
    }

    private static void Keep(Dictionary<string, UserAssistEntry> map, string key, UserAssistEntry e)
    {
        if (!map.TryGetValue(key, out var old) || (e.LastRunUtc ?? DateTime.MinValue) > (old.LastRunUtc ?? DateTime.MinValue)) map[key] = e;
    }
}
