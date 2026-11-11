using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages.Now;

/// <summary>
/// Home's optional "Now" card (layout spec §4.3, build list B3): CPU, GPU and RAM load rings, a 60-second line with a
/// CPU/GPU switch, the system drive's bar with "Free up space", and the GPU temperature when nvidia-smi exists.
/// Labelled "Live, not recorded": nothing it shows is kept. It can be folded to its header (remembered).
/// </summary>
public sealed class NowCard : UserControl
{
    private readonly Gauge _cpu = new("CPU"), _gpu = new("GPU"), _ram = new("RAM");
    private readonly Segmented _chartPicker = new() { Items = "CPU|GPU", Label = "Line shows", Margin = new Thickness(0, 0, 0, 6) };
    private readonly System.Windows.Shapes.Path _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Border _chart = new() { Height = 72, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
    private readonly TextBlock _chartCaption = Styled("CPU load, last 60 seconds", "Caption");
    // A faint dashed guide at 50 % with its label, so the line can be read as a number (0 % is the bottom, 100 % the top).
    private readonly System.Windows.Shapes.Path _guide = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
    private readonly TextBlock _guideLabel = Styled("50%", "Caption");
    private readonly TextBlock _driveText = Styled("", "Secondary");
    private readonly ProgressBar _driveBar = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 8, 0, 10) };
    private readonly TextBlock _temperature = Styled("", "Caption");
    private readonly UniformGrid _rings = new() { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _body = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly System.Windows.Controls.Button _fold = new();
    private readonly SymbolIcon _foldIcon = new() { FontSize = 16 };
    private IReadOnlyList<double> _points = Array.Empty<double>();
    private bool _collapsed;
    private bool _hasGpu = true;

    public NowCard()
    {
        SetResourceReference(FontFamilyProperty, "TidyText");
        AutomationProperties.SetName(this, "Now: live load of this PC, not recorded");

        var card = new Border();
        card.SetResourceReference(StyleProperty, "TidyCard");
        var stack = new StackPanel();

        // Header: "Now · Live, not recorded" and the fold button.
        var head = new DockPanel();
        _fold.SetResourceReference(StyleProperty, "LinkButton");
        _fold.Content = _foldIcon;
        _fold.Click += (_, _) => SetCollapsed(!_collapsed, raise: true);
        DockPanel.SetDock(_fold, Dock.Right);
        head.Children.Add(_fold);
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(Styled("Now", "CardTitle"));
        var live = new BadgePill { Text = NowPolicy.LiveLabel, Tone = PillTone.Neutral, ShowDot = false, Margin = new Thickness(10, 0, 0, 0) };
        titles.Children.Add(live);
        head.Children.Add(titles);
        stack.Children.Add(head);

        // Body: rings | 60-second line | drive.
        var grid = new ResponsiveGrid { Gap = 20, MaxColumns = 3, MinItemWidth = 250 };
        _rings.Children.Add(_cpu);
        _rings.Children.Add(_gpu);
        _rings.Children.Add(_ram);
        var ringsPanel = new StackPanel();
        ringsPanel.Children.Add(_rings);
        ringsPanel.Children.Add(_temperature);
        grid.Children.Add(ringsPanel);

        var chartPanel = new StackPanel();
        _chartPicker.SelectionChanged += (_, _) =>
        {
            ChartChanged?.Invoke(_chartPicker.SelectedIndex == 1 ? "gpu" : "cpu");
            UpdateChartCaption();
        };
        chartPanel.Children.Add(_chartPicker);
        _chart.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _chart.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        _line.SetResourceReference(Shape.StrokeProperty, "TidyAccentBrush");
        _guide.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
        _guide.Opacity = 0.5; // a guide line, not text
        _guideLabel.TextWrapping = TextWrapping.NoWrap;
        _guideLabel.IsHitTestVisible = false;
        Canvas.SetLeft(_guideLabel, 4);
        _chart.Child = new Canvas { Children = { _guide, _guideLabel, _line } };
        _chart.SizeChanged += (_, _) => DrawLine();
        AutomationProperties.SetName(_chart, "Load over the last 60 seconds");
        chartPanel.Children.Add(_chart);
        _chartCaption.Margin = new Thickness(0, 4, 0, 0);
        chartPanel.Children.Add(_chartCaption);
        grid.Children.Add(chartPanel);

        var drivePanel = new StackPanel();
        drivePanel.Children.Add(Styled("System drive", "CaptionStrong"));
        _driveText.Margin = new Thickness(0, 4, 0, 0);
        drivePanel.Children.Add(_driveText);
        AutomationProperties.SetName(_driveBar, "System drive used");
        drivePanel.Children.Add(_driveBar);
        var free = new Wpf.Ui.Controls.Button { Content = "Free up space", Icon = new SymbolIcon(SymbolRegular.Broom24), Appearance = ControlAppearance.Secondary };
        AutomationProperties.SetName(free, "Free up space: open Clean");
        free.Click += (_, _) => FreeUpSpaceRequested?.Invoke();
        FreeButton = free;
        drivePanel.Children.Add(free);
        grid.Children.Add(drivePanel);

        _body.Children.Add(grid);
        stack.Children.Add(_body);
        card.Child = stack;
        Content = card;
        SetCollapsed(false, raise: false);
    }

    /// <summary>"Free up space" (Home opens Clean).</summary>
    public event Action? FreeUpSpaceRequested;
    /// <summary>The card was folded (true) or unfolded by the user.</summary>
    public event Action<bool>? CollapsedChanged;
    /// <summary>The line's switch moved: "cpu" or "gpu".</summary>
    public event Action<string>? ChartChanged;

    public Wpf.Ui.Controls.Button FreeButton { get; }
    public bool IsCollapsed => _collapsed;

    /// <summary>Folds the card to its header (sampling stops) or opens it again.</summary>
    public void SetCollapsed(bool collapsed, bool raise)
    {
        _collapsed = collapsed;
        _body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _foldIcon.Symbol = collapsed ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronUp24;
        var text = collapsed ? "Show the Now card" : "Fold the Now card";
        AutomationProperties.SetName(_fold, text);
        _fold.ToolTip = text;
        if (raise) CollapsedChanged?.Invoke(collapsed);
    }

    public void SetChart(string chart) => _chartPicker.SelectedIndex = chart == "gpu" && _hasGpu ? 1 : 0;

    /// <summary>The system drive's free space (null hides the numbers, the button stays).</summary>
    public void SetDrive(DriveDetails? drive, string name)
    {
        if (drive is not { TotalBytes: > 0 })
        {
            _driveText.Text = "Drive space isn't available right now.";
            _driveBar.Visibility = Visibility.Collapsed;
            return;
        }
        double used = 100.0 * (drive.TotalBytes - drive.FreeBytes) / drive.TotalBytes;
        _driveText.Text = $"{name} · {Sizes.Format(drive.FreeBytes)} free of {Sizes.Format(drive.TotalBytes)}";
        _driveBar.Visibility = Visibility.Visible;
        _driveBar.Value = used;
        AutomationProperties.SetName(_driveBar, $"{name} {used:0} percent used");
    }

    /// <summary>Shows a reading and the last 60 seconds.</summary>
    public void Show(NowSample sample, NowHistory history)
    {
        _cpu.Set(sample.Cpu);
        _ram.Set(sample.Ram);
        _hasGpu = sample.Gpu is not null;
        _gpu.Visibility = _hasGpu ? Visibility.Visible : Visibility.Collapsed;
        _rings.Columns = _hasGpu ? 3 : 2;
        if (sample.Gpu is { } g) _gpu.Set(g);
        _chartPicker.Visibility = _hasGpu ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasGpu && _chartPicker.SelectedIndex != 0) _chartPicker.SelectedIndex = 0;
        _points = _chartPicker.SelectedIndex == 1 ? history.Gpu : history.Cpu;
        UpdateChartCaption();
        _temperature.Text = sample.GpuTemperatureC is { } t ? $"GPU temperature {t} °C (from nvidia-smi)" : "";
        _temperature.Visibility = sample.GpuTemperatureC is null ? Visibility.Collapsed : Visibility.Visible;
        DrawLine();
    }

    /// <summary>"CPU load, last 60 seconds · peak 42%": the scale in words next to the 50 % guide.</summary>
    private void UpdateChartCaption()
    {
        var name = _chartPicker.SelectedIndex == 1 ? "GPU" : "CPU";
        _chartCaption.Text = NowCaption(name, _points);
        AutomationProperties.SetName(_chart, _chartCaption.Text + ". The dashed line is 50 percent.");
    }

    /// <summary>The line's caption: what it shows, and its peak once there are readings.</summary>
    public static string NowCaption(string name, IReadOnlyList<double> points)
        => points.Count == 0 ? $"{name} load, last 60 seconds"
            : $"{name} load, last 60 seconds · peak {Math.Clamp(points.Max(), 0, 100).ToString("0", CultureInfo.CurrentCulture)}%";

    private void DrawLine()
    {
        double w = _chart.ActualWidth - 2, h = _chart.ActualHeight - 2;
        if (w > 0 && h > 0)
        {
            double mid = 1 + (h - 2) * 0.5;
            _guide.Data = new LineGeometry(new Point(0, mid), new Point(w, mid));
            _guideLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetTop(_guideLabel, Math.Max(0, mid - _guideLabel.DesiredSize.Height - 1));
        }
        if (w <= 0 || h <= 0 || _points.Count < 2) { _line.Data = null; return; }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // The newest reading sits at the right edge; the line fills in from the right over the first minute.
            double step = w / (NowHistory.Seconds - 1);
            double x0 = w - step * (_points.Count - 1);
            for (int i = 0; i < _points.Count; i++)
            {
                var p = new Point(x0 + i * step, 1 + (h - 2) * (1 - _points[i] / 100.0));
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        _line.Data = geo;
    }

    private static TextBlock Styled(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }

    /// <summary>One load ring: the ring, the percent in the middle, the name under it.</summary>
    private sealed class Gauge : StackPanel
    {
        private readonly RingGauge _ring = new() { Thickness = 6, Width = 64, Height = 64 };
        private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, FontWeight = FontWeights.SemiBold };
        private readonly string _name;

        public Gauge(string name)
        {
            _name = name;
            HorizontalAlignment = HorizontalAlignment.Center;
            _value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var g = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center };
            g.Children.Add(_ring);
            g.Children.Add(_value);
            Children.Add(g);
            var label = Styled(name, "Secondary");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 4, 0, 0);
            Children.Add(label);
            Set(0);
        }

        public void Set(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _ring.Value = percent / 100.0;
            _value.Text = percent.ToString("0", CultureInfo.CurrentCulture) + "%";
            AutomationProperties.SetName(this, $"{_name} load {percent:0} percent");
        }
    }
}
