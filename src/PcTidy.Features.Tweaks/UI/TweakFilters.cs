using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Tweaks.UI;

/// <summary>Category chips on the Tweaks page (layout spec §4.1). Order = chip order.</summary>
public enum TweakChip { All, Gaming, Privacy, Interface, Network, Power, DoesntHelp, Expert }

/// <summary>Sort menu: Evidence (strongest first), Name, Changed first (set differently from Windows on this PC).</summary>
public enum TweakSort { Evidence, Name, ChangedFirst }

/// <summary>One section of the Tweaks tab: a header and its rows, after filter, search and sort.</summary>
public sealed record TweakSection(TweakHeaderRow Header, IReadOnlyList<TweakItemRow> Rows);

/// <summary>
/// Pure filter / search / sort logic for the Tweaks tab, kept out of the view model so it's tested without a window.
/// Chips: a category chip shows that group's rows except the shelf; "Doesn't help" shows the shelf; "Expert" shows Expert
/// rows (they still need the Expert switch before they can be ticked); "All" shows everything.
/// </summary>
public static class TweakFilters
{
    public static readonly IReadOnlyList<TweakChip> ChipOrder = Enum.GetValues<TweakChip>();

    public static string ChipTitle(TweakChip c) => c switch
    {
        TweakChip.All => "All",
        TweakChip.Gaming => "Gaming",
        TweakChip.Privacy => "Privacy",
        TweakChip.Interface => "Interface",
        TweakChip.Network => "Network",
        TweakChip.Power => "Power",
        TweakChip.DoesntHelp => "Doesn't help",
        _ => "Expert",
    };

    public static string SortTitle(TweakSort s) => s switch
    {
        TweakSort.Evidence => "Evidence",
        TweakSort.Name => "Name",
        _ => "Changed first",
    };

    /// <summary>The category chip of a row. Store-app removal (Debloat) sits under Interface with Explorer and the taskbar.</summary>
    public static TweakChip ChipOf(TweakDefinition t) => t.IsShelf ? TweakChip.DoesntHelp : t.Group switch
    {
        TweakGroup.Gaming => TweakChip.Gaming,
        TweakGroup.Privacy => TweakChip.Privacy,
        TweakGroup.Debloat or TweakGroup.UiExplorer => TweakChip.Interface,
        TweakGroup.Network => TweakChip.Network,
        _ => TweakChip.Power,
    };

    public static bool InChip(TweakItemRow r, TweakChip chip) => chip switch
    {
        TweakChip.All => true,
        TweakChip.Expert => r.IsExpert,
        _ => r.Chip == chip,
    };

    /// <summary>Every word of the search must appear somewhere in the row (title, effect, exact values, measured notes).</summary>
    public static bool MatchesSearch(TweakItemRow r, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var words = search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => r.SearchText.Contains(w, StringComparison.Ordinal));
    }

    public static IEnumerable<TweakItemRow> Filter(IEnumerable<TweakItemRow> rows, TweakChip chip, string? search)
        => rows.Where(r => InChip(r, chip) && MatchesSearch(r, search));

    /// <summary>Rows per chip for the current search (the counts shown on the chips).</summary>
    public static IReadOnlyDictionary<TweakChip, int> Counts(IReadOnlyList<TweakItemRow> rows, string? search)
    {
        var matching = rows.Where(r => MatchesSearch(r, search)).ToList();
        return ChipOrder.ToDictionary(c => c, c => matching.Count(r => InChip(r, c)));
    }

    /// <summary>Evidence rank: proven, situational, keep-it-on information, then the shelf (rows with A-C evidence before D).</summary>
    public static int EvidenceRank(TweakItemRow r)
    {
        var t = r.Tweak;
        if (t is null) return 9;
        if (t.IsShelf) return t.ShelfLabel == TweakDefinition.NoEvidenceLabel ? 5 : 4;
        if (t.IsKeepOn) return 3;
        return t.Verdict == TweakVerdict.Proven ? 0 : 1;
    }

    private static int BestStrength(TweakItemRow r)
        => r.Tweak is { Measured.Count: > 0 } t ? (int)t.Measured.Min(m => m.Strength) : 4;

    public static IEnumerable<TweakItemRow> Sort(IEnumerable<TweakItemRow> rows, TweakSort sort) => sort switch
    {
        TweakSort.Name => rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.CatalogIndex),
        TweakSort.ChangedFirst => rows.OrderBy(r => r.IsChanged ? 0 : 1).ThenBy(EvidenceRank).ThenBy(r => r.IsExpert ? 1 : 0).ThenBy(r => r.CatalogIndex),
        _ => rows.OrderBy(EvidenceRank).ThenBy(BestStrength).ThenBy(r => r.IsExpert ? 1 : 0).ThenBy(r => r.CatalogIndex),
    };

    public const string ShelfTitle = TweakDefinition.DoesntHelpLabel;
    public const string ShelfDescription =
        "Popular tweaks that measured no real gain, or have no test at all. Each shows its number, the hardware and the source. They can't be ticked.";

    /// <summary>
    /// The visible sections: one per page group (Gaming · Privacy · Debloat · UI and Explorer · Network · Power), then the
    /// "Doesn't help (measured)" shelf last. Empty sections are left out.
    /// </summary>
    public static IReadOnlyList<TweakSection> Sections(IReadOnlyList<TweakItemRow> rows, TweakChip chip, string? search, TweakSort sort)
    {
        var visible = Filter(rows, chip, search).ToList();
        var sections = new List<TweakSection>();
        foreach (var g in Enum.GetValues<TweakGroup>())
        {
            var inGroup = Sort(visible.Where(r => r.Tweak?.Group == g && !r.IsShelf), sort).ToList();
            if (inGroup.Count > 0)
                sections.Add(new TweakSection(new TweakHeaderRow(TweaksScanner.GroupTitle(g), TweaksScanner.GroupDescription(g), inGroup.Count), inGroup));
        }
        var shelf = Sort(visible.Where(r => r.IsShelf), sort).ToList();
        if (shelf.Count > 0) sections.Add(new TweakSection(new TweakHeaderRow(ShelfTitle, ShelfDescription, shelf.Count), shelf));
        return sections;
    }
}

/// <summary>A category chip with its count (keyboard reachable radio button on the page).</summary>
public sealed partial class ChipItem : ObservableObject
{
    private readonly Action<TweakChip> _select;

    public ChipItem(TweakChip chip, Action<TweakChip> select)
    {
        Chip = chip;
        _select = select;
    }

    public TweakChip Chip { get; }
    public string Title => TweakFilters.ChipTitle(Chip);

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    public string Label => $"{Title} {Count}";
    public string AutomationName => Chip switch
    {
        TweakChip.All => $"Show all tweaks, {Count}",
        TweakChip.DoesntHelp => $"Show tweaks that don't help, {Count}, measured numbers and sources",
        TweakChip.Expert => $"Show Expert tweaks, {Count}. They need Expert mode before they can be ticked",
        _ => $"Show {Title} tweaks, {Count}",
    };

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _select(Chip);
    }

    /// <summary>Sets the selection without calling back (the view model picks the chip).</summary>
    internal void SetSelectedQuietly(bool value)
    {
#pragma warning disable MVVMTK0034
        if (_isSelected == value) return;
        _isSelected = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>One line in a preset's "what will change" popover.</summary>
public sealed record PresetPreviewItem(string Title, PillItem Badge, string AutomationName);

/// <summary>
/// What a preset button would tick, shown before any tick changes (layout spec §4.1): the count, a short list with Safe/Check
/// badges, and what it leaves out. Built from the scan, so rows already on, not on this PC or held back aren't counted.
/// </summary>
public sealed record PresetPreview(TweakPreset Preset, string Title, string CountText, IReadOnlyList<PresetPreviewItem> Items, string MoreText, string Notes)
{
    public const int ShortListLength = 8;

    public int Count { get; init; }
    public bool HasMore => MoreText.Length > 0;
    public bool CanFill => Count > 0;
    public string AutomationName => $"{Title} preset. {CountText}. " + string.Join(". ", Items.Select(i => i.AutomationName)) + (HasMore ? ". " + MoreText : "") + ". " + Notes;

    /// <summary>The rows the preset ticks: exactly what <see cref="TweaksViewModel.ApplyPreset"/> ticks.</summary>
    public static IEnumerable<TweakItemRow> Ticks(IEnumerable<TweakItemRow> rows, TweakPreset preset)
        => rows.Where(r => r.Preset == preset && r.Item.DefaultSelected && r.IsSelectable);

    public static PresetPreview Build(TweakPresets.PresetInfo info, IReadOnlyList<TweakItemRow> rows)
    {
        var ticks = Ticks(rows, info.Preset).OrderBy(r => r.CatalogIndex).ToList();
        var members = rows.Where(r => r.Preset == info.Preset && r.Tweak?.IsOffered == true).ToList();
        var alreadyOn = members.Count(r => r.State?.Status == TweakStatus.Applied);
        var optIn = members.Count(r => !r.Item.DefaultSelected && r.IsSelectable);
        var items = ticks.Take(ShortListLength)
            .Select(r => new PresetPreviewItem(r.Title, r.Safety, $"{r.Title}, {r.Safety.Text}"))
            .ToList();
        var more = ticks.Count > ShortListLength ? $"and {ticks.Count - ShortListLength} more" : "";
        var notes = new List<string>();
        if (alreadyOn > 0) notes.Add(alreadyOn == 1 ? "1 is already on" : $"{alreadyOn} are already on");
        if (optIn > 0) notes.Add(optIn == 1 ? "1 more you can tick yourself" : $"{optIn} more you can tick yourself");
        notes.Add("Nothing changes until you preview and confirm");
        var count = ticks.Count == 0 ? "Nothing to tick: everything in this preset is already set or doesn't apply here"
            : ticks.Count == 1 ? "Ticks 1 change" : $"Ticks {ticks.Count} changes";
        return new PresetPreview(info.Preset, info.Title, count, items, more, string.Join(" · ", notes) + ".") { Count = ticks.Count };
    }
}
