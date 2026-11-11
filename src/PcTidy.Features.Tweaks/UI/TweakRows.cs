using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Tweaks.UI;

/// <summary>A section header in the flat (virtualized) list.</summary>
public sealed class TweakHeaderRow(string title, string description, int count)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string CountText { get; } = count == 1 ? "1 item" : $"{count} items";
    public string AutomationName => $"{Title}, {CountText}. {Description}";
}

/// <summary>A source or Settings link shown under a row (opened through the shell, never directly).</summary>
public sealed record LinkItem(string Label, string Target, string AutomationName);

/// <summary>A small coloured pill (status, verdict, admin/restart tags).</summary>
public sealed record PillItem(string Text, PillTone Tone);

/// <summary>
/// One tweak or bad-tweak finding as a checkable row: three-state status pill, verdict pill, tags,
/// and an expandable details area with the exact registry paths and the source links.
/// </summary>
public sealed partial class TweakItemRow : ObservableObject
{
    private readonly Action<string> _openLink;
    private readonly Action _changed;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded;

    private TweakItemRow(ScanItem item, string sectionId, Action<string> openLink, Action changed)
    {
        Item = item;
        SectionId = sectionId;
        _openLink = openLink;
        _changed = changed;
        IsSelectable = item.DisabledReason is null && item.Actions.Count > 0;
        DetailsText = string.Join("\n", (item.Details ?? new Dictionary<string, string>())
            .Where(kv => kv.Key is not ("Source" or "Fix")) // the actions are listed under "What happens"
            .Select(kv => kv.Value.Contains('\n') ? $"{kv.Key}:\n  {kv.Value.Replace("\n", "\n  ")}" : $"{kv.Key}: {kv.Value}"));
        Sources = (item.Details?.GetValueOrDefault("Source") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => new LinkItem(LinkLabel(u), u, "Open source: " + LinkLabel(u))).ToList();
        WhatHappens = item.Actions.Count == 0 ? "Information only. Nothing here changes."
            : string.Join("\n", item.Actions.Select(a => "• " + a.Description + (a.Reversible ? "" : " (can't be undone by PC Tidy)")));
    }

    public ScanItem Item { get; }
    /// <summary>"tweaks" or "tweaks.badtweaks": which plan this row's actions go into.</summary>
    public string SectionId { get; }
    public TweakDefinition? Tweak { get; private init; }
    public TweakPreset Preset => Tweak?.Preset ?? TweakPreset.None;
    public bool IsSelectable { get; }
    public string Title => Item.Title;
    public string SecondLine => Item.DisabledReason ?? Item.Reason;
    public bool IsDisabled => Item.DisabledReason is not null;
    /// <summary>
    /// Information-only rows (nothing to apply and no reason it's held back, e.g. "Memory integrity: keep it on") show no tick box:
    /// the space is kept so the titles still line up.
    /// </summary>
    public System.Windows.Visibility CheckBoxVisibility => Item.Actions.Count == 0 && !IsDisabled ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
    public PillItem Status { get; private init; } = new("", PillTone.Neutral);
    public PillItem Verdict { get; private init; } = new("", PillTone.Neutral);
    public IReadOnlyList<PillItem> Tags { get; private init; } = Array.Empty<PillItem>();
    public string DetailsText { get; }
    public string WhatHappens { get; }
    public IReadOnlyList<LinkItem> Sources { get; }
    public LinkItem? SettingsLink { get; private init; }
    public bool HasSettingsLink => SettingsLink is not null;

    // ------------------------------------------------------------------ state-aware card (layout spec §4.1)

    /// <summary>The live state read by the scan (null for finder rows).</summary>
    public TweakState? State { get; private init; }
    /// <summary>"Now: On" / "Now: Off" / "Now: Other value", read back from this PC. Empty for information rows.</summary>
    public string NowText { get; private init; } = "";
    public PillTone NowTone { get; private init; } = PillTone.Neutral;
    public bool HasNow => NowText.Length > 0;
    /// <summary>Proven / Situational / Doesn't help (measured) / No evidence it helps / Keep it on.</summary>
    public PillItem Evidence { get; private init; } = new("", PillTone.Neutral);
    /// <summary>Safe / Check / Permanent, or "Info only" when there's nothing to apply.</summary>
    public PillItem Safety { get; private init; } = new("", PillTone.Neutral);
    /// <summary>One line: what this changes (the exact value, or the steps for information rows).</summary>
    public string WhatChanges { get; private init; } = "";
    /// <summary>Each measured note: number · hardware · source, date · evidence strength.</summary>
    public IReadOnlyList<string> MeasuredLines { get; private init; } = Array.Empty<string>();
    public string MeasuredSummary => MeasuredLines.Count == 0 ? "" : "Measured: " + MeasuredLines[0];
    public bool HasMeasured => MeasuredLines.Count > 0;
    /// <summary>The category chip this row belongs to (shelf rows: Doesn't help).</summary>
    public TweakChip Chip { get; private init; } = TweakChip.All;
    public bool IsShelf => Tweak?.IsShelf == true;
    public bool IsExpert => Tweak?.IsExpertOnly == true;
    /// <summary>Set differently from the Windows default on this PC right now (applied, or another value).</summary>
    public bool IsChanged => State?.Changes.Any(ChangedFromDefault) == true;

    /// <summary>Another value than a fresh Windows has (an applied tweak whose value is the Windows default doesn't count).</summary>
    private static bool ChangedFromDefault(ChangeState c) => c.Status switch
    {
        TweakStatus.Differs => true,
        TweakStatus.Applied => c.Change switch
        {
            RegistryValueChange r => r.WindowsDefault is null || !RegData.SameValue(r.Kind, r.Data, r.WindowsDefault),
            ServiceStartChange s => s.Target != s.WindowsDefault,
            _ => true,
        },
        _ => false,
    };
    /// <summary>Position in the catalog (stable tie-break for sorting).</summary>
    public int CatalogIndex { get; private init; }
    /// <summary>Lower-case text the search box matches against: title, effect, exact values, measured notes, id.</summary>
    public string SearchText { get; private init; } = "";

    public string CardAutomationName
        => $"{Title}. {Evidence.Text}. {Safety.Text}." + (HasNow ? $" {NowText}." : "") + (HasMeasured ? " " + MeasuredSummary + "." : "")
           + (IsDisabled ? " Can't be ticked: " + Item.DisabledReason : IsChecked ? " Ticked to change." : IsSelectable ? " Not ticked." : " Information only.");

    public string DetailsButtonName => "Show or hide details for " + Title;

    public string AutomationName
        => $"{Title}. {Status.Text}. {Verdict.Text}." + (IsDisabled ? " Not offered: " + Item.DisabledReason : IsChecked ? " Ticked." : IsSelectable ? " Not ticked." : " Information only.");

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !IsSelectable) { IsChecked = false; return; }
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(CardAutomationName));
        _changed();
    }

    [RelayCommand] private void ToggleDetails() => IsExpanded = !IsExpanded;

    [RelayCommand] private void OpenLink(LinkItem? link)
    {
        if (link is not null) _openLink(link.Target);
    }

    /// <summary>Sets the tick without raising the selection-changed callback (presets tick many rows at once).</summary>
    internal void SetCheckedQuietly(bool value)
    {
        var v = value && IsSelectable;
        if (v == IsChecked) return;
#pragma warning disable MVVMTK0034 // writing the field on purpose: skip the per-row callback, the caller recounts once
        _isChecked = v;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(CardAutomationName));
    }

    // ------------------------------------------------------------------ factories

    public static TweakItemRow ForTweak(TweakRow row, Action<string> openLink, Action changed) => ForTweak(row, openLink, changed, IndexOf(row.Tweak));

    private static int IndexOf(TweakDefinition t)
    {
        var all = TweakCatalog.All;
        for (var i = 0; i < all.Count; i++) if (all[i].Id == t.Id) return i;
        return all.Count;
    }

    public static TweakItemRow ForTweak(TweakRow row, Action<string> openLink, Action changed, int catalogIndex)
    {
        var t = row.Tweak;
        var tags = new List<PillItem>();
        if (row.Item.NeedsAdmin) tags.Add(new("Admin", PillTone.Neutral));
        if (t.Flags.HasFlag(TweakFlags.Restart)) tags.Add(new("Needs a restart", PillTone.Neutral));
        if (t.Flags.HasFlag(TweakFlags.SignOut)) tags.Add(new("Needs sign-out", PillTone.Neutral));
        if (t.Flags.HasFlag(TweakFlags.RestartExplorer)) tags.Add(new("Restarts Explorer", PillTone.Neutral));
        if (t.IsManagedPolicy) tags.Add(new("\"Managed\" label", PillTone.Info));
        if (t.IsExpertOnly) tags.Add(new("Expert", PillTone.Critical));
        if (!t.Reversible) tags.Add(new("Reinstall to undo", PillTone.Critical));
        if (t.Risk != TweakRisk.Low && !TweakRowBuilder.IsKeepOn(t)) tags.Add(new("Risk: " + t.Risk.ToString().ToLowerInvariant(), t.Risk == TweakRisk.Medium ? PillTone.Caution : PillTone.Critical));
        if (t.IsShelf) tags.Clear(); // never applied, so admin or restart tags would only confuse

        // A greyed row never shows a green "Available": it can't be picked, so say why in neutral words.
        var status = row.State.Status switch
        {
            _ when TweakRowBuilder.IsKeepOn(t) => new PillItem("Info", PillTone.Neutral),
            _ when t.Verdict == TweakVerdict.Placebo => new PillItem("Doesn't help", PillTone.Neutral),
            TweakStatus.Available when row.Item.DisabledReason is not null => new PillItem(t.IsExpertOnly ? "Expert mode only" : "Not offered", PillTone.Neutral),
            TweakStatus.Applied => new PillItem("Already applied", PillTone.Success),
            TweakStatus.Differs => new PillItem("Differs", PillTone.Caution),
            TweakStatus.Available => new PillItem("Available", PillTone.Accent),
            TweakStatus.Blocked => new PillItem("Left alone", PillTone.Neutral),
            TweakStatus.Unknown => new PillItem("Couldn't check", PillTone.Neutral),
            _ => new PillItem("Info", PillTone.Neutral),
        };
        var verdict = TweakRowBuilder.IsKeepOn(t) ? new PillItem("Keep it on", PillTone.Neutral) : t.Verdict switch
        {
            TweakVerdict.Proven => new PillItem("Proven", PillTone.Success),
            TweakVerdict.Situational => new PillItem("Situational", PillTone.Caution),
            _ => new PillItem(TweakRowBuilder.ShelfPrefix(t), PillTone.Neutral),
        };
        var now = Now(t, row.State);
        var settings = t.Changes.OfType<GuidanceChange>().FirstOrDefault(g => g.SettingsUri is not null) is { } g2
            ? new LinkItem("Open the Settings page", g2.SettingsUri!, "Open the Windows Settings page for " + t.Title) : null;
        return new TweakItemRow(row.Item, TweakPlanFeatures.Tweaks, openLink, changed)
        {
            Tweak = t, Status = status, Verdict = verdict, Tags = tags, SettingsLink = settings,
            State = row.State, NowText = now.Text, NowTone = now.Tone,
            Evidence = TweakRowBuilder.IsKeepOn(t) ? new PillItem("Keep it on", PillTone.Neutral)
                : t.IsShelf ? new PillItem(t.ShelfLabel, PillTone.Neutral) : verdict,
            Safety = SafetyPill(t, row.Item),
            WhatChanges = (t.IsShelf ? "Guides say: " : "") + WhatChangesLine(t),
            MeasuredLines = t.Measured.Select(m => m.Describe()).ToList(),
            Chip = TweakFilters.ChipOf(t),
            CatalogIndex = catalogIndex,
            SearchText = SearchTextOf(t, row.Item),
        };
    }

    /// <summary>"Now: On" when this change is already in effect, "Now: Off" at the Windows default, read back from this PC.</summary>
    public static (string Text, PillTone Tone) Now(TweakDefinition t, TweakState state)
    {
        if (TweakRowBuilder.IsKeepOn(t)) return ("", PillTone.Neutral);
        var appx = t.ActionableChanges.Any(c => c is AppxRemoveChange);
        return state.Status switch
        {
            TweakStatus.Applied when t.IsShelf => ("Now: Set on this PC", PillTone.Caution),
            TweakStatus.Available when t.IsShelf => ("Now: Not set", PillTone.Neutral),
            TweakStatus.Applied => ("Now: On", PillTone.Success),
            TweakStatus.Available when appx => ("Now: Installed", PillTone.Neutral),
            TweakStatus.Available => ("Now: Off", PillTone.Neutral),
            TweakStatus.Differs => ("Now: Other value (" + string.Join(", ", state.Changes.Where(c => c.Status == TweakStatus.Differs).Select(c => c.Now)) + ")", PillTone.Caution),
            TweakStatus.Blocked => ("Now: Left alone (unexpected type)", PillTone.Neutral),
            TweakStatus.Unknown => ("Now: Couldn't read", PillTone.Neutral),
            _ => ("", PillTone.Neutral),
        };
    }

    private static PillItem SafetyPill(TweakDefinition t, ScanItem item)
    {
        if (t.IsShelf || TweakRowBuilder.IsKeepOn(t) || !t.HasActions) return new PillItem("Info only", PillTone.Neutral);
        return new PillItem(BadgePill.LabelFor(item.Badge), item.Badge switch { Badge.Safe => PillTone.Success, Badge.Check => PillTone.Caution, _ => PillTone.Critical });
    }

    /// <summary>The one-line "what changes": the first exact change (plus how many more), or the steps for information rows.</summary>
    private static string WhatChangesLine(TweakDefinition t)
    {
        var first = t.Changes.FirstOrDefault();
        if (first is null) return "";
        var text = first switch
        {
            RegistryValueChange r => $"{r.Label} = {RegData.Show(r.Kind, r.Data)}",
            RegistryTokenChange tok => $"{tok.Label}: {tok.Token}={tok.Value}",
            ServiceStartChange s => $"Service {s.ServiceName} → {RegistryNames.Friendly(s.Target)}",
            AppxRemoveChange a => $"Removes the Store app {a.PackageName} for you",
            OptionalFeatureChange f => $"Turns off the Windows feature {f.FeatureName}",
            _ => first.Describe(),
        };
        var more = t.Changes.Count - 1;
        return more > 0 ? $"{text} (+{more} more)" : text;
    }

    private static string SearchTextOf(TweakDefinition t, ScanItem item)
        => string.Join(" ", new[] { t.Id, t.Title, t.Effect, t.Caveat ?? "", item.Reason ?? "", item.Detail ?? "", t.IsShelf ? t.ShelfLabel : "" }
                .Concat(t.Changes.Select(c => c.Describe()))
                .Concat(t.Measured.Select(m => m.Describe())))
            .ToLowerInvariant();

    public static TweakItemRow ForFinding(ScanItem item, Action<string> openLink, Action changed)
    {
        var check = BadTweakCatalog.Find(item.Id.StartsWith("badtweaks.", StringComparison.Ordinal) ? item.Id["badtweaks.".Length..] : item.Id);
        var severity = check?.Severity ?? BadTweakSeverity.Low;
        var tags = new List<PillItem>();
        if (item.NeedsAdmin) tags.Add(new("Admin", PillTone.Neutral));
        if (item.NeedsRestart) tags.Add(new("Needs a restart", PillTone.Neutral));
        if (check?.Flags.HasFlag(BadTweakFlags.BitLocker) == true) tags.Add(new("BitLocker: have your key", PillTone.Critical));
        var guidance = check?.Signals.SelectMany(s => s.Fixes).OfType<GuidanceFix>().FirstOrDefault(f => f.SettingsUri is not null);
        return new TweakItemRow(item, TweakPlanFeatures.Finder, openLink, changed)
        {
            Status = new PillItem(item.CurrentState ?? "Found", severity == BadTweakSeverity.High ? PillTone.Critical : PillTone.Caution),
            Verdict = new PillItem(severity switch { BadTweakSeverity.High => "Important", BadTweakSeverity.Medium => "Worth fixing", _ => "Minor" },
                severity switch { BadTweakSeverity.High => PillTone.Critical, BadTweakSeverity.Medium => PillTone.Caution, _ => PillTone.Neutral }),
            Tags = tags,
            SettingsLink = guidance is null ? null
                : guidance.SettingsUri!.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)
                    ? new LinkItem("Open Windows Security", guidance.SettingsUri!, "Open Windows Security for " + item.Title)
                    : new LinkItem("Open the Settings page", guidance.SettingsUri!, "Open the Windows Settings page for " + item.Title),
        };
    }

    /// <summary>"learn.microsoft.com · bcdedit set" style labels, so two links to the same site can be told apart.</summary>
    private static string LinkLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        var last = u.Segments.Select(s => s.Trim('/')).LastOrDefault(s => s.Length > 0 && !s.StartsWith("t=", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(last)) return host;
        var words = Uri.UnescapeDataString(last).Replace('-', ' ').Replace('_', ' ');
        words = System.Text.RegularExpressions.Regex.Replace(words, @"\s[0-9a-f]{8}( [0-9a-f]{4}){3} [0-9a-f]{12}$", ""); // support.microsoft.com ids
        if (words.Length > 42) words = words[..40].TrimEnd() + "…";
        return $"{host} · {words}";
    }
}

/// <summary>Feature ids the page's plans use.</summary>
public static class TweakPlanFeatures
{
    public const string Tweaks = Planning.TweakPlanItems.TweaksFeature;
    public const string Finder = Planning.TweakPlanItems.FinderFeature;
}
