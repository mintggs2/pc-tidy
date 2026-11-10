using System.IO.Compression;
using System.Text;

namespace PcTidy.TestbedGen.Content;

/// <summary>Small but real files: valid PDF, ZIP, OOXML, Shell Link (.lnk), Internet shortcut, .torrent.
/// Everything is byte-deterministic (fixed timestamps inside archives).</summary>
internal static class Formats
{
    private static readonly DateTimeOffset ZipTime = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One-page PDF with a line of text and a correct xref table.</summary>
    public static byte[] Pdf(string text)
    {
        var safe = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var stream = $"BT /F1 14 Tf 20 60 Td ({safe}) Tj ET";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = ZipTime;
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Minimal valid Word document (opens in Word).</summary>
    public static byte[] Docx(string text) => Zip(
        ("[Content_Types].xml", Fake.Text("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")),
        ("_rels/.rels", Fake.Text("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")),
        ("word/document.xml", Fake.Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p></w:body></w:document>")));

    /// <summary>Zip-shaped Office file (xlsx/pptx): right container, placeholder part. Good enough for type sniffing.</summary>
    public static byte[] OfficeShell(string part, byte[] filler) => Zip(
        ("[Content_Types].xml", Fake.Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
        (part, filler));

    /// <summary>
    /// Shell Link (MS-SHLLINK): header + target ID list + LinkInfo with the local base path (ANSI and Unicode) + terminal block.
    /// Built by hand so it is deterministic (IShellLink.Save would embed machine ids, the target's times and
    /// give the target a link-tracking object id). The ID list is what IShellLink.GetPath reads.
    /// </summary>
    public static byte[] Lnk(string targetPath)
    {
        var idList = Io.Native.SimpleIdList(targetPath);
        var ansi = Encoding.Latin1.GetBytes(targetPath + "\0");
        var unicode = Encoding.Unicode.GetBytes(targetPath + "\0");
        const int infoHeader = 0x24;
        const int volumeIdSize = 0x11;                      // 16-byte VolumeID + empty label
        int volumeOffset = infoHeader;
        int baseOffset = volumeOffset + volumeIdSize;
        int suffixOffset = baseOffset + ansi.Length;
        int baseUnicodeOffset = suffixOffset + 1;
        int suffixUnicodeOffset = baseUnicodeOffset + unicode.Length;
        int infoSize = suffixUnicodeOffset + 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x4C);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(0x01 | 0x02 | 0x80);    // HasLinkTargetIDList | HasLinkInfo | IsUnicode
        w.Write(0x20);                  // target attributes: Archive
        w.Write(0L); w.Write(0L); w.Write(0L);   // creation/access/write times: unknown
        w.Write(0);                     // target size
        w.Write(0);                     // icon index
        w.Write(1);                     // SW_SHOWNORMAL
        w.Write((ushort)0);             // hotkey
        w.Write((ushort)0); w.Write(0); w.Write(0); // reserved
        w.Write((ushort)idList.Length);
        w.Write(idList);

        w.Write(infoSize);
        w.Write(infoHeader);
        w.Write(1);                     // VolumeIDAndLocalBasePath
        w.Write(volumeOffset);
        w.Write(baseOffset);
        w.Write(0);                     // no network link
        w.Write(suffixOffset);
        w.Write(baseUnicodeOffset);
        w.Write(suffixUnicodeOffset);
        w.Write(volumeIdSize);
        w.Write(3);                     // DRIVE_FIXED
        w.Write(0x1234ABCD);            // fake volume serial
        w.Write(0x10);                  // label offset
        w.Write((byte)0);               // empty label
        w.Write(ansi);
        w.Write((byte)0);               // empty common path suffix
        w.Write(unicode);
        w.Write((ushort)0);             // empty unicode suffix
        w.Write(0);                     // TerminalBlock (no extra data)
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Url(string url) => Fake.Text($"[InternetShortcut]\r\nURL={url}\r\n");

    public static byte[] Torrent(string name, long length, byte[] pieceHash)
    {
        using var ms = new MemoryStream();
        void A(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        const string announce = "http://tracker.example.invalid/announce";
        A($"d8:announce{announce.Length}:{announce}4:infod");
        A($"6:lengthi{length}e4:name{Encoding.UTF8.GetByteCount(name)}:{name}12:piece lengthi262144e6:pieces{pieceHash.Length}:");
        ms.Write(pieceHash);
        A("ee");
        return ms.ToArray();
    }
}
