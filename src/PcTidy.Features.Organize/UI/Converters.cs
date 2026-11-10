using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Organize.UI;

/// <summary>"Image24" → SymbolRegular.Image24 (unknown names fall back to a plain document icon).</summary>
public sealed class SymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && Enum.TryParse<SymbolRegular>(s, out var symbol) ? symbol : SymbolRegular.Document24;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → Visible; false/null/empty string → Collapsed. Parameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Indent width (double) → left margin for tree rows.</summary>
public sealed class IndentMargin : IValueConverter
{
    public static readonly IndentMargin Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness(value is double d ? d : 0, 3, 0, 3);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Non-null/non-empty → true (for InfoBar.IsOpen).</summary>
public sealed class NotNull : IValueConverter
{
    public static readonly NotNull Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? s.Length > 0 : value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Boolean negation (two-way), e.g. for an "any (or)" radio button bound to MatchAll.</summary>
public sealed class Not : IValueConverter
{
    public static readonly Not Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
