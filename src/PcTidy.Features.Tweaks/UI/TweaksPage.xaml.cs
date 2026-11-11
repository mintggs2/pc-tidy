using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.Features.Tweaks.UI;

/// <summary>
/// The Tweaks page. All logic is in <see cref="TweaksViewModel"/>; this wires the empty state's button and asks the
/// same "Turn on Expert mode?" question as Settings before the switch turns on.
/// </summary>
public partial class TweaksPage : UserControl, IFixedHeightPage
{
    public TweaksPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TweaksViewModel old) old.ConfirmExpertMode = null;
            if (e.NewValue is TweaksViewModel vm) vm.ConfirmExpertMode = ConfirmExpertModeAsync;
            OnLayoutWidth(this, EventArgs.Empty);
        };
        Loaded += (_, _) =>
        {
            OnLayoutWidth(this, EventArgs.Empty);
            if (Window.GetWindow(this) is { } w) { w.SizeChanged -= OnLayoutWidth; w.SizeChanged += OnLayoutWidth; }
        };
        Unloaded += (_, _) => { if (Window.GetWindow(this) is { } w) w.SizeChanged -= OnLayoutWidth; };
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TweaksViewModel vm && vm.ScanCommand.CanExecute(null)) vm.ScanCommand.Execute(null);
    }

    // ------------------------------------------------------------------ view width (List below ~1100 px unless the user picked)

    private void OnLayoutWidth(object? sender, EventArgs e)
    {
        if (DataContext is not TweaksViewModel vm) return;
        var window = Window.GetWindow(this);
        vm.SetWindowWidth(window?.ActualWidth > 0 ? window.ActualWidth : ActualWidth);
    }

    // ------------------------------------------------------------------ preset popover

    private FrameworkElement? _presetButton;

    /// <summary>A preset button opens the "what will change" popover under itself; nothing is ticked yet.</summary>
    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TweaksViewModel vm || sender is not FrameworkElement button || button.DataContext is not TweaksViewModel.PresetChoice choice) return;
        _presetButton = button;
        PresetPopup.PlacementTarget = button;
        vm.PreviewPresetCommand.Execute(choice);
    }

    private void OnPresetPopupOpened(object? sender, EventArgs e)
    {
        // Keyboard users land on the main choice; Esc or Cancel closes without ticking.
        Dispatcher.BeginInvoke(() =>
        {
            if (PresetConfirmButton.IsEnabled) PresetConfirmButton.Focus();
            else (PresetPopup.Child as FrameworkElement)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPresetPopupClosed(object? sender, EventArgs e) => _presetButton?.Focus();

    private void OnPresetPopupKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || DataContext is not TweaksViewModel vm) return;
        vm.CancelPresetCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Settings' Expert mode question, word for word. Without the shell's dialog host the switch just turns on.</summary>
    private async Task<bool> ConfirmExpertModeAsync()
    {
        if (FindDialogHost() is not { } host) return true;
        var body = new TextBlock
        {
            Text = "Expert mode shows tweaks that can cause problems on some PCs (for example turning off search indexing). "
                   + "Each one still shows its warning, makes a backup first and can be undone. Harmful changes stay unavailable, even here.",
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

    /// <summary>The window's dialog host (the shell has one ContentDialogHost over the whole window).</summary>
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

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

/// <summary>
/// The list's viewport width as a row's MaxWidth (0, before the first layout, means no limit). GOTCHA: a virtualized
/// list keeps its rows at their widest after the window narrows, so rows would run off the right edge at ~1000 px.
/// </summary>
public sealed class RowMaxWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double w && w > 0 ? w : double.PositiveInfinity;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
