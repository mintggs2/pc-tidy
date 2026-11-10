using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Developer.Common;

/// <summary>Builds the PlanItems Developer cleanup emits. Features only describe changes; the engine carries them out.</summary>
internal static class Plans
{
    public static PlanItem Quarantine(string path, long size, DateTime lastWriteUtc, string description, IReadOnlyList<string>? closeFirst = null)
        => new()
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = DevContext.FeatureId,
            Description = description,
            SourcePath = path,
            IsDirectory = true,
            ExpectedSize = size,
            ExpectedLastWriteUtc = lastWriteUtc,
            RequiresClosedApps = closeFirst is { Count: > 0 } ? closeFirst : null,
        };

    /// <summary>A whitelisted tool run (cache clean, Docker prune, WSL). Never undoable, so always Reversible = false.</summary>
    public static PlanItem Tool(string toolId, string description, IReadOnlyDictionary<string, string>? args = null, long expectedSize = 0,
        IReadOnlyList<string>? closeFirst = null)
        => new()
        {
            Kind = ActionKind.RunSystemTool,
            FeatureId = DevContext.FeatureId,
            Description = description,
            ToolId = toolId,
            Args = args is null ? null : new Dictionary<string, string>(args),
            ExpectedSize = expectedSize,
            Reversible = false,
            RequiresClosedApps = closeFirst is { Count: > 0 } ? closeFirst : null,
        };

    /// <summary>
    /// Keeps only the items the safety rules allow right now. Returns the first refusal reason when nothing is allowed,
    /// so the row can be shown greyed out with an honest explanation instead of silently vanishing.
    /// </summary>
    public static (IReadOnlyList<PlanItem> Allowed, SafetyVerdict? Refusal) Vet(ISafetyGuard safety, IEnumerable<PlanItem> items)
    {
        var allowed = new List<PlanItem>();
        SafetyVerdict? refusal = null;
        foreach (var item in items)
        {
            SafetyVerdict verdict;
            try { verdict = safety.Check(item); }
            catch (Exception ex) { verdict = SafetyVerdict.Block("PC Tidy couldn't check this safely: " + ex.Message); }
            if (verdict.Allowed) allowed.Add(item);
            else refusal ??= verdict;
        }
        return (allowed, allowed.Count == 0 ? refusal ?? SafetyVerdict.Block("PC Tidy's safety rules don't allow changing this.") : null);
    }

    /// <summary>
    /// Several ticked rows may each stop WSL first; one stop is enough. Keeps the first <c>wsl.shutdown</c> and drops
    /// the rest (everything else keeps its order).
    /// </summary>
    public static IReadOnlyList<PlanItem> DropRepeatedShutdowns(IEnumerable<PlanItem> items)
    {
        var list = new List<PlanItem>();
        bool shutdownSeen = false;
        foreach (var item in items)
        {
            if (item.Kind == ActionKind.RunSystemTool && item.ToolId == ToolIds.WslShutdown)
            {
                if (shutdownSeen) continue;
                shutdownSeen = true;
            }
            list.Add(item);
        }
        return list;
    }
}

/// <summary>Friendly wording for ages and dates.</summary>
internal static class Ages
{
    public static string Since(DateTime thenUtc, DateTime nowUtc)
    {
        var days = (nowUtc - thenUtc).TotalDays;
        if (days < 1) return "today";
        if (days < 2) return "yesterday";
        if (days < 45) return $"{(int)days} days ago";
        var months = (int)Math.Round(days / 30.44);
        if (months < 24) return $"{months} months ago";
        return $"{(int)(days / 365.25)} years ago";
    }

    /// <summary>Whole months between two times (30.44-day months, never negative).</summary>
    public static int MonthsBetween(DateTime fromUtc, DateTime toUtc) => Math.Max(0, (int)Math.Floor((toUtc - fromUtc).TotalDays / 30.44));

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Plural(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n:N0} {many ?? one + "s"}";
}
