using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;

namespace PcTidy.UI.Flow;

/// <summary>
/// Grouped, virtualized checklist of scan results with a details panel. Keyboard: arrows move,
/// Space ticks, Enter / Left / Right expand or collapse a group.
/// </summary>
public partial class ResultsPanel : UserControl
{
    private SelectionModel? _model;

    public ResultsPanel() => InitializeComponent();

    /// <summary>Raised when the user asks to see an item in Explorer (the page forwards it to IFlowHost).</summary>
    public event Action<string>? OpenInExplorerRequested;

    public SelectionModel? Model
    {
        get => _model;
        set
        {
            if (_model != null) _model.PropertyChanged -= OnModelPropertyChanged;
            _model = value;
            if (_model != null) _model.PropertyChanged += OnModelPropertyChanged;
            List.ItemsSource = _model?.Rows;
            FillGroups(keep: null);
            ShowDetails(null);
        }
    }

    private bool _groupsAndDetails;

    /// <summary>
    /// 3.2 Workbench layout (Clean, Scan everything): the groups on the left, the picked group's rows on the right, and
    /// the row details as a third column. Same rows and ticks as the single list: only the layout differs. Feature pages
    /// that read the single list (the "List" part) keep it by leaving this off.
    /// </summary>
    public bool GroupsAndDetails
    {
        get => _groupsAndDetails;
        set
        {
            _groupsAndDetails = value;
            WbGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            List.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            KeyboardHint.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            DetailsPanel.Width = value ? 260 : 310; // three columns at 1280 px
            FillGroups(keep: null);
        }
    }

    /// <summary>The left list: section headers (only with several sections) and their groups. The first group is picked.</summary>
    private void FillGroups(GroupRow? keep)
    {
        if (!_groupsAndDetails) return;
        var rows = new List<ListRow>();
        if (_model is { } m)
            foreach (var s in m.Sections)
            {
                if (m.Sections.Count > 1) rows.Add(s.Header);
                rows.AddRange(s.Groups);
            }
        GroupsList.ItemsSource = rows;
        var pick = keep is not null && rows.Contains(keep) ? keep : rows.OfType<GroupRow>().FirstOrDefault();
        GroupsList.SelectedItem = pick;
        ShowGroup(pick);
    }

    private GroupRow? _group;

    /// <summary>Shows the one-time "how this layout works" hint (the page decides, from its settings).</summary>
    public bool ShowHint
    {
        get => WbHint.Visibility == Visibility.Visible;
        set => WbHint.Visibility = value && _groupsAndDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Got it" was clicked: the page stores it so the hint never comes back.</summary>
    public event Action? HintDismissed;

    private void OnHintOk(object sender, RoutedEventArgs e)
    {
        WbHint.Visibility = Visibility.Collapsed;
        HintDismissed?.Invoke();
    }

    private void ShowGroup(GroupRow? g)
    {
        _group = g;
        PickHint.Visibility = g is null ? Visibility.Visible : Visibility.Collapsed;
        GroupPane.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
        ItemsList.ItemsSource = g?.Items;
        ShowDetails(null);
        if (g is null) return;
        GroupTitle.Text = g.Title;
        GroupWhy.Text = g.Group.Description ?? "";
        GroupWhy.Visibility = string.IsNullOrWhiteSpace(g.Group.Description) ? Visibility.Collapsed : Visibility.Visible;
        GroupSize.Bytes = g.TotalBytes;
        if (g.IsInfoOnly) { GroupPill.Badge = null; GroupPill.Text = "Info only"; GroupPill.Tone = PillTone.Neutral; }
        else GroupPill.Badge = g.Group.Badge;
        GroupEmpty.Visibility = g.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGroupPicked(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsList.SelectedItem is GroupRow g) { if (!ReferenceEquals(g, _group)) ShowGroup(g); }
        else if (GroupsList.SelectedItem is SectionRow) GroupsList.SelectedItem = _group; // headers aren't pickable
    }

    private void OnItemPicked(object sender, SelectionChangedEventArgs e) => ShowDetails(ItemsList.SelectedItem as ItemRow);

    private void OnGroupsKeyDown(object sender, KeyEventArgs e)
    {
        if (GroupsList.SelectedItem is not GroupRow g) return;
        if (e.Key == Key.Space) { g.IsChecked = g.IsChecked != true; e.Handled = true; }
        else if (e.Key is Key.Right or Key.Enter && g.Items.Count > 0)
        {
            ItemsList.SelectedItem ??= g.Items[0];
            (ItemsList.ItemContainerGenerator.ContainerFromItem(ItemsList.SelectedItem) as UIElement ?? ItemsList).Focus();
            e.Handled = true;
        }
    }

    private void OnItemsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            (GroupsList.ItemContainerGenerator.ContainerFromItem(GroupsList.SelectedItem) as UIElement ?? GroupsList).Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && ItemsList.SelectedItem is ItemRow item) { item.IsChecked = !item.IsChecked; e.Handled = true; }
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new scan loads new groups (expanding a group only reshapes the single list, which this layout doesn't use).
        if (e.PropertyName == nameof(SelectionModel.Rows) && _model != null && _groupsAndDetails
            && (_group is null || !_model.Sections.SelectMany(s => s.Groups).Contains(_group)))
            FillGroups(keep: null);
        if (e.PropertyName == nameof(SelectionModel.Rows) && _model != null)
        {
            var keep = List.SelectedItem;
            List.ItemsSource = _model.Rows;
            if (keep != null && _model.Rows.Contains(keep))
            {
                List.SelectedItem = keep;
                List.ScrollIntoView(keep);
            }
        }
    }

    /// <summary>Selects the first item row (used by screenshots and "show me" links).</summary>
    public void SelectFirstItem()
    {
        if (_model == null) return;
        if (_groupsAndDetails)
        {
            if (_model.Sections.SelectMany(s => s.Groups).FirstOrDefault(g => g.Items.Count > 0) is not { } g) return;
            GroupsList.SelectedItem = g;
            ItemsList.SelectedItem = g.Items[0];
            ItemsList.ScrollIntoView(g.Items[0]);
            return;
        }
        var first = _model.Rows.OfType<ItemRow>().FirstOrDefault();
        if (first == null && _model.Rows.OfType<GroupRow>().FirstOrDefault(g => g.Items.Count > 0) is { } group)
        {
            group.IsExpanded = true;
            first = group.Items[0];
        }
        if (first == null) return;
        List.SelectedItem = first;
        List.ScrollIntoView(first);
    }

    public void FocusList()
    {
        if (_groupsAndDetails) GroupsList.Focus();
        else List.Focus();
    }

    /// <summary>Selects a row and scrolls it into view (a group another section asked for).</summary>
    public void ShowRow(ListRow row)
    {
        if (_groupsAndDetails)
        {
            var group = row as GroupRow ?? (row as ItemRow)?.Group;
            if (group is null) return;
            GroupsList.SelectedItem = group;
            GroupsList.ScrollIntoView(group);
            if (row is ItemRow item) { ItemsList.SelectedItem = item; ItemsList.ScrollIntoView(item); }
            return;
        }
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    public void ClearSelection()
    {
        List.SelectedItem = null;
        ItemsList.SelectedItem = null;
        ShowDetails(null);
    }

    // ---------------- interaction ----------------

    private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<ButtonBase>(e.OriginalSource as DependencyObject)) return; // checkbox / chevron handle themselves
        if ((sender as FrameworkElement)?.DataContext is GroupRow g) g.IsExpanded = !g.IsExpanded;
    }

    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow g) g.IsExpanded = !g.IsExpanded;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (List.SelectedItem)
        {
            case ItemRow item when e.Key == Key.Space:
                item.IsChecked = !item.IsChecked;
                e.Handled = true;
                break;
            case GroupRow group when e.Key == Key.Space:
                group.IsChecked = group.IsChecked != true;
                e.Handled = true;
                break;
            case GroupRow group when e.Key is Key.Enter or Key.Right && !group.IsExpanded:
            case GroupRow group2 when e.Key is Key.Enter or Key.Left && group2.IsExpanded:
                var g = (GroupRow)List.SelectedItem;
                g.IsExpanded = !g.IsExpanded;
                e.Handled = true;
                break;
            case ItemRow child when e.Key == Key.Left:
                List.SelectedItem = child.Group;
                child.Group.IsExpanded = false;
                List.ScrollIntoView(child.Group);
                e.Handled = true;
                break;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(List.SelectedItem as ItemRow);

    private void OnCloseDetails(object sender, RoutedEventArgs e)
    {
        List.SelectedItem = null;
        ItemsList.SelectedItem = null;
        ShowDetails(null);
    }

    private string? _detailsPath;

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (_detailsPath != null) OpenInExplorerRequested?.Invoke(_detailsPath);
    }

    // ---------------- details panel ----------------

    private void ShowDetails(ItemRow? row)
    {
        // Workbench: with the details column open, the groups column gives up room so the rows stay readable.
        if (_groupsAndDetails && WbGrid.ColumnDefinitions.Count > 0)
        {
            WbGrid.ColumnDefinitions[0].Width = new GridLength(row == null ? 310 : 265);
            GroupsList.Tag = row == null ? null : "narrow";
        }
        if (row == null)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var item = row.Item;
        DetailsStack.Children.Clear();
        DetailsPanel.Visibility = Visibility.Visible;

        Add(Text(item.Title, "SectionTitle", wrap: true));
        var pills = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        pills.Children.Add(item.DisabledReason is null
            ? new BadgePill { Badge = item.Badge, Margin = new Thickness(0, 0, 6, 0) }
            : new BadgePill { Text = "Not offered", Tone = PillTone.Neutral, Margin = new Thickness(0, 0, 6, 0) });
        if (item.NeedsAdmin) pills.Children.Add(new BadgePill { Text = "Needs admin", Tone = PillTone.Neutral, Margin = new Thickness(0, 0, 6, 0) });
        if (item.NeedsRestart) pills.Children.Add(new BadgePill { Text = "Needs restart", Tone = PillTone.Caution });
        Add(pills);

        if (item.DisabledReason != null)
            Add(new Wpf.Ui.Controls.InfoBar { Title = "Not offered", Message = item.DisabledReason, IsOpen = true, IsClosable = false, Severity = Wpf.Ui.Controls.InfoBarSeverity.Informational, Margin = new Thickness(0, 10, 0, 0) });

        Add(Text(item.Reason, "Secondary", wrap: true, top: 10));
        if (!string.IsNullOrEmpty(item.Detail)) Add(Text(item.Detail!, "Caption", wrap: true, top: 8));

        AddDivider();
        if (item.SizeBytes > 0) AddFact("Size", Sizes.Format(item.SizeBytes));
        if (item.SizeOnDiskBytes > 0 && item.SizeOnDiskBytes != item.SizeBytes) AddFact("Size on disk", Sizes.Format(item.SizeOnDiskBytes));
        if (item.FileCount > 0) AddFact("Files", item.FileCount.ToString("N0", CultureInfo.CurrentCulture));
        if (!string.IsNullOrEmpty(item.OwnerApp)) AddFact("Belongs to", item.OwnerApp!);
        if (item.LastModifiedUtc is { } lm) AddFact("Last changed", lm.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        if (!string.IsNullOrEmpty(item.CurrentState)) AddFact("Right now", item.CurrentState!);
        if (item.RequiresClosedApps is { Count: > 0 } apps) AddFact("Close first", string.Join(", ", apps));
        if (item.Details != null)
            foreach (var kv in item.Details) AddFact(kv.Key, kv.Value);

        if (item.Actions.Count > 0)
        {
            AddDivider();
            Add(Text("WHAT HAPPENS", "CaptionStrong"));
            foreach (var a in item.Actions.Take(5))
                Add(Text("• " + a.Description + (a.Reversible ? "" : " (can't be undone)"), "Secondary", wrap: true, top: 4));
            if (item.Actions.Count > 5) Add(Text($"and {item.Actions.Count - 5} more", "Caption", top: 4));
        }
        else Add(Text("Information only. Nothing here changes.", "Caption", wrap: true, top: 10));

        _detailsPath = PathFor(item);
        OpenButton.Visibility = _detailsPath != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The path to reveal: first action's source, else the detail line if it looks like a full path.</summary>
    private static string? PathFor(ScanItem item)
    {
        var src = item.Actions.Select(a => a.SourcePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (src != null) return src;
        return item.Detail is { } d && d.Length > 3 && System.IO.Path.IsPathFullyQualified(d) ? d : null;
    }

    private void Add(UIElement e) => DetailsStack.Children.Add(e);

    private void AddDivider()
    {
        var b = new Border { Margin = new Thickness(0, 12, 0, 10) };
        b.SetResourceReference(StyleProperty, "RowDivider");
        Add(b);
    }

    private void AddFact(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Text(label, "Secondary");
        var v = Text(value, "Body", wrap: true);
        v.FontSize = 13;
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        Add(g);
    }

    private static TextBlock Text(string text, string style, bool wrap = false, double top = 0)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(StyleProperty, style);
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }
}

/// <summary>3.2 Workbench templates: compact group rows on the left, plain rows on the right (anything else: implicit).</summary>
public sealed class WorkbenchRowTemplates : DataTemplateSelector
{
    public DataTemplate? Group { get; set; }
    public DataTemplate? Item { get; set; }
    public DataTemplate? Section { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        GroupRow => Group,
        ItemRow => Item,
        SectionRow => Section,
        _ => base.SelectTemplate(item, container),
    };
}
