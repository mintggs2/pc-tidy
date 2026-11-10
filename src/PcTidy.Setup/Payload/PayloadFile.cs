using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PcTidy.Setup.Payload;

/// <summary>
/// The app payload is a zip APPENDED to the published Setup exe (the .NET single-file host ignores trailing bytes):
/// <code>
///   [Setup exe (the "stub")][payload.zip][SHA-256 of the zip: 32 bytes][zip length: int64 LE][magic "PCTIDYP1"]
///   [Authenticode certificate table, if the file is signed later]
/// </code>
/// Why appended rather than an embedded resource: Setup is built once, the zip never passes through the compiler
/// (a 100+ MB resource would), it's streamed straight from disk at install time, and uninstall.exe is simply the
/// stub (the first <see cref="PayloadInfo.StubLength"/> bytes). If the exe is signed later, signtool puts the
/// certificate table at the very end and covers the payload with the signature; the reader looks just before it.
/// </summary>
public static class PayloadFile
{
    public static ReadOnlySpan<byte> Magic => "PCTIDYP1"u8;
    public const int TrailerLength = 32 + 8 + 8;

    /// <summary>Finds the payload in an exe, or null if it has none (e.g. a developer build, or uninstall.exe).</summary>
    public static PayloadInfo? Find(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Find(fs, exePath);
    }

    /// <summary>Finds the payload through an already open handle (the admin part keeps one open that denies writes).</summary>
    public static PayloadInfo? Find(Stream fs, string exePath)
    {
        var signatureStart = SignatureStart(fs);
        var end = signatureStart ?? fs.Length;
        var trailer = new byte[TrailerLength];
        // signtool pads the file to an 8-byte boundary before the certificate table, so allow up to 7 zero bytes.
        for (int pad = 0; pad < (signatureStart is null ? 1 : 8); pad++)
        {
            var trailerStart = end - pad - TrailerLength;
            if (trailerStart <= 0) return null;
            fs.Position = trailerStart;
            fs.ReadExactly(trailer);
            if (!trailer.AsSpan(40, 8).SequenceEqual(Magic)) continue;
            var length = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(32, 8));
            var offset = trailerStart - length;
            if (length <= 0 || offset <= 0) return null;
            return new PayloadInfo(exePath, offset, length, trailer[..32]);
        }
        return null;
    }

    /// <summary>Writes stub + zip + trailer to <paramref name="outputExe"/> (what build-release.ps1 does).</summary>
    public static void Write(string stubExe, string zipPath, string outputExe)
    {
        byte[] hash;
        using (var z = File.OpenRead(zipPath)) hash = SHA256.HashData(z);
        using var output = new FileStream(outputExe, FileMode.Create, FileAccess.Write);
        using (var s = File.OpenRead(stubExe)) s.CopyTo(output);
        long zipLength;
        using (var z = File.OpenRead(zipPath)) { z.CopyTo(output); zipLength = z.Length; }
        output.Write(hash);
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(len, zipLength);
        output.Write(len);
        output.Write(Magic);
    }

    /// <summary>
    /// File offset of the Authenticode certificate table (PE optional header, data directory 4), if the exe is signed.
    /// Only a table that sits at the very end of the file counts (that's where signtool puts it).
    /// </summary>
    internal static long? SignatureStart(Stream fs)
    {
        try
        {
            var buf = new byte[4];
            fs.Position = 0x3C;
            fs.ReadExactly(buf);
            var pe = BinaryPrimitives.ReadInt32LittleEndian(buf);
            if (pe <= 0 || pe > fs.Length - 24) return null;
            fs.Position = pe;
            fs.ReadExactly(buf);
            if (!buf.AsSpan().SequenceEqual("PE\0\0"u8)) return null;
            var optional = pe + 24;
            var magic = new byte[2];
            fs.Position = optional;
            fs.ReadExactly(magic);
            var dataDirs = BinaryPrimitives.ReadUInt16LittleEndian(magic) switch
            {
                0x20B => optional + 112, // PE32+
                0x10B => optional + 96,  // PE32
                _ => -1,
            };
            if (dataDirs < 0) return null;
            var entry = new byte[8];
            fs.Position = dataDirs + 4 * 8; // IMAGE_DIRECTORY_ENTRY_SECURITY
            fs.ReadExactly(entry);
            long certOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0, 4));
            long certSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4, 4));
            return certOffset > 0 && certSize > 0 && certOffset + certSize == fs.Length ? certOffset : null;
        }
        catch (EndOfStreamException) { return null; }
    }
}

/// <summary>Where the payload sits inside an exe.</summary>
public sealed record PayloadInfo(string ExePath, long Offset, long Length, byte[] Sha256)
{
    /// <summary>Bytes before the payload: the Setup program itself (= uninstall.exe).</summary>
    public long StubLength => Offset;

    public Stream Open()
    {
        var fs = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        return new SubStream(fs, Offset, Length);
    }

    /// <summary>The payload bytes of an exe that is already open (<paramref name="exe"/> stays open; it isn't disposed).</summary>
    public Stream OpenFrom(Stream exe) => new SubStream(exe, Offset, Length, ownsInner: false);

    /// <summary>True if the zip is complete and unchanged (catches a truncated or damaged download).</summary>
    public bool Verify(CancellationToken ct = default)
    {
        using var s = Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return sha.GetHashAndReset().AsSpan().SequenceEqual(Sha256);
    }
}

/// <summary>Read-only window onto part of a file (so ZipArchive can read the payload in place).</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _ownsInner;
    private long _position;

    public SubStream(Stream inner, long start, long length, bool ownsInner = true)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _ownsInner = ownsInner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var left = _length - _position;
        if (left <= 0) return 0;
        _inner.Position = _start + _position;
        var n = _inner.Read(buffer, offset, (int)Math.Min(count, left));
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => _length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner) _inner.Dispose();
        base.Dispose(disposing);
    }
}
