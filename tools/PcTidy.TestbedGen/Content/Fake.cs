using System.Text;

namespace PcTidy.TestbedGen.Content;

/// <summary>
/// Seeded fake bytes. Files start with the right magic number for their type (MZ, %PDF, ID3, SQLite...)
/// so type sniffers behave, followed by random filler so no two files are accidental duplicates.
/// All randomness flows from one seeded <see cref="Random"/>, consumed in a fixed order: deterministic.
/// </summary>
internal sealed class Fake
{
    private readonly Random _rng;
    public Fake(int seed) => _rng = new Random(seed);

    public int Next(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);
    public double NextDouble() => _rng.NextDouble();
    public T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    public byte[] Bytes(int count)
    {
        var b = new byte[count];
        _rng.NextBytes(b);
        return b;
    }

    public string Hex(int chars)
    {
        var sb = new StringBuilder(chars);
        for (int i = 0; i < chars; i++) sb.Append("0123456789abcdef"[_rng.Next(16)]);
        return sb.ToString();
    }

    public Guid Guid() => new(Bytes(16));

    /// <summary>Magic header followed by random filler, <paramref name="size"/> bytes in total.</summary>
    public byte[] Bin(ReadOnlySpan<byte> magic, int size)
    {
        var b = Bytes(Math.Max(size, magic.Length));
        magic.CopyTo(b);
        return b;
    }

    /// <summary>Magic given as a string of byte values 0-255 (Latin-1), e.g. "MZ\x90".</summary>
    public byte[] Bin(string magic, int size) => Bin(Encoding.Latin1.GetBytes(magic), size);

    public static byte[] Text(string text) => new UTF8Encoding(false).GetBytes(text);

    // Typed helpers: magic numbers of real formats, sizes in a plausible range.
    public byte[] Exe(int min = 40_000, int max = 400_000) => Bin("MZ\x90\0\x03\0\0\0\x04\0\0\0\xFF\xFF", Next(min, max));
    public byte[] Dll(int min = 20_000, int max = 200_000) => Exe(min, max);
    public byte[] Msi(int min = 200_000, int max = 900_000) => Bin(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, Next(min, max));
    public byte[] Cab(int min = 50_000, int max = 500_000) => Bin("MSCF\0\0\0\0", Next(min, max));
    public byte[] Dump(int min = 60_000, int max = 400_000) => Bin("MDMP\x93\xA7", Next(min, max));
    public byte[] Sqlite(int min = 8_192, int max = 60_000) => Bin("SQLite format 3\0", Next(min, max));
    public byte[] Mp3(int min = 150_000, int max = 600_000) => Bin("ID3\x04\0\0\0\0\0\0", Next(min, max));
    public byte[] Flac(int min = 300_000, int max = 900_000) => Bin("fLaC\0\0\0\x22", Next(min, max));
    public byte[] M4a(int min = 100_000, int max = 400_000) => Bin("\0\0\0\x20" + "ftypM4A \0\0\0\0", Next(min, max));
    public byte[] Mp4(int min = 200_000, int max = 900_000) => Bin("\0\0\0\x20" + "ftypisom\0\0\x02\0", Next(min, max));
    public byte[] Mkv(int min = 200_000, int max = 900_000) => Bin(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, Next(min, max));
    public byte[] Gif(int min = 2_000, int max = 30_000) => Bin("GIF89a", Next(min, max));
    public byte[] SevenZip(int min = 20_000, int max = 300_000) => Bin(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, Next(min, max));
    public byte[] Rar(int min = 20_000, int max = 300_000) => Bin("Rar!\x1A\x07\x01\0", Next(min, max));
    public byte[] LevelDb(int min = 1_000, int max = 20_000) => Bin(Bytes(7), Next(min, max));
    public byte[] Blob(int min = 500, int max = 30_000) => Bytes(Next(min, max));

    /// <summary>A few lines of readable text, so text files aren't binary noise.</summary>
    public byte[] Lines(string title, int lines = 5)
    {
        string[] words = { "budget", "meeting", "draft", "notes", "follow up", "ideas", "todo", "review", "plan", "summary", "call", "list" };
        var sb = new StringBuilder(title).Append("\r\n");
        for (int i = 0; i < lines; i++)
            sb.Append($"- {Pick(words)} {Pick(words)} {Next(1, 100)}\r\n");
        return Text(sb.ToString());
    }
}
