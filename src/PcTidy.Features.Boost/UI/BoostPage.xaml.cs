using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Boost.UI;

/// <summary>
/// Game boost page. Fixed height: only the games list and the apps list scroll. Works at 1000 px wide
/// (two columns of at least 300 px).
/// </summary>
public partial class BoostPage : UserControl, IFixedHeightPage
{
    public BoostPage(BoostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

/// <summary>true → Collapsed, false → Visible (empty-list hints).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
