using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>
/// "Optimize &amp; Organize everything" presets (Plan §8 "Across the whole app").
/// A preset decides which SECTIONS / GROUPS start ticked. Inside a chosen group only items the scanner marked
/// <see cref="ScanItem.DefaultSelected"/> with <see cref="Badge.Safe"/> are ticked: Check and Permanent items are
/// always shown unticked (that is what "(Check)" means in the plan), so a preset can never tick anything risky.
/// Scanners also receive the preset name in <see cref="ScanContext.Preset"/> and may refine their own defaults.
///
/// Matching convention (feature ids / group ids, case-insensitive "contains"):
///   cleanup*                       → Clean
///   organize* + group "download"   → Organize Downloads
///   speed*/startup* + group "startup" → startup items (scanner pre-ticks only high-impact non-Microsoft ones)
///   tweaks* group "clean-explorer" / "privacy-basic" / "safe-gaming" → tweak presets
///   games*/clips* group "clip"     → old clips (Check, never pre-ticked)
///   services group "profile-gaming" / "profile-privacy" → service profiles (Gamer / Privacy)
/// </summary>
public static class OptimizePresets
{
    public const string Safe = "Safe";
    public const string Recommended = "Recommended";
    public const string Gamer = "Gamer";
    public const string Privacy = "Privacy";

    public static readonly IReadOnlyList<string> All = new[] { Safe, Recommended, Gamer, Privacy };

    /// <summary>One "this group is part of the preset" rule.</summary>
    public sealed record Rule(string FeatureContains, string? GroupContains = null);

    private static readonly Rule Cleanup = new("cleanup");
    private static readonly Rule OrganizeDownloads = new("organize", "download");
    private static readonly Rule Startup = new("speed", "startup");
    private static readonly Rule StartupAlt = new("startup");
    private static readonly Rule CleanExplorer = new("tweak", "clean-explorer");
    private static readonly Rule PrivacyBasic = new("tweak", "privacy-basic");
    private static readonly Rule SafeGaming = new("tweak", "safe-gaming");
    private static readonly Rule OldClips = new("game", "clip");
    private static readonly Rule BrowserHistory = new("cleanup", "privacy");
    /// <summary>Windows tips, suggestions and ads (Privacy section's Notifications part): only the Privacy preset ticks them.</summary>
    private static readonly Rule WindowsTips = new("notifications", "tips");
    /// <summary>Services profiles. Gaming is deliberately empty (no proven service change helps games): its group is an
    /// information-only "nothing to do" row, so the preset ticks nothing there.</summary>
    private static readonly Rule ServicesGaming = new("services", "profile-gaming");
    private static readonly Rule ServicesPrivacy = new("services", "profile-privacy");

    /// <summary>Editable table: which groups each preset includes.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Rule>> Rules = new Dictionary<string, IReadOnlyList<Rule>>(StringComparer.OrdinalIgnoreCase)
    {
        [Safe] = new[] { Cleanup },
        [Recommended] = new[] { Cleanup, OrganizeDownloads, Startup, StartupAlt, CleanExplorer, PrivacyBasic },
        [Gamer] = new[] { Cleanup, OrganizeDownloads, Startup, StartupAlt, CleanExplorer, PrivacyBasic, SafeGaming, OldClips, ServicesGaming },
        [Privacy] = new[] { Cleanup, PrivacyBasic, BrowserHistory, WindowsTips, ServicesPrivacy },
    };

    public static int IndexOf(string? preset)
    {
        for (int i = 0; i < All.Count; i++)
            if (string.Equals(All[i], preset, StringComparison.OrdinalIgnoreCase)) return i;
        return 1; // Recommended
    }

    public static bool IncludesGroup(string preset, string featureId, string groupId)
    {
        if (!Rules.TryGetValue(preset, out var rules)) rules = Rules[Recommended];
        foreach (var r in rules)
        {
            if (!featureId.Contains(r.FeatureContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.GroupContains is null || groupId.Contains(r.GroupContains, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Should this item start ticked under the preset?</summary>
    public static bool ShouldTick(string preset, string featureId, ScanGroup group, ScanItem item)
        => IsTickableByDefault(item) && IncludesGroup(preset, featureId, group.Id);

    /// <summary>Hard rule for every default tick: Safe, scanner said so, actionable, not disabled.</summary>
    public static bool IsTickableByDefault(ScanItem item)
        => item.DefaultSelected && item.Badge == Badge.Safe && item.DisabledReason is null && item.Actions.Count > 0
           && item.Actions.All(a => a.Reversible);

    public static string Describe(string preset) => preset switch
    {
        Safe => "Only the clean-up items marked Safe. Nothing else changes.",
        Gamer => "Recommended, plus safe gaming tweaks. Old clips are shown for you to pick.",
        Privacy => "Safe clean-up plus basic privacy settings. Browser history is shown for you to pick.",
        _ => "Safe clean-up, tidy Downloads, slow startup apps, and a few proven settings.",
    };
}

/// <summary>Order sections run in: restore point → cleanup → organize moves → startup → tweaks → the rest → reports.</summary>
public static class SafeOrder
{
    public static int Rank(string featureId)
    {
        var f = featureId.ToLowerInvariant();
        if (f.Contains("cleanup") || f.Contains("clean")) return 10;
        if (f.Contains("duplicate")) return 15;
        if (f.Contains("developer")) return 18;
        if (f.Contains("game") || f.Contains("clip")) return 20;
        if (f.Contains("organize") || f.Contains("photo") || f.Contains("desktop")) return 30;
        if (f.Contains("speed") || f.Contains("startup")) return 40;
        // Services change how Windows services start (restore point first): after the user-file sections and startup
        // apps, before tweaks, drivers and the other system changes.
        if (f.Contains("service")) return 45;
        if (f.Contains("tweak")) return 50;
        if (f.Contains("report") || f.Contains("schedule")) return 90;
        return 60;
    }

    public static IReadOnlyList<ActionPlan> Sort(IEnumerable<ActionPlan> plans)
        => plans.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p.FeatureId)).ThenBy(x => x.i).Select(x => x.p).ToList();
}
