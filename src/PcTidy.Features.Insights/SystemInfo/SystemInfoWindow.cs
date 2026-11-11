using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PcTidy.Core;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

using PcTidy.Features.Insights.SystemInfo;

namespace PcTidy.Features.Insights;

/// <summary>
/// Shows <see cref="SystemInfoView"/> as a modal over the main window (layout spec §4.5 and §4.6 item 4): the window behind is
/// dimmed and blurred, the panel uses the theme's own brushes (light and dark), and there is one button, Close (Esc works too).
/// A card link closes the panel and opens Insights.
/// </summary>
public static class SystemInfoWindow
{
    /// <summary>Opens System info over <paramref name="owner"/> and returns when it closes.</summary>
    /// <param name="services">The app's services: IKnownLocations (required), ISystemInfo, and IShellFlowHost for card links.</param>
    public static void Show(Window owner, IServiceProvider services)
    {
        var view = new SystemInfoView(services);
        var window = Create(owner, view, out var picked);
        var behind = owner?.Content as UIElement;
        var oldEffect = behind?.Effect;
        if (behind is not null) behind.Effect = new BlurEffect { Radius = 6, RenderingBias = RenderingBias.Performance };
        try
        {
            window.ShowDialog();
        }
        finally
        {
            if (behind is not null) behind.Effect = oldEffect;
        }
        if (picked() is not null)
        {
            var shell = services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? services.GetService(typeof(IFlowHost)) as IShellFlowHost;
            shell?.NavigateTo(InsightsIds.FeatureId);
        }
    }

    /// <summary>The dimmed modal window around <paramref name="view"/> (not shown). <paramref name="picked"/> returns the card link chosen, if any.</summary>
    internal static Window Create(Window? owner, SystemInfoView view, out Func<string?> picked)
    {
        string? card = null;
        picked = () => card;

        var close = new Button
        {
            Content = "Close", Appearance = ControlAppearance.Primary, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0), IsDefault = true, IsCancel = true,
        };
        AutomationProperties.SetName(close, "Close System info");

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(view);
        Grid.SetRow(close, 1);
        stack.Children.Add(close);

        var panel = new Border
        {
            Child = stack, Padding = new Thickness(24), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            MaxWidth = 920, MaxHeight = 680, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        };
        // Solid theme brushes (not the see-through card fill), so the panel reads well over the dimmed window in light and dark.
        panel.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        panel.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 };
        AutomationProperties.SetName(panel, "System info");

        var window = new Window
        {
            Title = "System info",
            Content = new Grid { Children = { panel } },
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            // The dim layer: black at 45% over the (blurred) main window, the same in both themes.
            Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)),
        };
        AutomationProperties.SetName(window, "System info");
        window.SetResourceReference(Window.FontFamilyProperty, "TidyText");
        Place(window, owner);

        close.Click += (_, _) => window.Close();
        view.OpenCardRequested += (_, id) =>
        {
            card = id;
            window.Close();
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { window.Close(); e.Handled = true; }
        };
        window.Loaded += (_, _) => close.Focus();
        return window;
    }

    /// <summary>Covers the owner's area exactly (so only it is dimmed); without a shown owner it's a plain centred window.</summary>
    private static void Place(Window window, Window? owner)
    {
        if (owner is { IsLoaded: true } && PresentationSource.FromVisual(owner) is { CompositionTarget: { } target })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var topLeft = target.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
            window.Left = topLeft.X;
            window.Top = topLeft.Y;
            window.Width = owner.ActualWidth;
            window.Height = owner.ActualHeight;
            return;
        }
        if (owner is not null) window.Owner = owner;
        window.WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        window.Width = 1000;
        window.Height = 740;
    }
}
