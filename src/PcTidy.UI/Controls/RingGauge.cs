using System.Windows;
using System.Windows.Media;

namespace PcTidy.UI.Controls;

/// <summary>Custom-drawn circular gauge (drive space, health score). Purely visual.</summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Optional second segment drawn after the main value (e.g. "can be freed").</summary>
    public static readonly DependencyProperty ExtraValueProperty = DependencyProperty.Register(
        nameof(ExtraValue), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExtraBrushProperty = DependencyProperty.Register(
        nameof(ExtraBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double ExtraValue { get => (double)GetValue(ExtraValueProperty); set => SetValue(ExtraValueProperty, value); }
    public Brush ExtraBrush { get => (Brush)GetValue(ExtraBrushProperty); set => SetValue(ExtraBrushProperty, value); }

    public RingGauge()
    {
        SetResourceReference(TrackBrushProperty, "TidyRingTrackBrush");
        SetResourceReference(RingBrushProperty, "TidyAccentBrush");
        SetResourceReference(ExtraBrushProperty, "SystemFillColorCautionBrush");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Thickness) / 2;

        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);
        double v = Math.Clamp(Value, 0, 1);
        DrawArc(dc, center, r, 0, v, RingBrush);
        if (ExtraValue > 0)
            DrawArc(dc, center, r, v + 0.012, Math.Min(1, v + ExtraValue), ExtraBrush);
    }

    private void DrawArc(DrawingContext dc, Point c, double r, double from, double to, Brush brush)
    {
        if (to - from <= 0.001) return;
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (to - from >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        Point P(double f)
        {
            double a = f * 2 * Math.PI - Math.PI / 2;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(from), false, false);
            ctx.ArcTo(P(to), new Size(r, r), 0, to - from > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
