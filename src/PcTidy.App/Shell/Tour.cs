using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Shell;

/// <summary>One stop of the spotlight tour. <paramref name="Target"/> names what is lit up (null = nothing, card centred).</summary>
public sealed record TourStep(string? Target, string Title, string Text);

/// <summary>
/// The replayable spotlight tour (layout spec §4.6 item 1): "Step x of y", Back / Next / Close, Esc closes. Started
/// from Help and Settings; the first-run welcome stays three screens. It only points at things: it changes nothing.
/// </summary>
public sealed class TourModel
{
    public static readonly IReadOnlyList<TourStep> DefaultSteps = new[]
    {
        new TourStep("home.scan", "Scan everything", "Start here. PC Tidy looks at every section and shows one combined preview. Nothing changes until you confirm."),
        new TourStep("rail.groups", "Tabs by goal", "Overview, Clean, Optimize, Apps, Organize and Reports. Each page scans, shows what it found and why, and asks before it changes anything."),
        new TourStep("rail.more", "Pages in this tab", "The open tab lists its pages here. Clean, for example, has Space map and Duplicates; Optimize has Drivers and Games."),
        new TourStep("rail.history", "History and Undo", "Every run is listed here with its own Undo. Windows' restore points are here too."),
        new TourStep("rail.quarantine", "Quarantine", "Removed files go here first, not straight to the bin. Restore any of them until it's emptied."),
        new TourStep("rail.settings", "Settings", "Protected folders, how long quarantine keeps things, the theme, and this tour again."),
        new TourStep(null, "Simulate first", "Every confirm offers Simulate first: the full list of what would happen, and nothing changes. Try it whenever you're unsure."),
    };

    public TourModel(IReadOnlyList<TourStep>? steps = null)
    {
        Steps = steps is { Count: > 0 } ? steps : DefaultSteps;
    }

    public IReadOnlyList<TourStep> Steps { get; }
    public int Index { get; private set; }
    public bool IsOpen { get; private set; } = true;
    public TourStep Current => Steps[Index];
    public int Count => Steps.Count;
    public string StepText => $"Step {Index + 1} of {Count}";
    public bool CanGoBack => Index > 0;
    public bool IsLast => Index == Count - 1;
    public string NextText => IsLast ? "Done" : "Next";

    /// <summary>Raised after Index changed.</summary>
    public event Action? Changed;
    /// <summary>Raised once when the tour closes (Close, Esc, or Next on the last step).</summary>
    public event Action? Closed;

    public void Next()
    {
        if (!IsOpen) return;
        if (IsLast) { Close(); return; }
        Index++;
        Changed?.Invoke();
    }

    public void Back()
    {
        if (!IsOpen || !CanGoBack) return;
        Index--;
        Changed?.Invoke();
    }

    public void GoTo(int index)
    {
        if (!IsOpen || index < 0 || index >= Count || index == Index) return;
        Index = index;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Closed?.Invoke();
    }
}

/// <summary>
/// The tour's overlay: dims everything except the step's target (a cut-out with an accent ring), and a small card
/// with the step, Back / Next / Close. Keyboard: Tab stays inside the card, Enter on Next, Esc closes; the step is
/// announced to screen readers. Clicks outside the card do nothing (the tour never presses anything for you).
/// </summary>
public sealed class TourOverlay : Grid
{
    private readonly TourModel _model;
    private readonly Func<string, Rect?> _target;
    private readonly System.Windows.Shapes.Path _dim = new();
    private readonly Border _ring = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
    private readonly Canvas _canvas = new();
    private readonly Border _card = new() { Width = 340, Padding = new Thickness(20, 16, 20, 16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly TextBlock _step = new() { FontSize = 12 };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _back = new() { Content = "Back", Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 76 };
    private readonly Button _next = new() { Appearance = ControlAppearance.Primary, MinWidth = 88 };
    private readonly Button _close = new() { Content = "Close", Appearance = ControlAppearance.Transparent, MinWidth = 64 };

    public TourOverlay(TourModel model, Func<string, Rect?> target)
    {
        _model = model;
        _target = target;
        Background = Brushes.Transparent; // catches clicks: nothing under the overlay is pressed during the tour
        Focusable = true;
        AutomationProperties.SetName(this, "PC Tidy tour");

        _dim.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TidyScrimBrush");
        _dim.IsHitTestVisible = false;
        _ring.SetResourceReference(Border.BorderBrushProperty, "TidyAccentBrush");
        Children.Add(_dim);
        _canvas.Children.Add(_ring);

        _card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        KeyboardNavigation.SetTabNavigation(_card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(_card, KeyboardNavigationMode.Cycle);
        _step.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _title.SetResourceReference(StyleProperty, "CardTitle");
        _title.TextWrapping = TextWrapping.Wrap;
        _text.SetResourceReference(StyleProperty, "Body");
        _text.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Assertive);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_close, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_back, Dock.Right);
        buttons.Children.Add(_close);
        buttons.Children.Add(_next);
        buttons.Children.Add(_back);
        // Tab follows what's on screen (Close, Back, Next), not the docking order of the children.
        _close.TabIndex = 0;
        _back.TabIndex = 1;
        _next.TabIndex = 2;
        AutomationProperties.SetName(_back, "Back: previous step");
        AutomationProperties.SetName(_close, "Close the tour");
        _back.Click += (_, _) => _model.Back();
        _next.Click += (_, _) => _model.Next();
        _close.Click += (_, _) => _model.Close();

        var stack = new StackPanel();
        stack.Children.Add(_step);
        stack.Children.Add(_title);
        stack.Children.Add(_text);
        stack.Children.Add(buttons);
        _card.Child = stack;
        _canvas.Children.Add(_card);
        Children.Add(_canvas);

        PreviewKeyDown += OnKey;
        SizeChanged += (_, _) => Place();
        _model.Changed += Show;
        Loaded += (_, _) => Show();
    }

    public TourModel Model => _model;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _model.Close(); e.Handled = true; }
    }

    private void Show()
    {
        var s = _model.Current;
        _step.Text = _model.StepText;
        _title.Text = s.Title;
        _text.Text = s.Text;
        _back.Visibility = _model.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = _model.NextText;
        AutomationProperties.SetName(_next, _model.IsLast ? "Done: close the tour" : "Next step");
        AutomationProperties.SetName(_card, $"Tour, {_model.StepText}: {s.Title}. {s.Text}");
        Place();
        Dispatcher.BeginInvoke(() =>
        {
            Place(); // targets that just scrolled into view
            _next.Focus();
            if (UIElementAutomationPeer.FromElement(_title) is { } peer) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Cuts the target out of the dim layer, rings it, and puts the card next to it (or centred).</summary>
    private void Place()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var full = new RectangleGeometry(new Rect(0, 0, w, h));
        Rect? target = _model.Current.Target is { } id ? _target(id) : null;
        if (target is { } r && r.Width > 0 && r.Height > 0)
        {
            r.Inflate(4, 4);
            r.Intersect(new Rect(0, 0, w, h));
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r, 8, 8));
            _ring.Visibility = Visibility.Visible;
            _ring.Width = r.Width;
            _ring.Height = r.Height;
            Canvas.SetLeft(_ring, r.Left);
            Canvas.SetTop(_ring, r.Top);
            target = r;
        }
        else
        {
            _dim.Data = full;
            _ring.Visibility = Visibility.Collapsed;
            target = null;
        }
        _card.Measure(new Size(_card.Width, double.PositiveInfinity));
        var size = new Size(_card.Width, _card.DesiredSize.Height);
        var at = CardPosition(target, size, new Size(w, h));
        Canvas.SetLeft(_card, at.X);
        Canvas.SetTop(_card, at.Y);
    }

    /// <summary>Right of the target, else below, else above, else centred; always inside the window with a 16 px margin.</summary>
    public static Point CardPosition(Rect? target, Size card, Size window)
    {
        const double gap = 16;
        double Clamp(double v, double size, double max) => Math.Max(gap, Math.Min(v, max - size - gap));
        if (target is not { } r) return new Point(Math.Max(gap, (window.Width - card.Width) / 2), Math.Max(gap, (window.Height - card.Height) / 2));
        if (r.Right + gap + card.Width + gap <= window.Width) return new Point(r.Right + gap, Clamp(r.Top, card.Height, window.Height));
        if (r.Bottom + gap + card.Height + gap <= window.Height) return new Point(Clamp(r.Left, card.Width, window.Width), r.Bottom + gap);
        if (r.Top - gap - card.Height >= gap) return new Point(Clamp(r.Left, card.Width, window.Width), r.Top - gap - card.Height);
        return new Point(Math.Max(gap, (window.Width - card.Width) / 2), Math.Max(gap, (window.Height - card.Height) / 2));
    }
}
