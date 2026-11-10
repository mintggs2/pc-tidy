using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Learning.UI;

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TextToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NotNull : IValueConverter
{
    public static readonly NotNull Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A symbol name to the icon element a WPF-UI button shows.</summary>
public sealed class SymbolToIcon : IValueConverter
{
    public static readonly SymbolToIcon Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SymbolRegular s ? new SymbolIcon(s) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
