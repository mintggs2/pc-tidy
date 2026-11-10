using System.Windows;
using System.Windows.Controls;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Composition.Demo;

/// <summary>Demo page for a section the demo has no sample data for: what it does, in the section's own words.</summary>
public sealed class DemoPlaceholderPage : UserControl
{
    public DemoPlaceholderPage(string title, string summary, string icon)
    {
        var root = new StackPanel();
        root.SetResourceReference(MarginProperty, "TidyPageMargin");
        var t = new TextBlock { Text = title };
        t.SetResourceReference(StyleProperty, "PageTitle");
        var s = new TextBlock { Text = summary };
        s.SetResourceReference(StyleProperty, "PageSubtitle");
        root.Children.Add(t);
        root.Children.Add(s);
        var card = new Border { Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(0) };
        card.SetResourceReference(StyleProperty, "TidyCard");
        card.Child = new EmptyState
        {
            Icon = Enum.TryParse<SymbolRegular>(icon, out var sym) ? sym : SymbolRegular.Box24,
            Title = "No sample data for this section",
            Message = $"The demo only shows sample results for a few sections. In the full app, {title} looks at this PC and shows what it finds. Nothing here can change your PC.",
        };
        root.Children.Add(card);
        Content = root;
    }
}
