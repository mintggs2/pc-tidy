using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Bytes reused in several places: exact duplicates, near-duplicate image variants, size-equal decoys.</summary>
internal sealed class SharedFiles
{
    public SharedFiles(Ctx c)
    {
        HolidayPhoto = c.Photo(c.Seed + 100, new DateTime(2025, 6, 14, 10, 15, 0));
        ReportDocx = Formats.Docx("Report final. Quarterly numbers and next steps.");
        // Near-duplicates: one scene, four encodings.
        BeachOriginal = c.Photo(c.Seed + 200, new DateTime(2025, 6, 15, 16, 40, 0), quality: 92, width: 160, height: 120);
        BeachReencoded = c.Photo(c.Seed + 200, new DateTime(2025, 6, 15, 16, 40, 0), quality: 55, width: 160, height: 120);
        BeachResized = c.Photo(c.Seed + 200, null, quality: 85, scale: 0.5, width: 160, height: 120);
        BeachPng = c.Png(c.Seed + 200, 160, 120);
        // Decoys: same size and same first 64 KB (defeats a partial-hash pre-filter), different last byte.
        DecoyA = c.F.Bytes(100_000);
        DecoyB = (byte[])DecoyA.Clone();
        DecoyB[^1] ^= 0xFF;
        SameSize1 = c.F.Bytes(12_345);
        SameSize2 = c.F.Bytes(12_345);
    }

    public byte[] HolidayPhoto { get; }
    public byte[] ReportDocx { get; }
    public byte[] BeachOriginal { get; }
    public byte[] BeachReencoded { get; }
    public byte[] BeachResized { get; }
    public byte[] BeachPng { get; }
    public byte[] DecoyA { get; }
    public byte[] DecoyB { get; }
    public byte[] SameSize1 { get; }
    public byte[] SameSize2 { get; }
}
