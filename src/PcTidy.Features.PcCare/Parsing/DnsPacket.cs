using System.Buffers.Binary;
using System.Text;

namespace PcTidy.Features.PcCare.Parsing;

/// <summary>
/// The smallest possible DNS-over-UDP query (RFC 1035): one A-record question with recursion desired, and a check
/// that a reply matches it. Used only to time lookups for the read-only DNS speed test; answers are never used.
/// </summary>
public static class DnsPacket
{
    public static byte[] BuildQuery(ushort id, string name)
    {
        var labels = name.Trim('.').Split('.');
        if (labels.Length == 0 || labels.Any(l => l.Length is 0 or > 63 || l.Any(c => c > 127)))
            throw new ArgumentException("Not a plain host name.", nameof(name));
        var buffer = new List<byte>(32 + name.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // one question
        buffer.AddRange(header.ToArray());
        foreach (var label in labels)
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
        buffer.AddRange(new byte[] { 0, 1, 0, 1 }); // QTYPE A, QCLASS IN
        return buffer.ToArray();
    }

    /// <summary>True when <paramref name="reply"/> is a response to query <paramref name="id"/> that the server answered (any RCODE but SERVFAIL/REFUSED).</summary>
    public static bool IsAnswer(ReadOnlySpan<byte> reply, ushort id)
    {
        if (reply.Length < 12) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(reply) != id) return false;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(reply[2..]);
        bool isResponse = (flags & 0x8000) != 0;
        int rcode = flags & 0x000F;
        return isResponse && rcode is not (2 or 5);
    }

    /// <summary>Median of the timings (null for none).</summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
