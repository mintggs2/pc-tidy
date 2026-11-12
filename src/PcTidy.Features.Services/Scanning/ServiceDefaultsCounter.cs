using PcTidy.Features.Services.Rules;
using PcTidy.Features.Services.Sources;

namespace PcTidy.Features.Services.Scanning;

/// <summary>A Windows service whose start type isn't one of the Windows defaults the catalog lists for it.</summary>
public sealed record ChangedService(ServiceRecord Record, ServiceEntry Entry, ServiceStart Current);

/// <summary>
/// "N changed from Windows default" (the rail counter) and the "many disabled at once" summary line (v3.1). Pure and cheap:
/// it needs only the Service Control Manager's list (no file signatures, no process queries). Counts only WINDOWS services
/// (their program or DLL is under the Windows folder) that have a catalog default; per-user copies and drivers are skipped.
/// </summary>
public static class ServiceDefaultsCounter
{
    /// <summary>From this many Windows services disabled against their default, the page shows one summary line.</summary>
    public const int MassDisabledMinimum = 5;

    public static IReadOnlyList<ChangedService> ChangedFromDefault(IEnumerable<ServiceRecord> records, string windowsDir)
    {
        var list = new List<ChangedService>();
        foreach (var r in records)
        {
            if (r.IsDriver || (r.IsPerUser && !ServicePolicy.IsTemplate(r))) continue;
            if (r.StartType is not { } current || current is ServiceStart.Boot or ServiceStart.System) continue;
            if (ServiceInventory.CatalogEntry(r) is not { WindowsDefaults.Count: > 0 } entry) continue;
            if (!IsWindowsService(r, windowsDir)) continue;
            if (entry.WindowsDefaults.Contains(current)) continue;
            list.Add(new ChangedService(r, entry, current));
        }
        return list;
    }

    /// <summary>Windows services set to Disabled where Windows doesn't ship them Disabled.</summary>
    public static int DisabledAgainstDefault(IEnumerable<ChangedService> changed)
        => changed.Count(c => c.Current == ServiceStart.Disabled && !c.Entry.WindowsDefaults.Contains(ServiceStart.Disabled));

    /// <summary>
    /// The rail text, or null when nothing changed (the rail then shows nothing). Short, so the section name keeps its
    /// room (3.1.2: "5 changed from Windows default" cut "Services" down to "S…"); the page says from what.
    /// </summary>
    public static string? BadgeText(int changed) => changed <= 0 ? null : $"{changed} changed";

    /// <summary>The page's one summary line when many Windows services were switched off at once; null below the threshold.</summary>
    public static string? MassDisabledText(int disabled) => disabled < MassDisabledMinimum ? null
        : $"{disabled} Windows services are switched off against Windows' own setting, the way \"disable these services\" lists and presets leave them. "
          + "The bad-services repair puts back the ones Windows needs; each other one has a \"Back to Windows default\" choice.";

    /// <summary>True when the service's program (or, for svchost services, its DLL) is under the Windows folder. String work only.</summary>
    public static bool IsWindowsService(ServiceRecord r, string windowsDir)
    {
        var program = ProgramPath(r.ImagePath, windowsDir);
        var file = program is not null && Path.GetFileName(program).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.ServiceDll)
            ? Normalize(r.ServiceDll!, windowsDir)
            : program;
        if (file is null || string.IsNullOrWhiteSpace(windowsDir)) return false;
        var root = windowsDir.TrimEnd('\\') + "\\";
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ProgramPath(string? imagePath, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = imagePath.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            text = close < 0 ? text[1..] : text[1..close];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) text = text[..(exe + 4)];
        }
        return Normalize(text, windowsDir);
    }

    private static string Normalize(string path, string windowsDir)
    {
        var p = path.Trim().Trim('"');
        foreach (var v in new[] { "%SystemRoot%", "%windir%" })
            p = p.Replace(v, windowsDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windowsDir, p[@"\SystemRoot\".Length..]);
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windowsDir, p);
        return p;
    }
}
