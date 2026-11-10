using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Apps.UI;

/// <summary>
/// Apps page (Plan §8-4). Fills the window; only the app list, the details and the leftover list scroll.
/// All changes go through IFlowHost (confirm, restore point, journal, undo where possible).
/// </summary>
public partial class AppsView : UserControl, IFixedHeightPage
{
    public AppsView(AppsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var loadedOnce = false;
        Loaded += async (_, _) =>
        {
            if (loadedOnce) return;
            loadedOnce = true;
            await viewModel.LoadAsync();
        };
        Unloaded += (_, _) => viewModel.Cancel();
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

/// <summary>Null or empty text → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
