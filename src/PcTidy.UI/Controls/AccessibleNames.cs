using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Controls;

/// <summary>
/// WPF-UI's own small buttons inside its templates have no screen-reader name: the ✕ on an InfoBar and the clear
/// (✕) button in a search box are announced as just "button". <see cref="NameWithin"/> names them under a page; the
/// shell calls it after every navigation, and pages call it when they open a notice later.
/// </summary>
/// <remarks>
/// GOTCHA: a class handler for Loaded doesn't work here: WPF only broadcasts Loaded to elements with an instance
/// handler, so InfoBars and text boxes inside pages never raised it.
/// </remarks>
public static class AccessibleNames
{
    public const string CloseMessage = "Close this message";

    /// <summary>Names the unnamed template buttons of every InfoBar and WPF-UI text box under <paramref name="root"/>.</summary>
    public static void NameWithin(DependencyObject? root)
    {
        if (root is null) return;
        foreach (var d in Descendants(root).ToList())
        {
            if (d is InfoBar bar) NameUnnamedButtons(bar, CloseMessage);
            else if (d is Wpf.Ui.Controls.TextBox box)
            {
                var field = AutomationProperties.GetName(box);
                NameUnnamedButtons(box, string.IsNullOrWhiteSpace(field) ? "Clear the text" : "Clear: " + field);
            }
        }
    }

    /// <summary>Gives every button in the control's template that has no name of its own this name.</summary>
    public static void NameUnnamedButtons(DependencyObject root, string name)
    {
        foreach (var b in Descendants(root).OfType<ButtonBase>())
        {
            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))) continue;
            if (b.Content is string text && !string.IsNullOrWhiteSpace(text)) continue;
            AutomationProperties.SetName(b, name);
            if (b.ToolTip is null) b.ToolTip = name;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!ReferenceEquals(cur, root)) yield return cur;
            int n = VisualTreeHelper.GetChildrenCount(cur);
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
    }
}
