using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Flow;

/// <summary>
/// Base of every row in a flat, virtualized list. Groups are flattened into header + item rows so lists with
/// 100k items stay fast (one VirtualizingStackPanel, recycling) while still looking like grouped cards.
/// </summary>
public abstract class ListRow : ObservableObject
{
    private Thickness _selectionMargin;
    private CornerRadius _selectionRadius;

    /// <summary>What a screen reader says for the row.</summary>
    public abstract string AutomationName { get; }

    /// <summary>Keyboard hint a screen reader reads after the name (rows' checkboxes aren't Tab stops: Space ticks).</summary>
    public virtual string HelpText => "";

    /// <summary>Selection highlight geometry (matches the card shape the row draws).</summary>
    public Thickness SelectionMargin { get => _selectionMargin; protected set => SetProperty(ref _selectionMargin, value); }
    public CornerRadius SelectionRadius { get => _selectionRadius; protected set => SetProperty(ref _selectionRadius, value); }
}

/// <summary>Section title row (only in the combined "Optimize everything" preview).</summary>
public sealed class SectionRow : ListRow
{
    public SectionRow(string featureId, string title, SymbolRegular icon)
    {
        FeatureId = featureId;
        Title = title;
        Icon = icon;
    }

    public string FeatureId { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }
    private string _summary = "";
    public string Summary { get => _summary; set { if (SetProperty(ref _summary, value)) OnPropertyChanged(nameof(AutomationName)); } }
    private string? _note;
    /// <summary>A second line under the title, e.g. "Found at 12:41, before PC Tidy closed" for a resumed section.</summary>
    public string? Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) { OnPropertyChanged(nameof(HasNote)); OnPropertyChanged(nameof(AutomationName)); } }
    }
    public bool HasNote => !string.IsNullOrEmpty(_note);
    public override string AutomationName => $"Section {Title}. {(HasNote ? Note + ". " : "")}{Summary}";
}

/// <summary>A scan group header (card top) with a tri-state checkbox and expand/collapse.</summary>
public sealed class GroupRow : ListRow
{
    private bool _isExpanded;
    private int _checkedCount;
    private long _checkedBytes;
    internal Action<GroupRow>? ExpandedChanged;
    internal Action? SelectionChanged;

    public GroupRow(string featureId, string featureTitle, ScanGroup group)
    {
        FeatureId = featureId;
        FeatureTitle = featureTitle;
        Group = group;
        Icon = GroupIcons.For(group.Id + " " + group.Title);
        Items = group.Items.Select(i => new ItemRow(this, i)).ToList();
        if (Items.Count > 0) Items[^1].IsLast = true;
        TotalBytes = group.Items.Sum(i => i.SizeBytes);
        NeedsAdmin = group.Items.Any(i => i.NeedsAdmin);
        SelectableCount = Items.Count(i => i.IsSelectable);
        UpdateShape();
    }

    public string FeatureId { get; }
    public string FeatureTitle { get; }
    public ScanGroup Group { get; }
    public IReadOnlyList<ItemRow> Items { get; }
    public SymbolRegular Icon { get; }
    public long TotalBytes { get; }
    public bool NeedsAdmin { get; }
    public int SelectableCount { get; }
    public string Title => Group.Title;
    public string? Description => Group.Description;
    public Badge Badge => Group.Badge;
    public bool HasSelectable => SelectableCount > 0;
    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count:N0} items";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            UpdateShape();
            ExpandedChanged?.Invoke(this);
        }
    }

    /// <summary>true = all ticked, false = none, null = some.</summary>
    public bool? IsChecked
    {
        get => _checkedCount == 0 ? false : _checkedCount >= SelectableCount ? true : null;
        set => SetAll(value ?? false);
    }

    public int CheckedCount => _checkedCount;
    public long CheckedBytes => _checkedBytes;

    public void SetAll(bool on)
    {
        foreach (var item in Items) item.SetCheckedSilently(on && item.IsSelectable);
        Recount();
    }

    /// <summary>Ticks only the items a predicate allows (presets, "Select only Safe").</summary>
    public void SetWhere(Func<ItemRow, bool> predicate)
    {
        foreach (var item in Items) item.SetCheckedSilently(item.IsSelectable && predicate(item));
        Recount();
    }

    internal void Recount()
    {
        int n = 0; long b = 0;
        foreach (var i in Items)
            if (i.IsChecked) { n++; b += i.Item.SizeBytes; }
        _checkedCount = n;
        _checkedBytes = b;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedBytes));
        OnPropertyChanged(nameof(AutomationName));
        SelectionChanged?.Invoke();
    }

    private void UpdateShape()
    {
        SelectionRadius = IsExpanded ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
        SelectionMargin = IsExpanded ? new Thickness(0) : new Thickness(0, 0, 0, 6);
        OnPropertyChanged(nameof(BottomMargin));
        OnPropertyChanged(nameof(CardRadius));
        OnPropertyChanged(nameof(CardBorder));
    }

    public Thickness BottomMargin => IsExpanded ? new Thickness(0) : new Thickness(0, 0, 0, 6);
    public CornerRadius CardRadius => IsExpanded ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
    public Thickness CardBorder => IsExpanded ? new Thickness(1, 1, 1, 0) : new Thickness(1);

    /// <summary>Nothing in the group can be picked (placebo tweaks, protected things): its pill says so instead of a safety badge.</summary>
    public bool IsInfoOnly => !HasSelectable;

    public override string AutomationName
        => $"{Title}, {CountText}" + (TotalBytes > 0 ? $", {Sizes.Format(TotalBytes)}" : "") +
           (IsInfoOnly ? ", for information only, nothing here can be picked" : $", {BadgeText(Badge)}, " +
               (IsChecked == true ? "all ticked" : IsChecked == false ? "not ticked" : $"{_checkedCount} ticked")) +
           (NeedsAdmin ? ", some items need admin: Windows asks once for the whole run" : "") +
           (IsExpanded ? ", expanded" : ", collapsed");

    public override string HelpText => IsInfoOnly
        ? "Press Enter to show or hide the items."
        : "Press Space to tick or untick the whole group, Enter to show or hide its items.";

    internal static string BadgeText(Badge b) => b switch { Badge.Safe => "safe", Badge.Check => "check", _ => "permanent, can't be undone" };
}

/// <summary>One scan item row with checkbox.</summary>
public sealed class ItemRow : ListRow
{
    private bool _isChecked;
    private bool _isLast;

    public ItemRow(GroupRow group, ScanItem item)
    {
        Group = group;
        Item = item;
        IsSelectable = item.DisabledReason is null && item.Actions.Count > 0;
    }

    public GroupRow Group { get; }
    public ScanItem Item { get; }
    /// <summary>False for information-only rows and rows with a DisabledReason (greyed, cannot be ticked).</summary>
    public bool IsSelectable { get; }
    public bool IsDisabled => Item.DisabledReason is not null;
    public bool IsPermanent => Item.Badge == Badge.Permanent || Item.Actions.Any(a => !a.Reversible);
    /// <summary>Second line: the disabled reason (why it can't be picked) or the "why it's safe" text.</summary>
    public string SecondLine => Item.DisabledReason ?? Item.Reason;
    /// <summary>Short note after the title: "Close Chrome first" (a blocker), else the item's current state ("Never played").</summary>
    public string? Note => NoteIsBlocker ? "Close " + string.Join(", ", Item.RequiresClosedApps!) + " first" : Item.CurrentState;
    /// <summary>True when the note is something the user must do first (amber); a plain fact is secondary text.</summary>
    public bool NoteIsBlocker => Item.RequiresClosedApps is { Count: > 0 };

    public bool IsLast
    {
        get => _isLast;
        internal set
        {
            _isLast = value;
            SelectionRadius = value ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0);
            SelectionMargin = value ? new Thickness(0, 0, 0, 6) : new Thickness(0);
        }
    }

    public Thickness BottomMargin => IsLast ? new Thickness(0, 0, 0, 6) : new Thickness(0);
    public CornerRadius CardRadius => IsLast ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0);
    public Thickness CardBorder => IsLast ? new Thickness(1, 1, 1, 1) : new Thickness(1, 1, 1, 0);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!IsSelectable) value = false;
            if (!SetProperty(ref _isChecked, value)) return;
            OnPropertyChanged(nameof(AutomationName));
            Group.Recount();
        }
    }

    internal void SetCheckedSilently(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(AutomationName));
    }

    public override string AutomationName
        => Item.Title + (Item.SizeBytes > 0 ? $", {Sizes.Format(Item.SizeBytes)}" : "")
           + (IsDisabled ? ", not offered: " + Item.DisabledReason
               : $", {GroupRow.BadgeText(Item.Badge)}"
                 + (Item.NeedsAdmin ? ", needs admin: Windows asks once for the whole run" : "")
                 + (!IsSelectable ? ", for information only" : IsChecked ? ", ticked" : ", not ticked"));

    public override string HelpText => IsSelectable ? "Press Space to tick or untick. Left goes back to the group." : "";
}

/// <summary>Picks a friendly Fluent icon from a group's id/title keywords.</summary>
public static class GroupIcons
{
    // Most specific words first: "downloads-pictures" is about pictures, "app-cache" about apps.
    private static readonly (string Key, SymbolRegular Icon)[] Map =
    {
        ("not-cleaned", SymbolRegular.Prohibited24), ("never", SymbolRegular.Prohibited24), ("placebo", SymbolRegular.Prohibited24),
        // Notifications' three parts share the id prefix "notifications.", so their own words come first.
        ("windows tips", SymbolRegular.Lightbulb24), ("suggestions", SymbolRegular.Lightbulb24),
        ("ai features", SymbolRegular.Sparkle24), ("copilot", SymbolRegular.Sparkle24), ("notification", SymbolRegular.Alert24),
        ("doesn't help", SymbolRegular.Prohibited24), ("temp", SymbolRegular.DocumentDismiss24), ("browser", SymbolRegular.Globe24),
        ("crash", SymbolRegular.ErrorCircle24), ("error", SymbolRegular.ErrorCircle24), ("shader", SymbolRegular.Games24),
        ("clip", SymbolRegular.Video24), ("unplayed", SymbolRegular.Games24), ("game", SymbolRegular.Games24),
        ("picture", SymbolRegular.Image24), ("photo", SymbolRegular.Image24), ("video", SymbolRegular.Video24),
        ("music", SymbolRegular.MusicNote224), ("invoice", SymbolRegular.Receipt24), ("document", SymbolRegular.Document24),
        ("archive", SymbolRegular.FolderZip24), ("installer", SymbolRegular.BoxArrowUp24), ("download", SymbolRegular.ArrowDownload24),
        ("recycle", SymbolRegular.Delete24), ("windows.old", SymbolRegular.Window24), ("update", SymbolRegular.ArrowSync24),
        ("startup", SymbolRegular.Rocket24), ("privacy", SymbolRegular.ShieldKeyhole24), ("explorer", SymbolRegular.FolderOpen24),
        ("gaming", SymbolRegular.Games24), ("tweak", SymbolRegular.WrenchScrewdriver24), ("log", SymbolRegular.TextBulletListSquare24),
        ("app", SymbolRegular.Apps24), ("cache", SymbolRegular.Globe24), ("organize", SymbolRegular.FolderArrowRight24),
    };

    public static SymbolRegular For(string text)
    {
        var t = text.ToLowerInvariant();
        foreach (var (key, icon) in Map)
            if (t.Contains(key)) return icon;
        return SymbolRegular.Box24;
    }
}
