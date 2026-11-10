using System.Diagnostics;
using System.Globalization;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>
/// Turns a read-only <see cref="PcCareSnapshot"/> into the PC care page's groups and rows. Pure (no I/O), so every rule is
/// tested with fixed facts. Nothing is ever ticked by default; rows that are only "shown so you know" carry no action.
/// </summary>
public static partial class PcCareResultBuilder
{
    public static PcCareScanReport Build(PcCareSnapshot s, PcCareBuildContext c, TimeSpan? duration = null)
    {
        var sw = Stopwatch.StartNew();
        var groups = new List<ScanGroup>();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        var fixAreas = new HashSet<Catalog.PcCare.CrashFixArea>();

        AddUpdates(s, groups, notes);
        AddRepair(s, groups, notes);
        AddNetwork(s, groups, notes);
        var verdict = AddSecurity(s, groups, notes);
        AddBrowsers(s, groups, notes);
        AddCrashes(s, c, groups, notes, fixAreas);
        AddRestore(s, c, groups, notes);
        AddLeftovers(s, c, groups, notes);
        AddShredder(c, groups, notes);
        var (reportPath, facts) = AddReport(s, c, verdict, groups, notes);

        var result = new ScanResult
        {
            FeatureId = PcCareActions.FeatureId,
            Title = "PC care",
            Depth = ScanDepth.Quick,
            Duration = duration ?? sw.Elapsed,
            Groups = groups.Where(g => g.Items.Count > 0).ToList(),
        };
        return new PcCareScanReport(result, notes, s, verdict, fixAreas, reportPath) { Facts = facts, ExtraReportSections = c.ExtraReportSections };
    }

    // ------------------------------------------------------------------ row helpers

    /// <summary>A row the user may tick. Never pre-ticked: everything in PC care is picked by hand.</summary>
    internal static ScanItem Pick(string title, string reason, Badge badge, IReadOnlyList<PlanItem> actions, string? detail = null,
        string? state = null, IReadOnlyDictionary<string, string>? details = null, bool restart = false) => new()
    {
        Title = title, Reason = reason, Badge = badge, Actions = actions, Detail = detail, CurrentState = state, Details = details,
        DefaultSelected = false, NeedsAdmin = actions.Any(a => a.NeedsAdmin), NeedsRestart = restart,
    };

    /// <summary>A "shown so you know" row: no action, nothing to tick.</summary>
    internal static ScanItem Info(string title, string reason, string? detail = null, string? state = null,
        IReadOnlyDictionary<string, string>? details = null) => new()
    {
        Title = title, Reason = reason, Detail = detail, CurrentState = state, Details = details, Badge = Badge.Safe, DefaultSelected = false,
    };

    /// <summary>A greyed-out row with the reason it can't be picked.</summary>
    internal static ScanItem Disabled(string title, string reason, string disabledReason, Badge badge = Badge.Check, string? detail = null, string? state = null) => new()
    {
        Title = title, Reason = reason, DisabledReason = disabledReason, Badge = badge, Detail = detail, CurrentState = state, DefaultSelected = false,
    };

    internal static ScanGroup Group(string tab, string name, string title, string description, Badge badge, IReadOnlyList<ScanItem> items)
        => new() { Id = PcCareTabs.GroupId(tab, name), Title = title, Description = description, Badge = badge, Items = items };

    internal static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    internal static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalDays < 1) return "today";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 60) return $"{(int)d.TotalDays} days ago";
        return Date(utc);
    }

    internal static string Plural(int n, string word, string? plural = null) => n == 1 ? $"1 {word}" : $"{n:N0} {plural ?? word + "s"}";
}
