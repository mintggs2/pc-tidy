using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Controls;

/// <summary>
/// Cards in equal columns that wrap when the window is narrow: 3 columns where they fit at
/// <see cref="MinItemWidth"/>, else 2, else 1. Cards in a row share the row's height; a lone card on the last row
/// takes the full width so nothing looks left behind. Gaps never collapse, so cards never touch.
/// </summary>
public class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>Columns used by the last layout pass (pages can react, e.g. smaller drive rings).</summary>
    public int Columns { get; private set; } = 1;

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int ColumnsFor(double width, int count)
    {
        int max = Math.Max(1, Math.Min(MaxColumns, count));
        if (double.IsInfinity(width)) return max;
        int fit = (int)Math.Floor((width + Gap) / (MinItemWidth + Gap));
        return Math.Clamp(fit, 1, max);
    }

    private IEnumerable<List<UIElement>> Rows(List<UIElement> items, int columns)
    {
        for (int i = 0; i < items.Count; i += columns) yield return items.Skip(i).Take(columns).ToList();
    }

    private double CellWidth(double width, int inRow) => Math.Max(0, (width - Gap * (inRow - 1)) / inRow);

    protected override Size MeasureOverride(Size available)
    {
        var items = Visible();
        foreach (UIElement c in InternalChildren) if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        if (items.Count == 0) return new Size(0, 0);
        double width = double.IsInfinity(available.Width) ? MinItemWidth * MaxColumns + Gap * (MaxColumns - 1) : available.Width;
        Columns = ColumnsFor(available.Width, items.Count);
        double height = 0;
        foreach (var row in Rows(items, Columns))
        {
            double cell = CellWidth(width, row.Count), rowHeight = 0;
            foreach (var c in row)
            {
                c.Measure(new Size(cell, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, c.DesiredSize.Height);
            }
            height += rowHeight + (height > 0 ? Gap : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var items = Visible();
        if (items.Count == 0) return final;
        int columns = ColumnsFor(final.Width, items.Count);
        double y = 0;
        foreach (var row in Rows(items, columns))
        {
            double cell = CellWidth(final.Width, row.Count);
            double rowHeight = row.Max(c => c.DesiredSize.Height);
            double x = 0;
            foreach (var c in row)
            {
                c.Arrange(new Rect(x, y, cell, rowHeight));
                x += cell + Gap;
            }
            y += rowHeight + Gap;
        }
        return final;
    }
}

/// <summary>
/// GOTCHA (WPF-UI 4.3): <see cref="InfoBar"/> doesn't render its Content, so action buttons placed there never show.
/// This puts the buttons in a row right under the bar instead, inside one container that can be removed as a unit.
/// </summary>
public static class InfoBars
{
    public static StackPanel WithActions(InfoBar bar, params System.Windows.Controls.Button[] actions)
    {
        var margin = bar.Margin;
        bar.Margin = new Thickness(0);
        var panel = new StackPanel { Margin = margin };
        panel.Children.Add(bar);
        if (actions.Length > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var b in actions)
            {
                b.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(b);
            }
            panel.Children.Add(row);
        }
        // Closing the bar hides its buttons too.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            ?.AddValueChanged(bar, (_, _) => panel.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        return panel;
    }
}

/// <summary>
/// Works around a WPF virtualizing-list quirk: when the window gets narrower in width and height at once (restore from
/// maximised, or a resize), the recycled rows can stay arranged at the old, wider width, so their right-hand columns
/// (badge, size) slide out of view. After any width change the rows panel is laid out again once.
/// Set on the shared flat list style (<c>TidyFlatList</c>), so every list using it gets it.
/// </summary>
public static class ListReflow
{
    public static readonly DependencyProperty OnResizeProperty = DependencyProperty.RegisterAttached(
        "OnResize", typeof(bool), typeof(ListReflow), new PropertyMetadata(false, OnChanged));

    public static bool GetOnResize(DependencyObject d) => (bool)d.GetValue(OnResizeProperty);
    public static void SetOnResize(DependencyObject d, bool value) => d.SetValue(OnResizeProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.SizeChanged -= OnSizeChanged;
        if (e.NewValue is true) list.SizeChanged += OnSizeChanged;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || sender is not ItemsControl list) return;
        // Once the resize has fully settled: invalidating during its layout passes doesn't help.
        list.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (ItemsHost(list) is { } panel)
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        });
    }

    private static Panel? ItemsHost(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Panel { IsItemsHost: true } p) return p;
            if (ItemsHost(child) is { } found) return found;
        }
        return null;
    }
}
