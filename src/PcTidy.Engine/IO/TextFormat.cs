using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Engine.IO;

/// <summary>Line-break style of a text file.</summary>
internal enum LineBreaks { None, CrLf, Lf, Cr, Mixed }

/// <summary>
/// How a text file is stored: encoding, byte-order mark and line breaks. Config edits are written back exactly the way
/// the file was (an app that reads its INI as ANSI or UTF-16 must still be able to read it after PC Tidy's edit).
/// </summary>
internal sealed record TextFormat(string Name, Encoding Encoding, byte[] Preamble, LineBreaks Breaks)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>The default for a file PC Tidy creates: UTF-8 without a BOM, line breaks as given.</summary>
    public static TextFormat NewFile { get; } = new("utf-8", StrictUtf8, Array.Empty<byte>(), LineBreaks.None);

    /// <summary>Plain-English description for messages ("UTF-8 with BOM, CRLF line breaks").</summary>
    public string Describe()
    {
        var enc = Name switch
        {
            "utf-8" => "UTF-8",
            "utf-8-bom" => "UTF-8 with BOM",
            "utf-16le" => "UTF-16",
            "utf-16be" => "UTF-16 (big-endian)",
            "utf-32le" => "UTF-32",
            _ => $"ANSI (code page {Encoding.CodePage})",
        };
        return Breaks switch
        {
            LineBreaks.CrLf => enc + ", Windows line breaks",
            LineBreaks.Lf => enc + ", Unix line breaks",
            LineBreaks.Cr => enc + ", old Mac line breaks",
            _ => enc,
        };
    }

    /// <summary>Detects the format from the file's bytes: BOM first, then UTF-16 without BOM, then strict UTF-8, else ANSI.</summary>
    public static TextFormat Detect(byte[] bytes)
    {
        var (name, encoding, preamble) = DetectEncoding(bytes);
        var text = Decode(bytes, encoding, preamble.Length);
        return new TextFormat(name, encoding, preamble, BreaksOf(text));
    }

    /// <summary>Overrides from the plan (<c>Args["encoding"]</c>): utf-8, utf-8-bom, utf-16le, utf-16be, ansi. Null = keep.</summary>
    public TextFormat WithEncoding(string? requested) => requested?.Trim().ToLowerInvariant() switch
    {
        null or "" or "keep" => this,
        "utf-8" or "utf8" => this with { Name = "utf-8", Encoding = StrictUtf8, Preamble = Array.Empty<byte>() },
        "utf-8-bom" or "utf8bom" => this with { Name = "utf-8-bom", Encoding = StrictUtf8, Preamble = new byte[] { 0xEF, 0xBB, 0xBF } },
        "utf-16le" or "utf-16" or "unicode" => this with { Name = "utf-16le", Encoding = Strict(new UnicodeEncoding(false, false)), Preamble = new byte[] { 0xFF, 0xFE } },
        "utf-16be" => this with { Name = "utf-16be", Encoding = Strict(new UnicodeEncoding(true, false)), Preamble = new byte[] { 0xFE, 0xFF } },
        "ansi" => this with { Name = "ansi", Encoding = Ansi(), Preamble = Array.Empty<byte>() },
        _ => throw new ArgumentException($"Unknown text encoding '{requested}'."),
    };

    public string Decode(byte[] bytes) => Decode(bytes, Encoding, StartsWith(bytes, Preamble) ? Preamble.Length : 0);

    /// <summary>
    /// The bytes for <paramref name="text"/> in this format. <paramref name="lineBreaks"/>: "keep" (default) rewrites
    /// the text's line breaks to the file's own style when the file used one style throughout (a mixed file, or one with
    /// no line breaks, is written as given); "crlf" / "lf" force a style; "asis" writes them as given.
    /// Returns null when the text has characters this encoding can't hold (it is never written lossily).
    /// </summary>
    public byte[]? Encode(string text, string? lineBreaks = null)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..]; // the format decides about the BOM, never the text
        var target = lineBreaks?.Trim().ToLowerInvariant() switch
        {
            null or "" or "keep" => Breaks is LineBreaks.CrLf or LineBreaks.Lf or LineBreaks.Cr ? Breaks : LineBreaks.None,
            "crlf" => LineBreaks.CrLf,
            "lf" => LineBreaks.Lf,
            "asis" => LineBreaks.None,
            _ => throw new ArgumentException($"Unknown line-break style '{lineBreaks}'."),
        };
        if (target != LineBreaks.None) text = NormaliseBreaks(text, target);
        byte[] body;
        try { body = Encoding.GetBytes(text); }
        catch (EncoderFallbackException) { return null; }
        return Preamble.Length == 0 ? body : Preamble.Concat(body).ToArray();
    }

    private static string Decode(byte[] bytes, Encoding encoding, int skip)
    {
        try { return encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes, skip, bytes.Length - skip); }
    }

    private static (string Name, Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] b)
    {
        if (StartsWith(b, new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return ("utf-32le", Strict(new UTF32Encoding(false, false)), b[..4]);
        if (StartsWith(b, new byte[] { 0xEF, 0xBB, 0xBF })) return ("utf-8-bom", StrictUtf8, b[..3]);
        if (StartsWith(b, new byte[] { 0xFF, 0xFE })) return ("utf-16le", Strict(new UnicodeEncoding(false, false)), b[..2]);
        if (StartsWith(b, new byte[] { 0xFE, 0xFF })) return ("utf-16be", Strict(new UnicodeEncoding(true, false)), b[..2]);
        if (LooksLikeUtf16(b, littleEndian: true)) return ("utf-16le", Strict(new UnicodeEncoding(false, false)), Array.Empty<byte>());
        if (LooksLikeUtf16(b, littleEndian: false)) return ("utf-16be", Strict(new UnicodeEncoding(true, false)), Array.Empty<byte>());
        try
        {
            StrictUtf8.GetCharCount(b);
            return ("utf-8", StrictUtf8, Array.Empty<byte>());
        }
        catch (DecoderFallbackException)
        {
            var ansi = Ansi();
            return ("ansi-" + ansi.CodePage, ansi, Array.Empty<byte>());
        }
    }

    /// <summary>UTF-16 without a BOM (some games write INIs that way): mostly-ASCII text has a zero in every other byte.</summary>
    private static bool LooksLikeUtf16(byte[] b, bool littleEndian)
    {
        if (b.Length < 4 || b.Length % 2 != 0) return false;
        int pairs = b.Length / 2, zeroHigh = 0, zeroLow = 0;
        for (int i = 0; i < b.Length; i += 2)
        {
            var (low, high) = littleEndian ? (b[i], b[i + 1]) : (b[i + 1], b[i]);
            if (high == 0) zeroHigh++;
            if (low == 0) zeroLow++;
        }
        return zeroHigh * 10 >= pairs * 9 && zeroLow * 20 < pairs;
    }

    private static LineBreaks BreaksOf(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds switch
        {
            0 => LineBreaks.None,
            > 1 => LineBreaks.Mixed,
            _ => crlf > 0 ? LineBreaks.CrLf : lf > 0 ? LineBreaks.Lf : LineBreaks.Cr,
        };
    }

    private static string NormaliseBreaks(string text, LineBreaks to)
    {
        var nl = to switch { LineBreaks.CrLf => "\r\n", LineBreaks.Cr => "\r", _ => "\n" };
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(nl);
            }
            else if (c == '\n') sb.Append(nl);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool StartsWith(byte[] b, byte[] prefix) => prefix.Length > 0 && b.AsSpan().StartsWith(prefix);

    /// <summary>A copy that throws on characters/bytes it can't represent instead of silently substituting.</summary>
    private static Encoding Strict(Encoding e)
    {
        var c = (Encoding)e.Clone();
        c.EncoderFallback = EncoderFallback.ExceptionFallback;
        c.DecoderFallback = DecoderFallback.ExceptionFallback;
        return c;
    }

    /// <summary>This PC's ANSI code page (GetACP), strict: characters it can't hold are refused, never replaced by '?'.</summary>
    private static Encoding Ansi()
    {
        var cp = (int)GetACP();
        var provider = CodePagesEncodingProvider.Instance;
        return provider.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
               ?? Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
