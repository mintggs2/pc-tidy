using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Photos.UI;

/// <summary>The Desktop tidy page. Fixed height like the standard flow page: only the lists scroll, the action bar stays in view.</summary>
public partial class DesktopTidyView : UserControl, IFixedHeightPage
{
    public DesktopTidyView()
    {
        InitializeComponent();
    }

    /// <summary>The number box's own spin and clear buttons have no names: give them plain ones for screen readers.</summary>
    private void OnDaysLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject box) return;
        var stack = new Stack<DependencyObject>();
        stack.Push(box);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is ButtonBase b && string.IsNullOrEmpty(AutomationProperties.GetName(b)))
            {
                var part = b.Name ?? "";
                AutomationProperties.SetName(b,
                    part.Contains("Increment", StringComparison.OrdinalIgnoreCase) || part.Contains("Up", StringComparison.Ordinal) ? "One day more"
                    : part.Contains("Decrement", StringComparison.OrdinalIgnoreCase) || part.Contains("Down", StringComparison.Ordinal) ? "One day less"
                    : part.Contains("Clear", StringComparison.OrdinalIgnoreCase) ? "Clear the number of days"
                    : "Days untouched");
            }
            for (int i = VisualTreeHelper.GetChildrenCount(cur) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
    }
}
