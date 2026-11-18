namespace PcTidy.Features.Organize.Detection;

/// <summary>
/// File signatures ("magic bytes") from a file's first bytes, for "Sort by type" (3.3). Only ever given bytes that were
/// already read (never for online-only files). Conservative: unknown means "don't know", never "wrong".
/// </summary>
public static class FileSignature
{
    private static bool Starts(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic) => head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic);

    /// <summary>A Windows program (MZ header).</summary>
    public static bool IsProgram(ReadOnlySpan<byte> head) => Starts(head, "MZ"u8);

    public static bool IsPdf(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..Math.Min(head.Length, 1024)].IndexOf("%PDF"u8) >= 0;

    public static bool IsImage(ReadOnlySpan<byte> head)
        => Starts(head, [0xFF, 0xD8, 0xFF])                                      // JPEG
           || Starts(head, [0x89, 0x50, 0x4E, 0x47])                              // PNG
           || Starts(head, "GIF8"u8) || Starts(head, "BM"u8)                      // GIF, BMP
           || (Starts(head, "RIFF"u8) && head.Length >= 12 && head[8..12].SequenceEqual("WEBP"u8))
           || (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))          // HEIC/HEIF/AVIF (ISO media)
           || Starts(head, [0x49, 0x49, 0x2A, 0x00]) || Starts(head, [0x4D, 0x4D, 0x00, 0x2A]) // TIFF (and many camera RAWs)
           || Starts(head, [0x00, 0x00, 0x01, 0x00]);                             // ICO

    /// <summary>An MPEG transport stream: the 0x47 sync byte every 188 bytes.</summary>
    public static bool IsMpegTs(ReadOnlySpan<byte> head) => head.Length > 376 && head[0] == 0x47 && head[188] == 0x47 && head[376] == 0x47;

    /// <summary>A few well-known kinds, or null. Used only to spot a file that is clearly something else.</summary>
    public static string? Known(ReadOnlySpan<byte> head)
    {
        if (IsProgram(head)) return "program";
        if (IsPdf(head)) return "pdf";
        if (IsImage(head)) return "image";
        if (Starts(head, "PK\u0003\u0004"u8)) return "zip";
        if (Starts(head, "Rar!"u8)) return "rar";
        if (Starts(head, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return "7z";
        return null;
    }
}
