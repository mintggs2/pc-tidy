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
            ShowDetails(null);
        }
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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

    public void FocusList() => List.Focus();

    /// <summary>Selects a row and scrolls it into view (a group another section asked for).</summary>
    public void ShowRow(ListRow row)
    {
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    public void ClearSelection()
    {
        List.SelectedItem = null;
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
