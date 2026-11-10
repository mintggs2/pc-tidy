using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PcTidy.Features.Organize.UI;

/// <summary>
/// The Organize page. A fixed-height page like the shell's standard flow: only the lists scroll, so the sticky
/// action bar always stays in view.
/// </summary>
public partial class OrganizeView : UserControl, IFixedHeightPage
{
    /// <summary>Below this width the rules pane and the preview don't fit side by side: rules go on top.</summary>
    private const double SideBySideMinWidth = 860;
    /// <summary>Stacked on a short screen: the rules pane keeps about two rules in view, the preview at least this.</summary>
    private const double StackedRulesHeight = 240, StackedMinPreviewHeight = 360, StackedGap = 12;

    private Point _dragStart;
    private RuleRowViewModel? _dragRow;

    public OrganizeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private OrganizeViewModel? ViewModel => DataContext as OrganizeViewModel;

    // ---- responsive layout ----

    /// <summary>
    /// Wide: rules beside the preview, everything fits, only the lists scroll. Narrow: rules above the preview;
    /// if the screen is also short, this part scrolls (the preview then fills the view) while the step bar and the
    /// sticky action bar stay put.
    /// </summary>
    private void OnSortAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var area = e.NewSize;
        bool sideBySide = area.Width >= SideBySideMinWidth;
        bool scroll = !sideBySide && area.Height < StackedRulesHeight + StackedGap + StackedMinPreviewHeight;

        RulesColumn.Width = sideBySide ? new GridLength(380) : new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(sideBySide ? 16 : 0);
        Grid.SetColumnSpan(RulesCard, sideBySide ? 1 : 3);
        Grid.SetColumn(PreviewCard, sideBySide ? 2 : 0);
        Grid.SetColumnSpan(PreviewCard, sideBySide ? 1 : 3);
        Grid.SetRow(PreviewCard, sideBySide ? 0 : 2);

        if (sideBySide)
        {
            RulesRow.Height = new GridLength(1, GridUnitType.Star);
            GapRow.Height = PreviewRow.Height = new GridLength(0);
        }
        else if (!scroll)
        {
            RulesRow.Height = new GridLength(2, GridUnitType.Star);
            GapRow.Height = new GridLength(StackedGap);
            PreviewRow.Height = new GridLength(3, GridUnitType.Star);
        }
        else
        {
            RulesRow.Height = PreviewRow.Height = GridLength.Auto;
            GapRow.Height = new GridLength(StackedGap);
        }
        RulesCard.Height = scroll ? StackedRulesHeight : double.NaN;
        PreviewCard.Height = scroll ? Math.Max(StackedMinPreviewHeight, area.Height - StackedGap) : double.NaN;
        SortScroll.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    /// <summary>Below this height the Takeout folders card and the preview don't both fit: that part scrolls instead.</summary>
    private const double TakeoutMinAreaHeight = 480, TakeoutPreviewHeight = 340;

    /// <summary>
    /// Google Takeout on a short window (1024×700): the folders card plus a squeezed preview would leave no room for
    /// a single photo row, so the card and preview scroll together and the preview keeps a usable height.
    /// </summary>
    private void OnTakeoutAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _takeoutAreaHeight = e.NewSize.Height;
        UpdateTakeoutLayout();
    }

    private double _takeoutAreaHeight = double.PositiveInfinity;
    private TakeoutViewModel? _takeout;

    /// <summary>Only a real preview needs the extra height; the empty state fits (without its big icon) as it is.</summary>
    private void UpdateTakeoutLayout()
    {
        bool shortArea = _takeoutAreaHeight < TakeoutMinAreaHeight;
        bool scroll = shortArea && _takeout?.HasPreview == true;
        TakeoutScroll.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (!scroll) TakeoutScroll.ScrollToTop();
        TakeoutPreview.Height = scroll ? TakeoutPreviewHeight : double.NaN;
        TakeoutEmptyIcon.Visibility = shortArea ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_takeout is not null) _takeout.PropertyChanged -= OnTakeoutChanged;
        _takeout = (e.NewValue as OrganizeViewModel)?.Takeout;
        if (_takeout is not null) _takeout.PropertyChanged += OnTakeoutChanged;
        UpdateTakeoutLayout();
    }

    private void OnTakeoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TakeoutViewModel.HasPreview)) UpdateTakeoutLayout();
    }

    // ---- per-rule menu (also the keyboard way to reorder) ----

    private void RuleMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not FrameworkElement { DataContext: RuleRowViewModel row } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Add(string header, SymbolRegular icon, ICommand command, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon(icon), Command = command, CommandParameter = row, IsEnabled = enabled };
            AutomationProperties.SetName(item, header);
            menu.Items.Add(item);
        }
        int index = vm.Rules.IndexOf(row);
        Add("Edit or test this rule", SymbolRegular.Edit24, vm.EditRuleCommand);
        Add("Move up", SymbolRegular.ArrowUp24, vm.MoveRuleUpCommand, index > 0);
        Add("Move down", SymbolRegular.ArrowDown24, vm.MoveRuleDownCommand, index < vm.Rules.Count - 1);
        if (row.IsCustom)
        {
            menu.Items.Add(new Separator());
            Add("Delete this rule", SymbolRegular.Delete24, vm.DeleteRuleCommand);
        }
        menu.IsOpen = true;
    }

    // ---- drag to reorder rules ----

    private void RulesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(RulesList);
        // Don't start a drag from the toggle or the buttons on the row.
        _dragRow = IsInside<ButtonBase>(e.OriginalSource as DependencyObject) ? null : RowAt(e.OriginalSource as DependencyObject);
    }

    private void RulesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(RulesList) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(RulesList, new DataObject(typeof(RuleRowViewModel), row), DragDropEffects.Move);
    }

    private void RulesList_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.Data.GetData(typeof(RuleRowViewModel)) is not RuleRowViewModel dragged) return;
        var target = RowAt(e.OriginalSource as DependencyObject);
        var index = target is null ? vm.Rules.Count - 1 : vm.Rules.IndexOf(target);
        vm.MoveRuleTo(dragged, index);
    }

    private static RuleRowViewModel? RowAt(DependencyObject? source)
    {
        for (var d = source; d is not null; d = ParentOf(d))
            if (d is FrameworkElement { DataContext: RuleRowViewModel row }) return row;
        return null;
    }

    private static bool IsInside<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var d = source; d is not null; d = ParentOf(d))
            if (d is T) return true;
        return false;
    }

    /// <summary>Visual parent, or logical parent for content elements like Run (VisualTreeHelper throws on those).</summary>
    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
