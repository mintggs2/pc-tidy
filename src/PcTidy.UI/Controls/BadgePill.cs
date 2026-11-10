using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using PcTidy.Core.Model;

namespace PcTidy.UI.Controls;

public enum PillTone { Auto, Success, Caution, Critical, Neutral, Accent, Info }

/// <summary>
/// Small rounded status badge. Set <see cref="Badge"/> for scan items (Safe = success, Check = caution,
/// Permanent = critical) or <see cref="Text"/> + <see cref="Tone"/> for anything else (run status, impact...).
/// </summary>
public class BadgePill : Border
{
    private readonly TextBlock _text = new();
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BadgePill), new PropertyMetadata("", (d, _) => ((BadgePill)d).Update()));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(PillTone), typeof(BadgePill), new PropertyMetadata(PillTone.Auto, (d, _) => ((BadgePill)d).Update()));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(Badge?), typeof(BadgePill), new PropertyMetadata(null, (d, _) => ((BadgePill)d).Update()));
    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot), typeof(bool), typeof(BadgePill), new PropertyMetadata(true, (d, _) => ((BadgePill)d).Update()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public PillTone Tone { get => (PillTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Badge? Badge { get => (Badge?)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public bool ShowDot { get => (bool)GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }

    public BadgePill()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 2, 9, 3);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        _text.FontSize = 12;
        _text.FontWeight = FontWeights.SemiBold;
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "TidyText");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(_dot);
        sp.Children.Add(_text);
        Child = sp;
        Update();
    }

    public static string LabelFor(Badge badge) => badge switch
    {
        Core.Model.Badge.Safe => "Safe",
        Core.Model.Badge.Check => "Check",
        _ => "Permanent",
    };

    private (string Text, PillTone Tone) Resolve()
    {
        if (Badge is { } b)
            return (LabelFor(b), b switch { Core.Model.Badge.Safe => PillTone.Success, Core.Model.Badge.Check => PillTone.Caution, _ => PillTone.Critical });
        var text = Text ?? "";
        if (Tone != PillTone.Auto) return (text, Tone);
        var tone = text.ToLowerInvariant() switch
        {
            "safe" or "low" or "recommended" or "on" or "restorable" or "done" or "completed" => PillTone.Success,
            "check" or "medium" or "needs restart" or "partly undone" or "with skips" or "interrupted" => PillTone.Caution,
            "permanent" or "high" or "can't undo" or "failed" => PillTone.Critical,
            "simulated" => PillTone.Info,
            _ => PillTone.Neutral,
        };
        return (text, tone);
    }

    private void Update()
    {
        var (text, tone) = Resolve();
        _text.Text = text;
        AutomationProperties.SetName(this, text);
        Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _dot.Visibility = ShowDot ? Visibility.Visible : Visibility.Collapsed;
        var (bg, fg) = tone switch
        {
            PillTone.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            PillTone.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            PillTone.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            PillTone.Accent => ("TidyAccentSoftBrush", "TidyAccentBrush"),
            PillTone.Info => ("SystemFillColorAttentionBackgroundBrush", "SystemFillColorAttentionBrush"),
            _ => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        SetResourceReference(BackgroundProperty, bg);
        _text.SetResourceReference(TextBlock.ForegroundProperty, fg);
        _dot.SetResourceReference(Shape.FillProperty, fg);
        if (tone == PillTone.Neutral)
        {
            SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            BorderThickness = new Thickness(1);
        }
        else BorderThickness = new Thickness(0);
    }
}
