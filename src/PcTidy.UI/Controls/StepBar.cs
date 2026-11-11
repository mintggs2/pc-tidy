using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Controls;

/// <summary>The shared (1) Scan → (2) Preview → (3) Confirm → (4) Done flow indicator.</summary>
public class StepBar : ContentControl
{
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(int), typeof(StepBar), new PropertyMetadata(1, (d, _) => ((StepBar)d).Build()));
    public static readonly DependencyProperty CaptionsProperty = DependencyProperty.Register(
        nameof(Captions), typeof(string), typeof(StepBar), new PropertyMetadata("", (d, _) => ((StepBar)d).Build()));

    /// <summary>1 = Scan, 2 = Preview, 3 = Confirm, 4 = Done.</summary>
    public int Current { get => (int)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    /// <summary>Optional "a|b|c|d" captions under each step.</summary>
    public string Captions { get => (string)GetValue(CaptionsProperty); set => SetValue(CaptionsProperty, value); }

    private static readonly string[] Labels = { "Scan", "Preview", "Confirm", "Done" };

    public StepBar()
    {
        Focusable = false;
        Build();
    }

    /// <summary>Convenience: sets step and captions in one go.</summary>
    public void Set(int current, params string[] captions)
    {
        Captions = string.Join("|", captions);
        Current = current;
    }

    private void Build()
    {
        var caps = (Captions ?? "").Split('|');
        var grid = new Grid();
        for (int i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (i < 3) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 24 });
        }

        for (int i = 0; i < 4; i++)
        {
            int step = i + 1;
            bool done = step < Current || (step == 4 && Current >= 4);
            bool current = step == Current && step != 4;

            var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center };
            if (done)
            {
                circle.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                var icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark16, FontSize = 14, Filled = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(Control.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
                circle.Child = icon;
            }
            else
            {
                var tb = new TextBlock { Text = step.ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "TidyText");
                if (current)
                {
                    circle.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
                }
                else
                {
                    circle.BorderThickness = new Thickness(1.5);
                    circle.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                }
                circle.Child = tb;
            }

            var label = new TextBlock { Text = Labels[i], FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "TidyText");
            label.FontWeight = current || (step == 4 && Current >= 4) ? FontWeights.SemiBold : FontWeights.Normal;
            label.SetResourceReference(TextBlock.ForegroundProperty, step <= Current ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

            var textStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(label);
            string cap = i < caps.Length ? caps[i] : "";
            if (step == 4 && string.IsNullOrEmpty(cap)) cap = "+ Undo";
            if (!string.IsNullOrEmpty(cap))
            {
                var c = new TextBlock { Text = cap, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
                c.SetResourceReference(TextBlock.FontFamilyProperty, "TidyText");
                c.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                textStack.Children.Add(c);
            }

            var stepPanel = new StackPanel { Orientation = Orientation.Horizontal };
            stepPanel.Children.Add(circle);
            stepPanel.Children.Add(textStack);
            Grid.SetColumn(stepPanel, i * 2);
            grid.Children.Add(stepPanel);

            if (i < 3)
            {
                var line = new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Border.BackgroundProperty, step < Current ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
                Grid.SetColumn(line, i * 2 + 1);
                grid.Children.Add(line);
            }
        }

        var card = new Border { Padding = new Thickness(20, 12, 20, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = card;

        int shown = Math.Clamp(Current, 1, 4);
        AutomationProperties.SetName(this, $"Step {shown} of 4: {Labels[shown - 1]}");
    }
}
