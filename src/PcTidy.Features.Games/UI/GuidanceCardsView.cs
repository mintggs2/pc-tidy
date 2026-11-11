using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Catalog.Games;
using PcTidy.UI.Controls;

namespace PcTidy.Features.Games.UI;

/// <summary>
/// Games → Tips: the text-only guidance cards (C1 Cap your FPS, C2 Why is my game slow?, C7 Reflex / Anti-Lag, C9 shader
/// caches), each with its measured numbers, hardware and source. Nothing on this tab changes anything.
/// </summary>
public sealed class GuidanceCardsView : UserControl
{
    public GuidanceCardsView(IReadOnlyList<GuidanceCard> cards)
    {
        AutomationProperties.SetName(this, "Game tips");
        var grid = new ResponsiveGrid { MinItemWidth = 320, MaxColumns = 2 };
        foreach (var card in cards) grid.Children.Add(Card(card));
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Tips with measured numbers. Each number names the PC it was measured on and its source, so you can judge whether it fits yours. Nothing here changes your PC.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }.Styled("Secondary"));
        stack.Children.Add(grid);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
            Focusable = false,
        };
    }

    /// <summary>One card: title, summary, the steps, then each measurement with hardware and source.</summary>
    internal static Border Card(GuidanceCard card)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = card.Title, TextWrapping = TextWrapping.Wrap }.Styled("CardTitle"));
        body.Children.Add(new TextBlock { Text = card.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) }.Styled("Secondary"));
        foreach (var point in card.Points)
            body.Children.Add(new TextBlock { Text = "•  " + point, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }.Styled("Body"));
        foreach (var m in card.Measured)
        {
            // Readable brush and wrapped, never cut: the source is often the long part (UI review H1/M5).
            body.Children.Add(new TextBlock { Text = m.Line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }.Styled("Caption").Secondary());
            body.Children.Add(new TextBlock { Text = "Source: " + m.SourceUrl, TextWrapping = TextWrapping.Wrap }.Styled("Caption").Secondary());
        }
        var border = new Border { Child = body, Margin = new Thickness(0, 0, 0, 0) };
        border.SetResourceReference(StyleProperty, "TidyCard");
        AutomationProperties.SetName(border, card.Title);
        return border;
    }
}

internal static class StyleExtensions
{
    public static T Styled<T>(this T element, string styleKey) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    /// <summary>The secondary text brush (4.5:1 in both themes), for facts in caption-sized text.</summary>
    public static TextBlock Secondary(this TextBlock element)
    {
        element.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return element;
    }
}
