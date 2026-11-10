using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Games.Common;

/// <summary>Builds the PlanItems the Games section emits. Features only describe changes; the engine carries them out.</summary>
internal static class Plans
{
    public static PlanItem Quarantine(string path, bool isDirectory, long size, DateTime lastWriteUtc, string description,
        IReadOnlyList<string>? closeFirst = null, Dictionary<string, string>? args = null)
        => new()
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = GamesContext.FeatureId,
            Description = description,
            SourcePath = path,
            IsDirectory = isDirectory,
            ExpectedSize = size,
            ExpectedLastWriteUtc = lastWriteUtc,
            RequiresClosedApps = closeFirst is { Count: > 0 } ? closeFirst : null,
            Args = args,
        };

    /// <summary>
    /// The engine's duplicate check (Engine DuplicateVerifier): right before the copy moves, both it and the copy that
    /// stays are hashed again; if either changed or the kept one is gone, the copy is left where it is.
    /// </summary>
    public static Dictionary<string, string> VerifyAgainst(string keepPath, string fullHash) => new()
    {
        ["verifyHash"] = fullHash,
        ["keepPath"] = keepPath,
        ["verifyMode"] = "file",
    };

    /// <summary>Opens a launcher screen or Settings page. Harmless; nothing for PC Tidy to undo.</summary>
    public static PlanItem OpenExternal(string target, string description)
        => new()
        {
            Kind = ActionKind.OpenExternal,
            FeatureId = GamesContext.FeatureId,
            Description = description,
            Args = new Dictionary<string, string> { ["target"] = target },
        };

    /// <summary>
    /// Keeps only the items the safety rules allow right now. Returns the first refusal reason when nothing is allowed,
    /// so the row can be shown greyed out with an honest explanation instead of silently vanishing.
    /// </summary>
    public static (IReadOnlyList<PlanItem> Allowed, string? Refusal) Vet(ISafetyGuard safety, IEnumerable<PlanItem> items)
    {
        var allowed = new List<PlanItem>();
        string? refusal = null;
        foreach (var item in items)
        {
            SafetyVerdict verdict;
            try { verdict = safety.Check(item); }
            catch (Exception ex) { verdict = SafetyVerdict.Block("PC Tidy couldn't check this safely: " + ex.Message); }
            if (verdict.Allowed) allowed.Add(item);
            else refusal ??= verdict.Reason ?? "PC Tidy's safety rules don't allow changing this.";
        }
        return (allowed, allowed.Count == 0 ? refusal : null);
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

    public static int MonthsBetween(DateTime fromUtc, DateTime toUtc) => (int)Math.Floor((toUtc - fromUtc).TotalDays / 30.44);

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
