using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>How careful a whole section is (shown as a pill on its page). Expert sections are hidden unless Expert mode is on.</summary>
public enum ModuleRisk { Safe, Check, Expert }

/// <summary>
/// Optional: a feature module can implement this to declare its risk class.
/// (Local stand-in until IFeatureModule gets a RiskClass property; see the App shell vault page.)
/// </summary>
public interface IModuleRiskInfo
{
    ModuleRisk Risk { get; }
}

/// <summary>
/// Optional: a feature module (or scanner) can implement this to say, in its own words, what happens to ticked items
/// ("Ticked files are sorted into folders inside Downloads. Undo puts each one back."). Shown on the preview banner.
/// Without it the banner is worked out from the scan's actions (<see cref="FlowCopy.PreviewNote"/>).
/// </summary>
public interface IModuleFlowText
{
    string? PreviewNote { get; }
}

/// <summary>Plain-English page copy worked out from what a scan actually offers (never a generic sentence).</summary>
public static class FlowCopy
{
    /// <summary>The "Nothing is changed yet." banner text for this scan's actions.</summary>
    public static string PreviewNote(IEnumerable<ScanItem> items, int quarantineDays)
    {
        var c = PlanCounts.Of(items.Where(i => i.DisabledReason is null).SelectMany(i => i.Actions));
        var parts = new List<string>();
        if (c.Quarantine > 0) parts.Add($"ticked items wait in quarantine for {quarantineDays} days");
        if (c.Moves > 0) parts.Add("ticked files are sorted into folders");
        if (c.Startup > 0) parts.Add("ticked apps stop starting with Windows but stay installed");
        if (c.Settings > 0) parts.Add("ticked settings keep a copy of their old value");
        if (parts.Count == 0 && c.Permanent == 0) return "This list is for information: nothing here is changed by PC Tidy.";
        var text = parts.Count == 0 ? "" : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ". ";
        text += c.Permanent > 0 ? "Everything else can be undone; Permanent items always ask again." : "Everything can be undone from History.";
        return text;
    }

    /// <summary>
    /// Page pill after a scan, from the rows themselves, so it never contradicts their badges.
    /// Before a scan the module's declared risk is shown instead.
    /// </summary>
    public static (string Text, Controls.PillTone Tone)? ResultPill(IEnumerable<ScanItem> items, ModuleRisk? declared)
    {
        if (declared == ModuleRisk.Expert) return ("Expert", Controls.PillTone.Critical);
        var offered = items.Where(i => i.DisabledReason is null && i.Actions.Count > 0).ToList();
        if (offered.Count == 0) return null;
        // Calm tone: the rows themselves carry the red "Permanent" badge, and none of them is ever pre-ticked.
        int permanent = offered.Count(i => i.Badge == Badge.Permanent || i.Actions.Any(a => !a.Reversible));
        if (permanent > 0) return ($"{permanent} can't be undone", Controls.PillTone.Caution);
        if (offered.Any(i => i.Badge == Badge.Check)) return ("Check each item", Controls.PillTone.Caution);
        return ("All Safe", Controls.PillTone.Success);
    }
}
