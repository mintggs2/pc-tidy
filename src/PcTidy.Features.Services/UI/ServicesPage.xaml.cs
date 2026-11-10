using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.Features.Services.UI;

/// <summary>
/// The Services page. All logic is in <see cref="ServicesViewModel"/>; this wires the empty state's button and asks
/// the same "Turn on Expert mode?" question as Settings before the switch turns on.
/// </summary>
public partial class ServicesPage : UserControl, IFixedHeightPage
{
    public ServicesPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServicesViewModel old) old.ConfirmExpertMode = null;
            if (e.NewValue is ServicesViewModel vm) vm.ConfirmExpertMode = ConfirmExpertModeAsync;
        };
        SizeChanged += (_, _) => UpdateCompact();
        DataContextChanged += (_, _) => UpdateCompact();
    }

    /// <summary>Page height under which the header folds up (one-line subtitle, profile chips) so the list keeps room for rows.</summary>
    internal const double CompactBelowHeight = 700;

    private void UpdateCompact()
    {
        if (ActualHeight <= 0) return;
        bool compact = ActualHeight < CompactBelowHeight;
        if (DataContext is ServicesViewModel vm) vm.IsCompact = compact;
        SubtitleText.MaxHeight = compact ? SubtitleText.FontSize * 1.45 : double.PositiveInfinity;
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServicesViewModel vm && vm.ScanCommand.CanExecute(null)) vm.ScanCommand.Execute(null);
    }

    /// <summary>Settings' Expert mode question. Without the shell's dialog host the switch just turns on.</summary>
    private async Task<bool> ConfirmExpertModeAsync()
    {
        if (FindDialogHost() is not { } host) return true;
        var body = new TextBlock
        {
            Text = "Expert mode also offers services that only help in some setups (for example the print service on a PC without a printer). "
                   + "Each one says what might stop working, makes a restore point first and can be undone. Security, update and anti-cheat services stay unavailable, even here.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        };
        body.SetResourceReference(StyleProperty, "Body");
        var dialog = new ContentDialog(host)
        {
            Title = "Turn on Expert mode?",
            Content = body,
            PrimaryButtonText = "Turn on",
            CloseButtonText = "Keep it off",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private ContentDialogHost? FindDialogHost()
    {
        if (Window.GetWindow(this) is not { } window) return null;
        var stack = new Stack<DependencyObject>();
        stack.Push(window);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is ContentDialogHost host) return host;
            for (int i = VisualTreeHelper.GetChildrenCount(cur) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
        return null;
    }
}
