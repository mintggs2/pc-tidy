using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>The tabs of the PC care page. Every scan group id starts with its tab id and a dot ("security.drivers").</summary>
public static class PcCareTabs
{
    public const string Updates = "updates";
    public const string Repair = "repair";
    public const string Network = "network";
    /// <summary>The lag check (B2): its own panel, not scan rows. Read-only, runs only on request.</summary>
    public const string LagCheck = "lag";
    /// <summary>Fix-it recipes (B5): cards with a Run button, not scan rows.</summary>
    public const string Fixes = "fixes";
    public const string Security = "security";
    public const string Browsers = "browsers";
    public const string Crashes = "crashes";
    public const string Restore = "restore";
    public const string Leftovers = "leftovers";
    public const string Shredder = "shredder";
    public const string Report = "report";

    public static readonly IReadOnlyList<(string Id, string Title)> All = new[]
    {
        (Updates, "App updates"),
        (Repair, "Repair"),
        (Network, "Network"),
        (LagCheck, "Lag check"),
        (Fixes, "Fixes"),
        (Security, "Security"),
        (Browsers, "Browsers"),
        (Crashes, "Crashes"),
        (Restore, "Restore points"),
        (Leftovers, "Leftovers"),
        (Shredder, "Shredder"),
        (Report, "Health report"),
    };

    /// <summary>
    /// The tabs in four plain groups, so the strip fits a 1000 px window: Fix (things to repair or update),
    /// Security, History (what happened) and Tools (things you start yourself). Every tab is in exactly one group.
    /// </summary>
    public static readonly IReadOnlyList<(string Title, IReadOnlyList<string> TabIds)> Groups = new (string, IReadOnlyList<string>)[]
    {
        ("Fix", new[] { Updates, Repair, Network, LagCheck, Fixes }),
        ("Security", new[] { Security, Browsers }),
        ("History", new[] { Crashes, Restore }),
        ("Tools", new[] { Leftovers, Shredder, Report }),
    };

    public static string TitleOf(string tabId) => All.First(t => t.Id == tabId).Title;

    /// <summary>Tabs that show their own panel (no scan needed, no tick boxes): Lag check and Fixes.</summary>
    public static bool IsPanel(string tabId) => tabId is LagCheck or Fixes;

    public static string GroupId(string tab, string name) => tab + "." + name;

    public static string Of(string groupId)
    {
        var dot = groupId.IndexOf('.');
        return dot > 0 ? groupId[..dot] : groupId;
    }
}

/// <summary>Options the PC care page passes to its scanner (ScanContext.Options[<see cref="Key"/>]).</summary>
public sealed record PcCareScanOptions
{
    public const string Key = "pccare.options";

    /// <summary>Time ~10 lookups against the current DNS and three public ones (sends network traffic, so only on request).</summary>
    public bool RunDnsTest { get; init; }

    /// <summary>Ask winget for app updates (takes a few seconds and needs internet).</summary>
    public bool CheckAppUpdates { get; init; } = true;

    /// <summary>How far back the crash report looks.</summary>
    public int CrashDays { get; init; } = 30;

    public static PcCareScanOptions From(IReadOnlyDictionary<string, object> options)
        => options.TryGetValue(Key, out var o) && o is PcCareScanOptions p ? p : new PcCareScanOptions();
}

/// <summary>One finished PC Tidy run, for the health report's "recent runs" list.</summary>
public sealed record RecentRun(DateTime StartedUtc, string Title, string Status);

/// <summary>Everything the result builder needs besides the snapshot.</summary>
public sealed record PcCareBuildContext
{
    public required DateTime NowUtc { get; init; }
    public required string DataRoot { get; init; }
    public required string SystemDrive { get; init; }
    public IReadOnlyList<DriveDetails> Drives { get; init; } = Array.Empty<DriveDetails>();
    public IReadOnlyList<RecentRun> RecentRuns { get; init; } = Array.Empty<RecentRun>();
    /// <summary>Words removed from the exported report (user name, profile folder, PC name).</summary>
    public IReadOnlyList<string> RedactTerms { get; init; } = Array.Empty<string>();
    public PcCareScanOptions Options { get; init; } = new();
    /// <summary>Headings other sections add to the saved report (<see cref="Report.IHealthReportSectionSource"/>), already read.</summary>
    public IReadOnlyList<Report.ReportSection> ExtraReportSections { get; init; } = Array.Empty<Report.ReportSection>();
}

/// <summary>The scan result plus per-tab notes and the raw snapshot (so the page can rebuild without rescanning).</summary>
public sealed record PcCareScanReport(
    ScanResult Result,
    IReadOnlyDictionary<string, string> TabNotes,
    PcCareSnapshot Snapshot,
    AntivirusVerdict? Antivirus,
    IReadOnlySet<Catalog.PcCare.CrashFixArea> CrashFixAreas,
    string? ReportPath)
{
    /// <summary>The health report's facts at a glance, shown on its tab as a plain key/value list.</summary>
    public IReadOnlyList<Report.ReportSection> Facts { get; init; } = Array.Empty<Report.ReportSection>();
    /// <summary>The extra headings read for this scan, kept so a rebuild (DNS test, run history) doesn't ask again.</summary>
    public IReadOnlyList<Report.ReportSection> ExtraReportSections { get; init; } = Array.Empty<Report.ReportSection>();
}
