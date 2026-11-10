using System.Text;

namespace PcTidy.Features.Duplicates.Signatures;

/// <summary>What a file's first bytes say it is.</summary>
public enum ContentKind
{
    Unknown,
    Jpeg, Png, Gif, Bmp, Webp, Tiff,
    Pdf, Rtf, Html,
    Zip, SevenZip, Rar, Gzip,
    IsoMedia, Matroska, Avi,
    Mp3, Wav, Ogg, Flac,
    /// <summary>Old Office / Windows Installer "compound file": .doc, .xls, .ppt, .msi… (can't tell which).</summary>
    OleCompound,
    /// <summary>A Windows program or library ("MZ").</summary>
    WindowsProgram,
    /// <summary>A Linux program or library (ELF).</summary>
    LinuxProgram,
}

/// <summary>One kind of content: how to say it, which extensions are right for it, and the safe fix (if any).</summary>
public sealed record ContentType(ContentKind Kind, string Words, IReadOnlySet<string> Extensions, string? FixExtension, bool IsProgram = false);

/// <summary>
/// Content signatures ("magic numbers"): reads only the first bytes of a file and never runs or parses anything else.
/// JPEG <c>FF D8 FF</c>, PNG <c>89 50 4E 47</c>, PDF <c>%PDF</c>, ZIP <c>PK 03 04</c> (Office files, .jar and .apk are
/// ZIP too), GIF, MP4/MOV/HEIC <c>ftyp</c>, Windows programs <c>MZ</c>, and more. A mismatch is only reported for
/// extensions in this table (or a program dressed up as a document), so plain-text formats never raise false alarms.
/// </summary>
public static class FileSignatures
{
    /// <summary>Bytes needed to tell every kind apart.</summary>
    public const int HeadBytes = 64;

    private static IReadOnlySet<string> Set(string list) => new HashSet<string>(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<ContentKind, ContentType> Types = new ContentType[]
    {
        new(ContentKind.Jpeg, "a JPEG picture", Set("jpg jpeg jpe jfif"), "jpg"),
        new(ContentKind.Png, "a PNG picture", Set("png apng"), "png"),
        new(ContentKind.Gif, "a GIF picture", Set("gif"), "gif"),
        new(ContentKind.Bmp, "a BMP picture", Set("bmp dib"), "bmp"),
        new(ContentKind.Webp, "a WebP picture", Set("webp"), "webp"),
        // Most camera raw formats are TIFF inside.
        new(ContentKind.Tiff, "a TIFF picture", Set("tif tiff dng nef nrw cr2 arw srw sr2 pef 3fr erf kdc mos iiq raw"), "tif"),
        new(ContentKind.Pdf, "a PDF document", Set("pdf ai"), "pdf"),
        new(ContentKind.Rtf, "a rich-text document", Set("rtf"), "rtf"),
        // A web page saved under another name is usually a failed download; which name is right can't be known.
        new(ContentKind.Html, "a web page", Set("html htm xhtml shtml mht mhtml"), null),
        new(ContentKind.Zip, "a ZIP archive", Set("zip docx docm dotx dotm xlsx xlsm xltx xltm pptx pptm potx ppsx odt ods odp odg odf epub jar apk aab xapk apks xpi vsix nupkg appx appxbundle msix msixbundle 3mf kmz cbz ipa xps oxps whl aar war ear sketch pages numbers key idml xd crx"), "zip"),
        new(ContentKind.SevenZip, "a 7-Zip archive", Set("7z"), "7z"),
        new(ContentKind.Rar, "a RAR archive", Set("rar cbr"), "rar"),
        new(ContentKind.Gzip, "a GZip archive", Set("gz tgz"), "gz"),
        // ISO media ("ftyp"): the fix comes from the brand (mp4, mov, m4a, heic, avif…).
        new(ContentKind.IsoMedia, "an MP4-family video, sound or picture", Set("mp4 m4v m4a m4b m4p mov qt 3gp 3g2 heic heif hif avif cr3 f4v f4a mp4v"), "mp4"),
        new(ContentKind.Matroska, "a Matroska video (MKV or WebM)", Set("mkv webm mka mk3d"), "mkv"),
        new(ContentKind.Avi, "an AVI video", Set("avi"), "avi"),
        new(ContentKind.Mp3, "an MP3 sound file", Set("mp3"), "mp3"),
        new(ContentKind.Wav, "a WAV sound file", Set("wav wave"), "wav"),
        new(ContentKind.Ogg, "an Ogg sound or video file", Set("ogg oga ogv opus spx"), "ogg"),
        new(ContentKind.Flac, "a FLAC sound file", Set("flac"), "flac"),
        // .doc/.xls/.ppt and .msi all look the same from outside: no rename (it could make an installer runnable).
        new(ContentKind.OleCompound, "an old-style Office or installer file", Set("doc dot xls xlt xla ppt pot pps msi msp mst msg pub vsd mpp"), null),
        new(ContentKind.WindowsProgram, "a Windows program", Set("exe dll sys scr com cpl ocx efi mui drv ax tlb acm ime pyd node winmd msstyles"), null, IsProgram: true),
        new(ContentKind.LinuxProgram, "a Linux program", Set("so elf ko"), null, IsProgram: true),
    }.ToDictionary(t => t.Kind);

    /// <summary>Names people open without a second thought: a program behind one of these is a security warning.</summary>
    public static readonly IReadOnlySet<string> DocumentLike = Set(
        "pdf doc docx docm xls xlsx xlsm ppt pptx rtf txt csv md odt ods odp epub jpg jpeg png gif bmp webp heic tif tiff svg " +
        "mp3 mp4 mov m4a wav avi mkv zip rar 7z html htm");

    /// <summary>Names Windows (or an installed runtime) runs anyway: a program behind one of these isn't in disguise.</summary>
    public static readonly IReadOnlySet<string> Runnable = Set(
        "exe dll sys scr com cpl ocx efi mui drv ax tlb acm ime pyd node winmd msstyles so elf ko msi msp mst jar bat cmd ps1 vbs vbe js jse wsf wsh hta lnk pif appref-ms msix appx");

    private static readonly IReadOnlyDictionary<string, ContentKind> ByExtension = Types.Values
        .SelectMany(t => t.Extensions.Select(e => (e, t.Kind)))
        .GroupBy(x => x.e, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when PC Tidy knows what this extension's content should look like (or it's a document a program could hide behind).</summary>
    public static bool IsJudged(string extension) => ByExtension.ContainsKey(extension) || DocumentLike.Contains(extension);

    /// <summary>The kind of content in <paramref name="head"/> (the first <see cref="HeadBytes"/> bytes or fewer).</summary>
    public static ContentKind Detect(ReadOnlySpan<byte> head)
    {
        if (StartsWith(head, 0xFF, 0xD8, 0xFF)) return ContentKind.Jpeg;
        if (StartsWith(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return ContentKind.Png;
        if (Ascii(head, 0, "GIF87a") || Ascii(head, 0, "GIF89a")) return ContentKind.Gif;
        if (Ascii(head, 0, "%PDF-")) return ContentKind.Pdf;
        if (StartsWith(head, 0x50, 0x4B) && head.Length >= 4 && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6) || (head[2] == 7 && head[3] == 8))) return ContentKind.Zip;
        if (StartsWith(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return ContentKind.SevenZip;
        if (Ascii(head, 0, "Rar!\u001A\u0007")) return ContentKind.Rar;
        if (StartsWith(head, 0x1F, 0x8B, 0x08)) return ContentKind.Gzip;
        if (Ascii(head, 4, "ftyp")) return ContentKind.IsoMedia;
        if (StartsWith(head, 0x1A, 0x45, 0xDF, 0xA3)) return ContentKind.Matroska;
        if (Ascii(head, 0, "RIFF") && head.Length >= 12)
        {
            if (Ascii(head, 8, "WEBP")) return ContentKind.Webp;
            if (Ascii(head, 8, "WAVE")) return ContentKind.Wav;
            if (Ascii(head, 8, "AVI ")) return ContentKind.Avi;
            return ContentKind.Unknown;
        }
        if (Ascii(head, 0, "ID3")) return ContentKind.Mp3;
        if (Ascii(head, 0, "OggS")) return ContentKind.Ogg;
        if (Ascii(head, 0, "fLaC")) return ContentKind.Flac;
        if (StartsWith(head, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) return ContentKind.OleCompound;
        if (Ascii(head, 0, "{\\rtf")) return ContentKind.Rtf;
        if (StartsWith(head, 0x7F, 0x45, 0x4C, 0x46)) return ContentKind.LinuxProgram;
        // "MZ" plus a believable header: e_lfanew (offset 60) points past the DOS header, inside the first 64 KB.
        if (StartsWith(head, 0x4D, 0x5A) && head.Length >= 64 && BitConverter.ToInt32(head[60..64]) is >= 64 and < 64 * 1024) return ContentKind.WindowsProgram;
        // Two bytes alone are too weak for BMP: the reserved fields must be zero too.
        if (StartsWith(head, 0x42, 0x4D) && head.Length >= 10 && head[6] == 0 && head[7] == 0 && head[8] == 0 && head[9] == 0) return ContentKind.Bmp;
        if (StartsWith(head, 0x49, 0x49, 0x2A, 0x00) || StartsWith(head, 0x4D, 0x4D, 0x00, 0x2A)) return ContentKind.Tiff;
        if (LooksLikeHtml(head)) return ContentKind.Html;
        return ContentKind.Unknown;
    }

    /// <summary>ISO media brand → the right extension ("qt  " → mov, "heic" → heic…); null when the brand is unknown.</summary>
    public static string? IsoMediaExtension(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12 || !Ascii(head, 4, "ftyp")) return null;
        var brand = Encoding.ASCII.GetString(head[8..12]).Trim().ToLowerInvariant();
        return brand switch
        {
            "isom" or "iso2" or "iso4" or "iso5" or "iso6" or "mp41" or "mp42" or "avc1" or "dash" or "mmp4" or "msnv" => "mp4",
            "qt" => "mov",
            "m4a" or "m4b" => "m4a",
            "m4v" or "m4vh" or "m4vp" => "m4v",
            "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1" => "heic",
            "avif" or "avis" => "avif",
            "crx" => "cr3",
            _ when brand.StartsWith("3gp", StringComparison.Ordinal) => "3gp",
            _ when brand.StartsWith("3g2", StringComparison.Ordinal) => "3g2",
            _ => null,
        };
    }

    /// <summary>
    /// What's wrong with this file's name, if anything: (real type, is it a program, safe fix extension or null).
    /// Null when the content matches the extension, or when PC Tidy can't tell.
    /// </summary>
    public static (ContentType Real, string? FixExtension)? Mismatch(string extension, ReadOnlySpan<byte> head)
    {
        if (!IsJudged(extension)) return null;
        var kind = Detect(head);
        if (kind == ContentKind.Unknown || !Types.TryGetValue(kind, out var real)) return null;
        if (real.Extensions.Contains(extension)) return null;
        bool knownExtension = ByExtension.ContainsKey(extension);
        if (real.IsProgram)
            // A program is only worth a warning when it's dressed as something people open without thinking.
            return !Runnable.Contains(extension) && (DocumentLike.Contains(extension) || knownExtension) ? (real, null) : null;
        if (!knownExtension) return null; // e.g. a .txt that holds a PDF: plain-text names aren't judged
        var fix = kind == ContentKind.IsoMedia ? IsoMediaExtension(head)
            // A ".doc" that's really a ZIP is almost always a .docx with the wrong name: Word opens it again.
            : kind == ContentKind.Zip && OfficeZipNames.TryGetValue(extension, out var office) ? office
            : real.FixExtension;
        // Never suggest a name Windows would run.
        if (fix is not null && Runnable.Contains(fix)) fix = null;
        return (real, fix);
    }

    private static readonly IReadOnlyDictionary<string, string> OfficeZipNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["doc"] = "docx", ["xls"] = "xlsx", ["ppt"] = "pptx", ["dot"] = "dotx", ["xlt"] = "xltx", ["pot"] = "potx", ["pps"] = "ppsx",
    };

    private static bool StartsWith(ReadOnlySpan<byte> d, params byte[] magic) => d.Length >= magic.Length && d[..magic.Length].SequenceEqual(magic);

    private static bool Ascii(ReadOnlySpan<byte> d, int at, string text)
    {
        if (d.Length < at + text.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (d[at + i] != (byte)text[i]) return false;
        return true;
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> head)
    {
        int i = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0; // UTF-8 BOM
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        var rest = Encoding.ASCII.GetString(head[i..]).ToLowerInvariant();
        return rest.StartsWith("<!doctype html", StringComparison.Ordinal) || rest.StartsWith("<html", StringComparison.Ordinal);
    }
}
