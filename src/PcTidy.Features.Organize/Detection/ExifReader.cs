using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PcTidy.Features.Organize.Detection;

/// <summary>
/// Minimal, read-only EXIF "date taken" reader for JPEG (APP1 Exif segment) and TIFF-based raw files
/// (TIFF, DNG, NEF, CR2, ARW, ORF, RW2 start with a TIFF header). Reads at most the first 256 KB and
/// bounds-checks every offset, so a damaged or hostile file can only ever yield "no date".
/// </summary>
public static class ExifReader
{
    private const int MaxRead = 256 * 1024;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagDateTimeDigitized = 0x9004;

    /// <summary>Date the photo was taken (camera local time), or null. Never call for cloud placeholders.</summary>
    public static DateTime? ReadDateTaken(string path)
    {
        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);
            data = new byte[(int)Math.Min(MaxRead, fs.Length)];
            int read = 0, n;
            while (read < data.Length && (n = fs.Read(data, read, data.Length - read)) > 0) read += n;
            if (read < data.Length) data = data[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(data);
    }

    public static DateTime? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && IsTiffHeader(data)) return ParseTiff(data);
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;

        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) return null;
            byte marker = data[pos + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }
            if (marker == 0xFF) { pos++; continue; }               // fill byte
            if (marker == 0xDA || marker == 0xD9) return null;     // image data starts: no EXIF before it
            int len = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            if (len < 2 || pos + 2 + len > data.Length) return null;
            var segment = data.Slice(pos + 4, len - 2);
            if (marker == 0xE1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8))
                return ParseTiff(segment[6..]);
            pos += 2 + len;
        }
        return null;
    }

    private static bool IsTiffHeader(ReadOnlySpan<byte> d)
        => (d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42)
        // Olympus ORF ("IIRO"/"IIRS") and Panasonic RW2 ("IIU\0") use TIFF layout with a different magic.
        || (d[0] == 'I' && d[1] == 'I' && (d[2] == 'R' || d[2] == 'U'));

    private static DateTime? ParseTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little = tiff[0] == 'I';
        uint ifd0 = U32(tiff, 4, little);

        DateTime? fallback = null;
        uint exifIfd = 0;
        foreach (var (tag, type, count, valueOffset) in Entries(tiff, ifd0, little))
        {
            if (tag == TagDateTime) fallback = ReadDate(tiff, type, count, valueOffset, little);
            else if (tag == TagExifIfd) exifIfd = valueOffset;
        }
        if (exifIfd != 0)
        {
            DateTime? digitized = null;
            foreach (var (tag, type, count, valueOffset) in Entries(tiff, exifIfd, little))
            {
                if (tag == TagDateTimeOriginal && ReadDate(tiff, type, count, valueOffset, little) is { } original) return original;
                if (tag == TagDateTimeDigitized) digitized = ReadDate(tiff, type, count, valueOffset, little);
            }
            if (digitized is not null) return digitized;
        }
        return fallback;
    }

    private static List<(ushort Tag, ushort Type, uint Count, uint Value)> Entries(ReadOnlySpan<byte> tiff, uint ifdOffset, bool little)
    {
        var list = new List<(ushort, ushort, uint, uint)>();
        if (ifdOffset < 8 || ifdOffset + 2 > tiff.Length) return list;
        int count = U16(tiff, (int)ifdOffset, little);
        for (int i = 0; i < count && i < 512; i++)
        {
            int e = (int)ifdOffset + 2 + i * 12;
            if (e + 12 > tiff.Length) break;
            list.Add((U16(tiff, e, little), U16(tiff, e + 2, little), U32(tiff, e + 4, little), U32(tiff, e + 8, little)));
        }
        return list;
    }

    /// <summary>EXIF dates are ASCII "yyyy:MM:dd HH:mm:ss" (type 2, 20 bytes incl. NUL). Parsed culture-invariant.</summary>
    private static DateTime? ReadDate(ReadOnlySpan<byte> tiff, ushort type, uint count, uint offset, bool little)
    {
        if (type != 2 || count < 19 || count > 64 || offset + count > tiff.Length) return null;
        var text = Encoding.ASCII.GetString(tiff.Slice((int)offset, 19));
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt.Year > 1900
            ? dt
            : null;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(d[at..]) : BinaryPrimitives.ReadUInt16BigEndian(d[at..]);

    private static uint U32(ReadOnlySpan<byte> d, int at, bool little)
        => at + 4 > d.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(d[at..]) : BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
}
