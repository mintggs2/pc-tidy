using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Flow;

/// <summary>What a preselection did: rows ticked, matching rows that can't be ticked, ids not in this scan.</summary>
public sealed record PreselectResult(int Picked, int NotPickable, int NotFound)
{
    /// <summary>The note shown above the list ("3 items were picked for you…").</summary>
    public string Message
    {
        get
        {
            var parts = new List<string>
            {
                Picked == 0 ? "None of the items you asked for could be picked here." : $"{Picked:N0} item{PlanFacts.S(Picked)} picked for you. Nothing runs until you confirm.",
            };
            if (NotPickable > 0) parts.Add($"{NotPickable:N0} can't be ticked from here (shown greyed, or permanent: tick those yourself).");
            if (NotFound > 0) parts.Add($"{NotFound:N0} {(NotFound == 1 ? "wasn't" : "weren't")} found in this scan (already gone, or not here any more).");
            return string.Join(" ", parts);
        }
    }
}

/// <summary>One scanned section (feature) in a selection: its scan result and group rows.</summary>
public sealed class SectionModel
{
    public SectionModel(string featureId, string title, SymbolRegular icon, ScanResult result)
    {
        FeatureId = featureId;
        Title = title;
        Icon = icon;
        Result = result;
        Header = new SectionRow(featureId, title, icon);
        Groups = result.Groups.Where(g => g.Items.Count > 0).Select(g => new GroupRow(featureId, title, g)).ToList();
    }

    public string FeatureId { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }
    public ScanResult Result { get; }
    public SectionRow Header { get; }
    public IReadOnlyList<GroupRow> Groups { get; }
    public IEnumerable<ItemRow> CheckedItems => Groups.SelectMany(g => g.Items).Where(i => i.IsChecked);
    /// <summary>A line under the section's title ("Found at 12:41, before PC Tidy closed"); not part of any plan or run title.</summary>
    public string? Note { get => Header.Note; set => Header.Note = value; }
}

/// <summary>
/// The tick state of a whole preview (one section for a standard page, many for Optimize-everything),
/// flattened into <see cref="Rows"/> for a single virtualized list, with running totals.
/// </summary>
public sealed class SelectionModel : ObservableObject
{
    private readonly List<SectionModel> _sections = new();
    private bool _suspend;

    public SelectionModel(bool showSectionHeaders) => ShowSectionHeaders = showSectionHeaders;

    public bool ShowSectionHeaders { get; }
    public IReadOnlyList<SectionModel> Sections => _sections;
    public ObservableCollection<ListRow> Rows { get; private set; } = new();

    public int CheckedCount { get; private set; }
    public long CheckedBytes { get; private set; }
    public int TotalItems { get; private set; }
    public long TotalBytes { get; private set; }

    public event Action? Changed;

    public void Load(IEnumerable<SectionModel> sections, Func<SectionModel, GroupRow, ItemRow, bool> initialTick, Func<SectionModel, GroupRow, bool>? expandInitially = null)
    {
        _sections.Clear();
        _sections.AddRange(sections);
        _suspend = true;
        foreach (var s in _sections)
            foreach (var g in s.Groups)
            {
                g.SetWhere(i => initialTick(s, g, i));
                // Expanded state never starts true for huge groups (keeps the first render quick).
                g.IsExpanded = expandInitially?.Invoke(s, g) == true && g.Items.Count <= 500;
                g.SelectionChanged = OnGroupChanged;
                g.ExpandedChanged = OnExpandedChanged;
            }
        _suspend = false;
        TotalItems = _sections.Sum(s => s.Groups.Sum(g => g.Items.Count));
        TotalBytes = _sections.Sum(s => s.Groups.Sum(g => g.TotalBytes));
        Rebuild();
        OnGroupChanged();
    }

    /// <summary>Re-applies a tick rule to every item (presets, "Select only Safe").</summary>
    public void Retick(Func<SectionModel, GroupRow, ItemRow, bool> rule)
    {
        _suspend = true;
        foreach (var s in _sections)
            foreach (var g in s.Groups)
                g.SetWhere(i => rule(s, g, i));
        _suspend = false;
        OnGroupChanged();
    }

    public void SetAll(bool on) => Retick((_, _, i) => on);

    private void OnGroupChanged()
    {
        if (_suspend) return;
        CheckedCount = _sections.Sum(s => s.Groups.Sum(g => g.CheckedCount));
        CheckedBytes = _sections.Sum(s => s.Groups.Sum(g => g.CheckedBytes));
        foreach (var s in _sections)
        {
            // Rows ticked, then the same plan-item wording as the bottom bar ("clean 1.2 GB (1,010 items)").
            int n = s.Groups.Sum(g => g.CheckedCount);
            var c = PlanCounts.Of(s.CheckedItems.Where(i => i.IsSelectable).SelectMany(i => i.Item.Actions));
            var totals = PlanFacts.TotalsLine(c);
            s.Header.Summary = $"{n:N0} of {s.Groups.Sum(g => g.Items.Count):N0} ticked" + (c.Total > 0 ? " · " + char.ToLowerInvariant(totals[0]) + totals[1..] : "");
        }
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedBytes));
        Changed?.Invoke();
    }

    /// <summary>
    /// Expanding/collapsing edits the flat list in place (keeps the scroll position). Very large groups
    /// fall back to one rebuild because thousands of single inserts would be slower than a Reset.
    /// </summary>
    private void OnExpandedChanged(GroupRow g)
    {
        int at = Rows.IndexOf(g);
        if (at < 0 || g.Items.Count > 2000) { Rebuild(); return; }
        if (g.IsExpanded)
        {
            for (int i = 0; i < g.Items.Count; i++) Rows.Insert(at + 1 + i, g.Items[i]);
        }
        else
        {
            while (at + 1 < Rows.Count && Rows[at + 1] is ItemRow ir && ir.Group == g) Rows.RemoveAt(at + 1);
        }
    }

    /// <summary>Flattens sections → groups → (expanded) items. A new collection = one Reset, cheap even for 100k rows.</summary>
    private void Rebuild()
    {
        var list = new List<ListRow>(Math.Min(TotalItems + 64, 200_000));
        foreach (var s in _sections)
        {
            if (ShowSectionHeaders) list.Add(s.Header);
            foreach (var g in s.Groups)
            {
                list.Add(g);
                if (g.IsExpanded) list.AddRange(g.Items);
            }
        }
        Rows = new ObservableCollection<ListRow>(list);
        OnPropertyChanged(nameof(Rows));
    }

    /// <summary>
    /// Ticks exactly the rows another section asked for (<see cref="INavigationService"/>): an id matches a row's
    /// ScanItem.Id or the full SourcePath of one of its actions. Rows that can't be picked and permanent rows are
    /// never ticked this way. Groups with a picked row are opened so the user sees what was picked.
    /// </summary>
    public PreselectResult Preselect(IReadOnlyCollection<string> ids)
    {
        var wanted = new HashSet<string>(ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int notPickable = 0;
        var picked = new HashSet<ItemRow>();
        foreach (var row in _sections.SelectMany(s => s.Groups).SelectMany(g => g.Items))
        {
            var keys = Keys(row).Where(wanted.Contains).ToList();
            if (keys.Count == 0) continue;
            found.UnionWith(keys);
            if (row.IsSelectable && !row.IsPermanent) picked.Add(row);
            else notPickable++;
        }
        Retick((_, _, i) => picked.Contains(i));
        foreach (var g in _sections.SelectMany(s => s.Groups).Where(g => g.Items.Any(picked.Contains) && g.Items.Count <= 500))
            g.IsExpanded = true;
        return new PreselectResult(picked.Count, notPickable, wanted.Count - found.Count);
    }

    private static IEnumerable<string> Keys(ItemRow row)
    {
        yield return row.Item.Id;
        foreach (var a in row.Item.Actions)
            if (!string.IsNullOrEmpty(a.SourcePath)) yield return a.SourcePath;
    }

    /// <summary>Every group that had something pickable, and whether anything in it is ticked now (for learning).</summary>
    public IReadOnlyList<GroupDecision> Decisions()
        => _sections.SelectMany(s => s.Groups.Where(g => g.HasSelectable).Select(g => new GroupDecision(
                s.FeatureId, g.Group.Id, g.CheckedCount > 0, g.Title,
                (g.Items.FirstOrDefault(i => i.IsChecked) ?? g.Items.FirstOrDefault(i => i.IsSelectable))?.Item.Title)))
            .ToList();

    public IReadOnlyList<ItemRow> CheckedItems() => _sections.SelectMany(s => s.CheckedItems).ToList();

    /// <summary>All plan items of the ticked rows (disabled rows never contribute).</summary>
    public IReadOnlyList<PlanItem> CheckedActions() => CheckedItems().Where(i => i.IsSelectable).SelectMany(i => i.Item.Actions).ToList();

    /// <summary>Plan item ids whose scan item needs a restart.</summary>
    public IReadOnlySet<string> RestartIds()
        => CheckedItems().Where(i => i.Item.NeedsRestart).SelectMany(i => i.Item.Actions).Select(a => a.Id).ToHashSet();

    /// <summary>One frozen plan per section with ticks; sections share <paramref name="parentRunId"/> when given.</summary>
    public IReadOnlyList<ActionPlan> BuildPlans(string? parentRunId)
    {
        var plans = new List<ActionPlan>();
        foreach (var s in _sections)
        {
            var picked = s.CheckedItems.Select(i => i.Item).ToList();
            if (picked.Count == 0) continue;
            var plan = PlanBuilder.FromSelection(s.FeatureId, s.Title, picked, parentRunId);
            if (plan.Items.Count > 0) plans.Add(plan);
        }
        return SafeOrder.Sort(plans);
    }
}
