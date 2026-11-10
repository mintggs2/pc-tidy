using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.View;

/// <summary>One point of a history graph.</summary>
public sealed record GraphPoint(DateTime TimeUtc, double Value, SampleKind Kind);

/// <summary>
/// A small line graph drawn directly (no chart library): one metric over time, with before-run samples as hollow
/// dots, after-run samples as filled accent dots and daily samples as small dots. Theme brushes come in through
/// dependency properties (bound with DynamicResource), so light and dark both work. Its accessible name is a
/// one-sentence summary of the data for screen readers.
/// </summary>
public sealed class HistoryGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<GraphPoint>), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(Array.Empty<GraphPoint>(), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HistoryGraph)d).UpdateName()));
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(MetricInfo), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HistoryGraph)d).UpdateName()));
    public static readonly DependencyProperty LineBrushProperty = RegisterBrush(nameof(LineBrush), Brushes.SeaGreen);
    public static readonly DependencyProperty AxisBrushProperty = RegisterBrush(nameof(AxisBrush), Brushes.Gray);
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty BackgroundBrushProperty = RegisterBrush(nameof(BackgroundBrush), Brushes.Transparent);

    private static DependencyProperty RegisterBrush(string name, Brush fallback)
        => DependencyProperty.Register(name, typeof(Brush), typeof(HistoryGraph), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<GraphPoint> Points { get => (IReadOnlyList<GraphPoint>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public MetricInfo? Metric { get => (MetricInfo?)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush BackgroundBrush { get => (Brush)GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }

    private const double LeftGutter = 64, BottomGutter = 22, TopPad = 8, RightPad = 8;

    protected override Size MeasureOverride(Size available)
        => new(double.IsInfinity(available.Width) ? 480 : available.Width, double.IsInfinity(available.Height) ? 180 : Math.Min(available.Height, 220));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));
        var points = Points.OrderBy(p => p.TimeUtc).ToList();
        var plot = new Rect(LeftGutter, TopPad, Math.Max(1, w - LeftGutter - RightPad), Math.Max(1, h - TopPad - BottomGutter));
        var axisPen = new Pen(AxisBrush, 1);
        axisPen.Freeze();
        dc.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);

        if (points.Count == 0 || Metric is not { } metric)
        {
            DrawText(dc, "No samples yet", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), Align.Center, middle: true);
            return;
        }

        var (min, max) = Range(points);
        var t0 = points[0].TimeUtc.Ticks;
        var t1 = points[^1].TimeUtc.Ticks;
        double X(DateTime t) => t1 == t0 ? plot.Left + plot.Width / 2 : plot.Left + (t.Ticks - t0) / (double)(t1 - t0) * plot.Width;
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;

        // Three horizontal guide lines with their values.
        var guidePen = new Pen(AxisBrush, 0.5) { DashStyle = DashStyles.Dot };
        guidePen.Freeze();
        for (int i = 0; i <= 2; i++)
        {
            var v = min + (max - min) * i / 2;
            var y = Y(v);
            if (i > 0) dc.DrawLine(guidePen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, metric.Format(v), new Point(plot.Left - 6, y), Align.Right, middle: true);
        }
        DrawText(dc, Short(points[0].TimeUtc), new Point(plot.Left, plot.Bottom + 4), Align.Left, middle: false);
        if (points.Count > 1) DrawText(dc, Short(points[^1].TimeUtc), new Point(plot.Right, plot.Bottom + 4), Align.Right, middle: false);

        var linePen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(points[0].TimeUtc), Y(points[0].Value)), false, false);
                g.PolyLineTo(points.Skip(1).Select(p => new Point(X(p.TimeUtc), Y(p.Value))).ToList(), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, linePen, geometry);
        }
        foreach (var p in points)
        {
            var at = new Point(X(p.TimeUtc), Y(p.Value));
            switch (p.Kind)
            {
                case SampleKind.Before: dc.DrawEllipse(BackgroundBrush, linePen, at, 4.5, 4.5); break;
                case SampleKind.After: dc.DrawEllipse(LineBrush, null, at, 5, 5); break;
                default: dc.DrawEllipse(LineBrush, null, at, 2.5, 2.5); break;
            }
        }
    }

    private static (double Min, double Max) Range(IReadOnlyList<GraphPoint> points)
    {
        double min = points.Min(p => p.Value), max = points.Max(p => p.Value);
        if (max - min < 1e-9) { var pad = Math.Max(1, Math.Abs(max) * 0.05); return (min - pad, max + pad); }
        var margin = (max - min) * 0.1;
        return (min - margin, max + margin);
    }

    private enum Align { Left, Center, Right }

    /// <summary>Draws a label anchored at <paramref name="at"/>: horizontally by <paramref name="align"/>, vertically centred or hanging below.</summary>
    private void DrawText(DrawingContext dc, string text, Point at, Align align, bool middle)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double x = align switch { Align.Center => at.X - ft.Width / 2, Align.Right => at.X - ft.Width, _ => at.X };
        double y = middle ? at.Y - ft.Height / 2 : at.Y;
        dc.DrawText(ft, new Point(x, y));
    }

    private static string Short(DateTime utc) => utc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);

    /// <summary>Screen readers get the numbers, not a picture.</summary>
    private void UpdateName()
    {
        var points = Points;
        AutomationProperties.SetName(this, Describe(Metric, points));
    }

    internal static string Describe(MetricInfo? metric, IReadOnlyList<GraphPoint> points)
    {
        if (metric is null || points.Count == 0) return "History graph: no samples yet.";
        var ordered = points.OrderBy(p => p.TimeUtc).ToList();
        return $"{metric.Label} history: {ordered.Count} samples from {Short(ordered[0].TimeUtc)} to {Short(ordered[^1].TimeUtc)}; "
               + $"latest {metric.Format(ordered[^1].Value)}, lowest {metric.Format(ordered.Min(p => p.Value))}, highest {metric.Format(ordered.Max(p => p.Value))}.";
    }
}
