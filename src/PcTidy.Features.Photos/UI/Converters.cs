using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using PcTidy.Core;

namespace PcTidy.Features.Photos.UI;

/// <summary>
/// Photo path → small thumbnail (decoded at 64 px, so a big photo never fills memory). Online-only files are never
/// opened (that would download them): they, and anything that can't be decoded, show the plain icon instead.
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    public const int DecodeWidth = 64;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || CloudFile.IsPlaceholder(info.Attributes)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = fs;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible only when every bound value is false (the "scan to start" card: no preview, not scanning, not done).</summary>
public sealed class AllFalseVisible : IMultiValueConverter
{
    public static readonly AllFalseVisible Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.All(v => v is false) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Index → Visible when it equals the parameter ("0", "1"…), for the Changes | Before → after | Stays put switch.</summary>
public sealed class IndexVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && int.TryParse(parameter as string, out var want) && i == want ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A list's viewport width minus the list's right margin (the parameter), as a row's MaxWidth. GOTCHA: a virtualized
/// list keeps its rows at their widest after the window narrows, so rows would run off the right edge at ~1000 px.
/// </summary>
public sealed class RowMaxWidth : IValueConverter
{
    public static readonly RowMaxWidth Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double margin = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return value is double w && w > margin ? w - margin : double.PositiveInfinity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
