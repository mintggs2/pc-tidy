using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.Cards;

/// <summary>What every card builder gets: the scan context (paths, guard, walker), the read-only source and the clock.</summary>
public sealed record CardContext(ScanContext Scan, IInsightsSource Source, DateTime NowUtc, CancellationToken Token)
{
    public IKnownLocations Locations => Scan.Locations;
    public ISystemInfo? System => Scan.System;
    public bool IsLaptop => System?.IsLaptop ?? false;
    public GpuVendor Gpu => System?.PrimaryGpu ?? GpuVendor.Unknown;
    public IReadOnlyList<string> GpuNames => System?.GpuNames ?? Array.Empty<string>();
    public int WindowsBuild => System?.WindowsBuild ?? 0;

    private WindowsIdentity? _identity;
    /// <summary>Read once per scan (it walks the Uninstall registry): used by the modified-Windows, backup and Dev Drive cards.</summary>
    public WindowsIdentity Identity => _identity ??= Source.ReadWindowsIdentity();

    private bool _cpuRead;
    private string? _cpu;
    /// <summary>The processor brand string, read once per scan (null when unreadable).</summary>
    public string? CpuName
    {
        get
        {
            if (!_cpuRead) { _cpu = Source.ReadCpuName(); _cpuRead = true; }
            return _cpu;
        }
    }

    /// <summary>Windows build from the registry read, falling back to the system facts.</summary>
    public int Build => Identity.Build > 0 ? Identity.Build : WindowsBuild;

    /// <summary>An app in the Uninstall list whose display name starts with <paramref name="name"/> (case-insensitive).</summary>
    public bool HasApp(string name) => Identity.InstalledApps.Any(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plan items the safety rules allow right now, plus the first refusal (so a row can say why it's greyed).</summary>
    public (IReadOnlyList<PlanItem> Allowed, string? Refusal) Vet(params PlanItem[] items)
    {
        var allowed = new List<PlanItem>();
        string? refusal = null;
        foreach (var item in items)
        {
            var v = Scan.Safety.Check(item);
            if (v.Allowed) allowed.Add(item);
            else refusal ??= v.Reason;
        }
        return (allowed, refusal);
    }
}

/// <summary>Formatting shared by the cards.</summary>
internal static class Words
{
    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    public static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        var d = nowUtc - whenUtc;
        if (d.TotalDays < 1) return "today";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 60) return $"{(int)d.TotalDays} days ago";
        if (d.TotalDays < 730) return $"{(int)(d.TotalDays / 30)} months ago";
        return $"{(int)(d.TotalDays / 365)} years ago";
    }

    /// <summary>The fixed drive a path is on ("C:\"), matched against the PC's drive list (works for sandbox drives too).</summary>
    public static string? DriveRootOf(string path, IReadOnlyList<string> fixedDrives)
        => fixedDrives.Where(d => PathUtil.IsUnder(path, d)).OrderByDescending(d => d.Length).FirstOrDefault() ?? PathUtil.DriveOf(path);

    /// <summary>
    /// A drive root as people know it: "D:" on a real PC. A practice PC's drive is a folder, shown the way Home shows it
    /// ("D (practice)"), never as the long practice-folder path.
    /// </summary>
    public static string ShowDrive(string root, bool sandbox)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
        if (trimmed.Length <= 2) return trimmed;
        var name = Path.GetFileName(trimmed);
        return sandbox ? $"{name} (practice)" : name;
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
}
