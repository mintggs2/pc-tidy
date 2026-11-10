using System.Runtime.InteropServices;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Photos.Photos;

/// <summary>Where a photo's date came from (shown in the preview, so the user knows how sure PC Tidy is).</summary>
public enum DateSource
{
    /// <summary>The camera's "date taken" (EXIF DateTimeOriginal, or Windows' System.Photo.DateTaken).</summary>
    Taken,
    /// <summary>A video's "media created" date (Windows' System.Media.DateEncoded / System.ItemDate).</summary>
    MediaCreated,
    /// <summary>No date inside the file: the file's modified date.</summary>
    Modified,
}

public readonly record struct MediaDate(DateTime Local, DateSource Source)
{
    public string Describe() => Source switch
    {
        DateSource.Taken => $"taken {Local:d MMM yyyy}",
        DateSource.MediaCreated => $"recorded {Local:d MMM yyyy}",
        _ => $"no date inside the file, so its modified date ({Local:d MMM yyyy})",
    };
}

/// <summary>Read-only access to dates Windows' property system knows about a file. Swappable for tests.</summary>
public interface IMediaProperties
{
    /// <summary>System.Photo.DateTaken as local time, or null.</summary>
    DateTime? PhotoDateTaken(string path);
    /// <summary>System.Media.DateEncoded, else System.ItemDate, as local time, or null.</summary>
    DateTime? VideoDateCreated(string path);
}

/// <summary>
/// The date a photo or video is sorted by: the camera's date taken when the file has one, else the modified date.
/// Never called for online-only files (reading them would download them).
/// </summary>
public sealed class MediaDateResolver
{
    private readonly IMediaProperties _properties;
    private readonly Func<DateTime> _nowLocal;

    public MediaDateResolver(IMediaProperties? properties = null, Func<DateTime>? nowLocal = null)
    {
        _properties = properties ?? WindowsMediaProperties.Instance;
        _nowLocal = nowLocal ?? (() => DateTime.Now);
    }

    /// <param name="depth">Deep also asks Windows for photos without an EXIF block PC Tidy reads itself (HEIC, PNG, some raw).</param>
    public MediaDate Resolve(FileEntry entry, ScanDepth depth)
    {
        if (!entry.IsCloudPlaceholder && !entry.IsDirectory)
        {
            DateTime? inside = null;
            DateSource source = DateSource.Taken;
            if (PhotoFormats.IsVideo(entry.Name))
            {
                inside = Safe(() => _properties.VideoDateCreated(entry.FullPath));
                source = DateSource.MediaCreated;
            }
            else if (PhotoFormats.IsPhoto(entry.Name))
            {
                if (PhotoFormats.HasExif(entry.Name)) inside = ExifReader.ReadDateTaken(entry.FullPath);
                if (inside is null && depth == ScanDepth.Deep) inside = Safe(() => _properties.PhotoDateTaken(entry.FullPath));
            }
            if (inside is { } d && IsPlausible(d)) return new MediaDate(d, source);
        }
        return new MediaDate(entry.LastWriteUtc.ToLocalTime(), DateSource.Modified);
    }

    /// <summary>Cameras with a flat clock battery write 1970 or dates in the future: those aren't trusted.</summary>
    private bool IsPlausible(DateTime d) => d.Year >= 1990 && d <= _nowLocal().AddDays(2);

    private static DateTime? Safe(Func<DateTime?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads dates through Windows' property system (the same values Explorer shows in "Date taken" / "Media created").
/// Read-only: the store is opened with GPS_DEFAULT (read-only) and the interface below doesn't even declare the
/// write methods.
/// </summary>
public sealed class WindowsMediaProperties : IMediaProperties
{
    public static readonly WindowsMediaProperties Instance = new();

    private static readonly PropertyKey PhotoDateTakenKey = new(new Guid("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);
    private static readonly PropertyKey MediaDateEncodedKey = new(new Guid("2E4B640D-5019-46D8-8881-55414CC5CAA0"), 100);
    private static readonly PropertyKey ItemDateKey = new(new Guid("F7DB74B4-4287-4103-AFBA-F1B13DCD75CF"), 13);

    public DateTime? PhotoDateTaken(string path) => ReadDate(path, PhotoDateTakenKey);

    public DateTime? VideoDateCreated(string path) => ReadDate(path, MediaDateEncodedKey) ?? ReadDate(path, ItemDateKey);

    private static DateTime? ReadDate(string path, PropertyKey key)
    {
        var iid = typeof(IPropertyStoreRead).GUID;
        if (NativeMethods.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GpsDefault, ref iid, out var store) != 0 || store is null)
            return null;
        try
        {
            var k = key;
            if (store.GetValue(ref k, out var value) != 0) return null;
            try
            {
                // Property-system dates are UTC FILETIMEs; Explorer shows them in local time, and so does PC Tidy.
                return value.Type == VtFiletime && value.FileTime > 0 ? DateTime.FromFileTimeUtc(value.FileTime).ToLocalTime() : null;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private const int GpsDefault = 0;
    private const ushort VtFiletime = 64;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT (24 bytes on x64). Only VT_FILETIME is read; everything else is cleared unread.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long FileTime;
    }

    /// <summary>The read half of IPropertyStore (GetCount, GetAt, GetValue). SetValue and Commit are deliberately absent.</summary>
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStoreRead
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStoreRead? store);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int PropVariantClear(ref PropVariant value);
    }
}
