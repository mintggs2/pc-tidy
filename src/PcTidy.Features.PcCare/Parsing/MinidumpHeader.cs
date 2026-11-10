using System.Buffers.Binary;

namespace PcTidy.Features.PcCare.Parsing;

public sealed record MinidumpBugcheck(uint Code, IReadOnlyList<ulong> Parameters, bool Is64Bit);

/// <summary>
/// Reads the stop code from the start of a Windows kernel dump (.dmp in C:\Windows\Minidump). Only the fixed header is read:
/// DUMP_HEADER64 starts "PAGEDU64", BugCheckCode is a ULONG at 0x38 and the four ULONG64 parameters follow at 0x40;
/// DUMP_HEADER32 starts "PAGEDUMP", BugCheckCode at 0x28 and four ULONG parameters at 0x2C.
/// </summary>
public static class MinidumpHeader
{
    /// <summary>Bytes needed from the start of the file.</summary>
    public const int HeaderBytes = 0x60;

    public static MinidumpBugcheck? TryParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x3C) return null;
        if (!header[..4].SequenceEqual("PAGE"u8)) return null;

        if (header[4..8].SequenceEqual("DU64"u8))
        {
            if (header.Length < HeaderBytes) return null;
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
            var p = new ulong[4];
            for (int i = 0; i < 4; i++) p[i] = BinaryPrimitives.ReadUInt64LittleEndian(header[(0x40 + 8 * i)..]);
            return new MinidumpBugcheck(code, p, true);
        }

        if (header[4..8].SequenceEqual("DUMP"u8))
        {
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(header[0x28..]);
            var p = new ulong[4];
            for (int i = 0; i < 4; i++) p[i] = BinaryPrimitives.ReadUInt32LittleEndian(header[(0x2C + 4 * i)..]);
            return new MinidumpBugcheck(code, p, false);
        }

        return null;
    }
}
