using System.Globalization;
using PcTidy.Features.Tweaks.Planning;
using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.Tweaks;

/// <summary>One tweak as shown: its definition, its live state and the ScanItem (row) built from both.</summary>
public sealed record TweakRow(TweakDefinition Tweak, TweakState State, ScanItem Item)
{
    public bool IsSelectable => Item.DisabledReason is null && Item.Actions.Count > 0;
}

/// <summary>Turns a definition + live state into a row. All user-visible wording for tweak rows lives here.</summary>
public static class TweakRowBuilder
{
    public const string ManagedLabel = "Windows will say \"Managed by your organization\" while this is on.";

    /// <summary>
    /// True for a "keep it on" security row (<see cref="TweakFlags.KeepOn"/>): information only, never a tick box.
    /// The catalog writes their title and text the "keep it on" way round, so nothing is reworded here.
    /// </summary>
    public static bool IsKeepOn(TweakDefinition t) => t.IsKeepOn;

    /// <param name="expertAllowed">Expert mode is on and this isn't an Optimize-all run.</param>
    public static TweakRow Build(TweakDefinition t, TweakState state, TweakEnvironment env, bool expertAllowed)
    {
        var disabled = DisabledReason(t, state, expertAllowed);
        var actions = disabled is null ? Actions(t, state, env.Registry.Redirect) : Array.Empty<PlanItem>();
        var badge = !t.Reversible ? Badge.Permanent
            : t.Verdict == TweakVerdict.Proven && t.Risk == TweakRisk.Low && !t.IsExpertOnly ? Badge.Safe
            : Badge.Check;
        var needsAdmin = t.Flags.HasFlag(TweakFlags.Admin) || t.ActionableChanges.Any(NeedsAdmin);
        var defaultTick = badge == Badge.Safe && t.PresetDefault && t.Preset != TweakPreset.None && t.Verdict == TweakVerdict.Proven
                          && disabled is null && actions.Count > 0;

        var item = new ScanItem
        {
            Id = "tweaks." + t.Id,
            Title = t.Title,
            Detail = DetailLine(t),
            Reason = t.IsManagedPolicy ? $"{t.Effect} {ManagedLabel}" : t.Effect,
            Badge = badge,
            DefaultSelected = defaultTick,
            NeedsAdmin = needsAdmin,
            NeedsRestart = t.Flags.HasFlag(TweakFlags.Restart),
            CurrentState = StateText(t, state),
            Details = Details(t, state, needsAdmin),
            Actions = actions,
            DisabledReason = disabled,
        };
        return new TweakRow(t, state, item);
    }

    private static bool NeedsAdmin(TweakChange c) => c switch
    {
        RegistryValueChange r => TweakPlanItems.NeedsAdmin(r.Hive, r.KeyPath),
        RegistryTokenChange tok => TweakPlanItems.NeedsAdmin(tok.Hive, tok.KeyPath),
        ServiceStartChange or OptionalFeatureChange => true,
        _ => false,
    };

    private static string? DisabledReason(TweakDefinition t, TweakState state, bool expertAllowed)
    {
        if (t.Verdict == TweakVerdict.Placebo) return "Doesn't help: " + (t.Caveat ?? t.Effect);
        if (!t.IsOffered) return "Never offered: it does more harm than good.";
        if (state.Status == TweakStatus.Blocked) return state.Problem;
        if (state.Status == TweakStatus.Applied) return "Already set on this PC.";
        if (state.Status == TweakStatus.Unknown) return "PC Tidy couldn't read this setting on your PC, so it isn't offered.";
        if (t.IsExpertOnly && t.HasActions && !expertAllowed) return "Expert mode only. " + (t.Caveat ?? "");
        if (t.IsExpertOnly && !t.HasActions) return t.Caveat;
        return null;
    }

    private static IReadOnlyList<PlanItem> Actions(TweakDefinition t, TweakState state, RegistryRedirect redirect)
    {
        var list = new List<PlanItem>();
        foreach (var c in state.Pending)
        {
            switch (c.Change)
            {
                case RegistryValueChange r:
                    list.Add(TweakPlanItems.RegistrySet(TweakPlanItems.TweaksFeature, redirect, r.Hive, r.KeyPath, r.ValueName, r.Kind, r.Data,
                        $"{t.Title}: set {r.Label} to {RegData.Show(r.Kind, r.Data)}"));
                    break;
                case RegistryTokenChange tok:
                    list.Add(TweakPlanItems.RegistrySet(TweakPlanItems.TweaksFeature, redirect, tok.Hive, tok.KeyPath, tok.ValueName, RegistryKind.String, c.PlannedData ?? "",
                        $"{t.Title}: add {tok.Token}={tok.Value} to {tok.Label} (other settings kept)"));
                    break;
                case ServiceStartChange s:
                    list.Add(TweakPlanItems.ServiceStart(TweakPlanItems.TweaksFeature, s.ServiceName, s.Target,
                        $"{t.Title}: set the '{s.ServiceName}' service to {RegistryNames.Friendly(s.Target)}"));
                    break;
                case AppxRemoveChange a when c.PlannedData is { } full:
                    list.Add(TweakPlanItems.RemoveApp(TweakPlanItems.TweaksFeature, full, $"{t.Title} (reinstall from the Microsoft Store to undo)"));
                    break;
                case OptionalFeatureChange f:
                    list.Add(TweakPlanItems.RemoveFeature(TweakPlanItems.TweaksFeature, f.FeatureName, $"{t.Title}: turn off the '{f.FeatureName}' Windows feature"));
                    break;
            }
        }
        return list;
    }

    private static string? DetailLine(TweakDefinition t) => t.Changes.FirstOrDefault() switch
    {
        RegistryValueChange r => r.Label,
        RegistryTokenChange tok => tok.Label,
        ServiceStartChange s => $"Service: {s.ServiceName}",
        AppxRemoveChange a => $"Store app: {a.PackageName}",
        OptionalFeatureChange f => $"Windows feature: {f.FeatureName}",
        GuidanceChange g => g.Steps,
        CommandChange cmd => cmd.CommandLine,
        PowerPlanChange p => $"Power plan: {p.Name}",
        _ => null,
    };

    public static string StateText(TweakDefinition t, TweakState state) => state.Status switch
    {
        TweakStatus.Applied when t.Verdict == TweakVerdict.Placebo => "Set on this PC (doesn't help)",
        TweakStatus.Differs when t.Verdict == TweakVerdict.Placebo => "Changed on this PC (doesn't help)",
        TweakStatus.Applied => "Already applied",
        TweakStatus.Differs => "Differs: " + string.Join(", ", state.Changes.Where(c => c.Status == TweakStatus.Differs).Select(c => c.Now)),
        TweakStatus.Blocked => "Left alone (unexpected type)",
        TweakStatus.NotPresent => "Not on this PC",
        TweakStatus.Unknown => "Couldn't be checked",
        TweakStatus.Information => t.Verdict == TweakVerdict.Placebo ? "Doesn't help" : "Information",
        _ => t.Verdict == TweakVerdict.Placebo ? "Doesn't help" : "Available",
    };

    private static IReadOnlyDictionary<string, string> Details(TweakDefinition t, TweakState state, bool needsAdmin)
    {
        var d = new Dictionary<string, string>
        {
            ["Verdict"] = t.Verdict switch
            {
                TweakVerdict.Proven => "Proven: helps measurably",
                TweakVerdict.Situational => "Situational: only helps on some PCs",
                TweakVerdict.Placebo => "Placebo: no measurable effect",
                _ => "Harmful",
            },
            ["Risk"] = t.Risk.ToString(),
        };
        if (IsKeepOn(t)) { d["Verdict"] = "Keep it on: a security feature PC Tidy never changes"; d.Remove("Risk"); }
        var needs = new List<string>();
        if (needsAdmin) needs.Add("admin");
        if (t.Flags.HasFlag(TweakFlags.Restart)) needs.Add("a restart");
        if (t.Flags.HasFlag(TweakFlags.SignOut)) needs.Add("signing out and in");
        if (t.Flags.HasFlag(TweakFlags.RestartExplorer)) needs.Add("restarting Explorer (or signing out)");
        if (t.Flags.HasFlag(TweakFlags.RestartGame)) needs.Add("restarting the game");
        if (needs.Count > 0) d["Needs"] = string.Join(", ", needs);

        var n = 0;
        foreach (var c in state.Changes)
        {
            var label = state.Changes.Count == 1 ? "Change" : "Change " + (++n).ToString(CultureInfo.InvariantCulture);
            d[label] = c.Change switch
            {
                RegistryValueChange r => $"{r.Label}\nNow: {c.Now} · Windows default: {(r.WindowsDefault is null ? "not set" : RegData.Show(r.Kind, r.WindowsDefault))} · PC Tidy sets: {RegData.Show(r.Kind, r.Data)}",
                RegistryTokenChange tok => $"{tok.Label}\nNow: {c.Now} · PC Tidy adds: {tok.Token}={tok.Value}",
                ServiceStartChange s => $"Service '{s.ServiceName}'\nNow: {c.Now} · Windows default: {RegistryNames.Friendly(s.WindowsDefault)} · PC Tidy sets: {RegistryNames.Friendly(s.Target)}",
                _ => c.Change.Describe() + (c.Now.Length > 0 ? $"\nNow: {c.Now}" : ""),
            };
        }
        if (t.IsManagedPolicy) d["Managed"] = ManagedLabel;
        if (t.Caveat is { } caveat && t.IsOffered) d["Good to know"] = caveat;
        d["Undo"] = t.Undo;
        d["Source"] = string.Join("\n", t.Sources);
        return d;
    }
}
