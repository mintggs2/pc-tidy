using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Core;
using PcTidy.Features.Games.Clips;

namespace PcTidy.Features.Games.UI;

/// <summary>
/// Clip and screenshot previews from the Windows shell, loaded off the UI thread on an STA worker. OneDrive online-only
/// files get Windows' cached thumbnail only (the shell is never allowed to open them, so nothing is downloaded).
/// </summary>
internal sealed class ThumbnailLoader
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<ImageSource?> LoadAsync(string path, int size)
    {
        if (_cache.TryGetValue(path, out var hit)) return Task.FromResult(hit);
        var done = new TaskCompletionSource<ImageSource?>();
        var thread = new Thread(() =>
        {
            ImageSource? image = null;
            try { image = Load(path, size); }
            catch (Exception) { /* no preview */ }
            done.TrySetResult(image);
        }) { IsBackground = true, Name = "PC Tidy clip preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.ContinueWith(t =>
        {
            _cache[path] = t.Result;
            return t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static ImageSource? Load(string path, int size)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); } // attributes only: never opens the file
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var bitmap = ShellMediaProbe.GetThumbnailBitmap(path, size, cacheOnly: CloudFile.IsPlaceholder(attributes));
        if (bitmap == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { ShellMediaProbe.FreeBitmap(bitmap); }
    }
}
