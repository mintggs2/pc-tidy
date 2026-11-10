using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using PcTidy.Features.Duplicates.Hashing;

namespace PcTidy.Features.Duplicates.Images;

/// <summary>What the side-by-side compare shows about a picture: pixel size, camera and date taken (EXIF).</summary>
public sealed record PhotoFacts(int? Width, int? Height, string? Camera, DateTime? Taken)
{
    public static readonly PhotoFacts None = new(null, null, null, null);

    private const int MaxExifRead = 256 * 1024;
    private const ushort TagMake = 0x010F, TagModel = 0x0110, TagDateTime = 0x0132, TagExifIfd = 0x8769, TagDateTimeOriginal = 0x9003;

    /// <summary>
    /// Reads the facts (read-only, bounded: the picture header through WIC without decoding the pixels, and at most
    /// the first 256 KB for EXIF). Never call it for online-only files. Anything unreadable gives empty facts.
    /// </summary>
    public static PhotoFacts Read(string path)
    {
        int? width = null, height = null;
        byte[] head;
        try
        {
            using var fs = new FileStream(Win32Path.Raw(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            head = new byte[(int)Math.Min(MaxExifRead, fs.Length)];
            int read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (read < head.Length) head = head[..read];
            fs.Position = 0;
            try
            {
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException or FileFormatException or OverflowException)
            {
                // Not a picture WIC can read: only the EXIF facts (if any) are shown.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }
        var (camera, taken) = ParseExif(head);
        return new PhotoFacts(width, height, camera, taken);
    }

    /// <summary>Camera ("Make Model") and date taken from a JPEG's EXIF block or a TIFF-style raw header. Bounds-checked.</summary>
    public static (string? Camera, DateTime? Taken) ParseExif(ReadOnlySpan<byte> data)
    {
        var tiff = TiffBlock(data);
        if (tiff.Length < 8) return (null, null);
        bool little = tiff[0] == 'I';
        string? make = null, model = null;
        DateTime? fallback = null, original = null;
        uint exifIfd = 0;
        foreach (var (tag, type, count, value) in Entries(tiff, U32(tiff, 4, little), little))
        {
            switch (tag)
            {
                case TagMake: make = Ascii(tiff, type, count, value, little); break;
                case TagModel: model = Ascii(tiff, type, count, value, little); break;
                case TagDateTime: fallback = Date(Ascii(tiff, type, count, value, little)); break;
                case TagExifIfd: exifIfd = value; break;
            }
        }
        if (exifIfd != 0)
            foreach (var (tag, type, count, value) in Entries(tiff, exifIfd, little))
                if (tag == TagDateTimeOriginal) original = Date(Ascii(tiff, type, count, value, little));
        // Many cameras repeat the maker in the model ("Canon" + "Canon EOS R6").
        string? camera = model is null ? make
            : make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model
            : $"{make} {model}";
        return (string.IsNullOrWhiteSpace(camera) ? null : camera.Trim(), original ?? fallback);
    }

    private static ReadOnlySpan<byte> TiffBlock(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 4 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42) || (d[0] == 'M' && d[1] == 'M' && d[3] == 42))) return d;
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return ReadOnlySpan<byte>.Empty;
        int pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) break;
            byte marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xDA or 0xD9) break;
            int len = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 2)..]);
            if (len < 2 || pos + 2 + len > d.Length) break;
            var segment = d.Slice(pos + 4, len - 2);
            if (marker == 0xE1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8)) return segment[6..];
            pos += 2 + len;
        }
        return ReadOnlySpan<byte>.Empty;
    }

    private static List<(ushort Tag, ushort Type, uint Count, uint Value)> Entries(ReadOnlySpan<byte> tiff, uint ifd, bool little)
    {
        var list = new List<(ushort, ushort, uint, uint)>();
        if (ifd < 8 || ifd + 2 > tiff.Length) return list;
        int count = U16(tiff, (int)ifd, little);
        for (int i = 0; i < count && i < 512; i++)
        {
            int e = (int)ifd + 2 + i * 12;
            if (e + 12 > tiff.Length) break;
            list.Add((U16(tiff, e, little), U16(tiff, e + 2, little), U32(tiff, e + 4, little), U32(tiff, e + 8, little)));
        }
        return list;
    }

    /// <summary>ASCII value: inline when 4 bytes or fewer, else at the offset. Trailing NULs and spaces dropped.</summary>
    private static string? Ascii(ReadOnlySpan<byte> tiff, ushort type, uint count, uint value, bool little)
    {
        if (type != 2 || count == 0 || count > 256) return null;
        ReadOnlySpan<byte> bytes;
        if (count <= 4)
        {
            Span<byte> inline = stackalloc byte[4];
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(inline, value); else BinaryPrimitives.WriteUInt32BigEndian(inline, value);
            return Clean(Encoding.ASCII.GetString(inline[..(int)count]));
        }
        if (value + count > tiff.Length) return null;
        bytes = tiff.Slice((int)value, (int)count);
        return Clean(Encoding.ASCII.GetString(bytes));
    }

    private static string? Clean(string s)
    {
        var cut = s.IndexOf('\0');
        var t = (cut >= 0 ? s[..cut] : s).Trim();
        return t.Length == 0 || t.Any(ch => ch < 32 || ch > 126) ? null : t;
    }

    private static DateTime? Date(string? text)
        => text is not null && text.Length >= 19
           && DateTime.TryParseExact(text[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt.Year > 1900
            ? dt : null;

    private static ushort U16(ReadOnlySpan<byte> d, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(d[at..]) : BinaryPrimitives.ReadUInt16BigEndian(d[at..]);

    private static uint U32(ReadOnlySpan<byte> d, int at, bool little)
        => at + 4 > d.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(d[at..]) : BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
}
