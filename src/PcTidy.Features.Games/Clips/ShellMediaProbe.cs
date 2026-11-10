using System.Runtime.InteropServices;

namespace PcTidy.Features.Games.Clips;

/// <summary>
/// Real-PC <see cref="IMediaProbe"/>: Windows' property store and IShellItemImageFactory, run on a short-lived STA
/// thread (shell handlers expect one). Read-only. Any failure means "unknown", never an exception.
/// </summary>
public sealed class ShellMediaProbe : IMediaProbe
{
    public MediaInfo? Probe(string path) => OnSta(() => ReadProperties(path));

    public ulong? ThumbnailHash(string path, bool cacheOnly) => OnSta(() => ReadThumbnailHash(path, cacheOnly));

    private static T? OnSta<T>(Func<T?> work) where T : struct
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception) { result = null; }
        }) { IsBackground = true, Name = "PC Tidy media probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(15)) ? result : null;
    }

    // ---------------- property store ----------------

    private static readonly Guid MediaFormat = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid VideoFormat = new("64440491-4C8B-11D1-8B70-080036B11A03");

    private static MediaInfo? ReadProperties(string path)
    {
        var iid = typeof(Native.IPropertyStore).GUID;
        if (Native.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, Native.GpsDefault, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var duration = ReadUInt64(store, MediaFormat, 3);      // System.Media.Duration, 100 ns units
            var width = ReadUInt64(store, VideoFormat, 3);         // System.Video.FrameWidth
            var height = ReadUInt64(store, VideoFormat, 4);        // System.Video.FrameHeight
            if (duration is not > 0 || width is not > 0 || height is not > 0) return null;
            return new MediaInfo(TimeSpan.FromTicks((long)duration.Value), (int)width.Value, (int)height.Value);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static ulong? ReadUInt64(Native.IPropertyStore store, Guid fmtid, uint pid)
    {
        var key = new Native.PropertyKey { FormatId = fmtid, PropertyId = pid };
        var pv = new Native.PropVariant();
        try
        {
            if (store.GetValue(ref key, ref pv) != 0) return null;
            return pv.VarType switch
            {
                Native.VtUI4 => pv.UInt32,
                Native.VtUI8 => pv.UInt64,
                Native.VtI4 => pv.UInt32,
                Native.VtI8 => pv.UInt64,
                _ => null,
            };
        }
        finally { Native.PropVariantClear(ref pv); }
    }

    // ---------------- thumbnails ----------------

    private static ulong? ReadThumbnailHash(string path, bool cacheOnly)
    {
        var bitmap = GetThumbnailBitmap(path, 96, cacheOnly);
        if (bitmap == IntPtr.Zero) return null;
        try { return HashBitmap(bitmap); }
        finally { FreeBitmap(bitmap); }
    }

    /// <summary>
    /// The shell's thumbnail as an HBITMAP (caller frees it with <see cref="FreeBitmap"/>), or IntPtr.Zero.
    /// Call on an STA thread. <paramref name="cacheOnly"/> = never ask the shell to open the file (online-only files).
    /// </summary>
    internal static IntPtr GetThumbnailBitmap(string path, int size, bool cacheOnly)
    {
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return IntPtr.Zero;
        try
        {
            var flags = Native.SiigbfThumbnailOnly | Native.SiigbfBiggerSizeOk | (cacheOnly ? Native.SiigbfInCacheOnly : 0);
            return factory.GetImage(new Native.Size { Width = size, Height = size }, flags, out var bitmap) == 0 ? bitmap : IntPtr.Zero;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    internal static void FreeBitmap(IntPtr bitmap)
    {
        if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
    }

    private static ulong? HashBitmap(IntPtr bitmap)
    {
        var info = new Native.Bitmap();
        if (Native.GetObject(bitmap, Marshal.SizeOf<Native.Bitmap>(), ref info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        int w = info.Width, h = Math.Abs(info.Height);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32, Compression = 0,
        };
        var pixels = new byte[w * h * 4];
        var dc = Native.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, bitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { Native.DeleteDC(dc); }
        return DHash.FromBgra(pixels, w, h);
    }

    private static class Native
    {
        public const int GpsDefault = 0;
        public const ushort VtI4 = 3, VtUI4 = 19, VtI8 = 20, VtUI8 = 21;
        public const int SiigbfBiggerSizeOk = 0x1, SiigbfThumbnailOnly = 0x8, SiigbfInCacheOnly = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey { public Guid FormatId; public uint PropertyId; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort VarType;
            [FieldOffset(8)] public uint UInt32;
            [FieldOffset(8)] public ulong UInt64;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Size { public int Width; public int Height; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Bitmap
        {
            public int Type, Width, Height, WidthBytes;
            public ushort Planes, BitsPixel;
            public IntPtr Bits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }

        // Only the reading slots are called; the order must match the COM vtable.
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int VtableSlotWrite(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int VtableSlotCommit();
        }

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant value);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr handle, int size, ref Bitmap bitmap);
        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    }
}
