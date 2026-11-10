using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Photos.UI;

/// <summary>One change in the list: a tick box, a thumbnail or icon, what happens and why.</summary>
public sealed partial class ChangeRow : ObservableObject
{
    private readonly Action<ChangeRow> _changed;

    public ChangeRow(ScanItem item, Action<ChangeRow> changed, string? pairKey = null)
    {
        Item = item;
        _changed = changed;
        PairKey = pairKey;
        _isChecked = item.DefaultSelected && item.DisabledReason is null;
    }

    public ScanItem Item { get; }
    /// <summary>Rows sharing a key are alternatives for one file (its move and its quarantine): ticking one unticks the other.</summary>
    public string? PairKey { get; }
    public string Title => Item.Title;
    public string? Detail => Item.Detail;
    /// <summary>What happens and why. "Allow this OneDrive folder first" is said once in the bar at the top, not on every row.</summary>
    public string Reason => Item.DisabledReason is { } d && d != Item.Reason && !IsPermissionReason(d) ? $"{Item.Reason} {d}" : Item.Reason;

    private static bool IsPermissionReason(string d)
        => d == Desktop.DesktopTidyPlanner.PermissionReason || d == Photos.PhotoPlanner.PermissionReason;
    public Badge Badge => Item.Badge;
    public string SizeText => Item.SizeBytes > 0 ? Sizes.Format(Item.SizeBytes) : "";
    public string? PreviewPath => Item.PreviewPath;
    public string Icon => PreviewTree.IconFor(Item.Title);
    public bool IsSelectable => Item.DisabledReason is null && Item.Actions.Count > 0;
    public string CheckName => $"Include {Item.Title}";

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !IsSelectable) { IsChecked = false; return; }
        _changed(this);
    }
}

/// <summary>Group header with a tri-state "all" box.</summary>
public sealed partial class ChangeGroup : ObservableObject
{
    private bool _updating;

    public ChangeGroup(string title, string? description, Badge badge, IReadOnlyList<ChangeRow> rows)
    {
        Title = title;
        Description = description ?? "";
        Badge = badge;
        Rows = rows;
        Refresh();
    }

    public string Title { get; }
    public string Description { get; }
    public Badge Badge { get; }
    public IReadOnlyList<ChangeRow> Rows { get; }
    public string CountText => Rows.Count == 1 ? "1 item" : $"{Rows.Count:N0} items";
    public bool HasSelectable => Rows.Any(r => r.IsSelectable);
    public string CheckName => $"Include everything in {Title}";

    [ObservableProperty] private bool? _allChecked;

    partial void OnAllCheckedChanged(bool? value)
    {
        if (_updating || value is null) return;
        _updating = true;
        foreach (var r in Rows.Where(r => r.IsSelectable)) r.IsChecked = value.Value;
        _updating = false;
        Refresh();
    }

    /// <summary>Re-reads the rows (after one of them was ticked or unticked).</summary>
    public void Refresh()
    {
        if (_updating) return;
        _updating = true;
        var selectable = Rows.Where(r => r.IsSelectable).ToList();
        AllChecked = selectable.Count == 0 || selectable.All(r => !r.IsChecked) ? false : selectable.All(r => r.IsChecked) ? true : null;
        _updating = false;
    }
}
