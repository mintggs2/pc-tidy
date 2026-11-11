using System.Text;
using PcTidy.Core;

namespace PcTidy.Features.Measure.Import;

/// <summary>
/// Reads a frame-time recording the user made with a free tool and picked or dropped onto the page. Read-only: the file is
/// opened for reading and shared, never changed. Refuses online-only OneDrive files (reading one would download it),
/// files over the size limit, binary and compressed files, and anything without a frame-time column, each with a plain message.
/// </summary>
public static class RecordingImporter
{
    public static readonly IReadOnlyList<string> Extensions = new[] { ".csv", ".json", ".txt" };

    public static string LabelFor(RecordingFormat format) => format switch
    {
        RecordingFormat.PresentMonV1Csv => "PresentMon 1.x-style CSV (also OCAT, FrameView, CapFrameX CSV)",
        RecordingFormat.PresentMonV2Csv => "PresentMon 2.x CSV",
        RecordingFormat.PresentMonV2EarlyCsv => "PresentMon 2.0-2.3 CSV",
        RecordingFormat.CapFrameXJson => "CapFrameX session (JSON)",
        _ => format.ToString(),
    };

    public static bool HasKnownExtension(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a file on a background thread.</summary>
    public static Task<ImportResult> ImportAsync(string path, ImportLimits? limits = null, CancellationToken ct = default)
        => Task.Run(() => Import(path, limits ?? ImportLimits.Default, ct), ct);

    public static ImportResult Import(string path, ImportLimits limits, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        if (!HasKnownExtension(path)) return ImportResult.Fail("Pick a .csv or .json recording (PresentMon, CapFrameX, OCAT or FrameView).");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ImportResult.Fail("That file isn't there any more.");
            if (CloudFile.IsPlaceholder(info.Attributes)) return ImportResult.Fail("This file is online-only in OneDrive. Open it once so it downloads, then import it.");
            if (info.Length == 0) return ImportResult.Fail("This file is empty.");
            if (info.Length > limits.MaxBytes)
                return ImportResult.Fail($"This file is larger than {Sizes.Format(limits.MaxBytes)}. Record 60 to 120 seconds per run and import those files.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Import(stream, name, limits, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportResult.Fail("PC Tidy couldn't read that file (it may be open in another program or blocked).");
        }
    }

    /// <summary>Reads from a stream (tests feed generated text this way).</summary>
    public static ImportResult Import(Stream stream, string fileName, ImportLimits limits, CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length > limits.MaxBytes)
            return ImportResult.Fail($"This file is larger than {Sizes.Format(limits.MaxBytes)}.");
        var bounded = new LimitedStream(stream, limits.MaxBytes);
        var buffered = new BufferedStream(bounded, 64 * 1024);

        // Sniff the start: byte-order mark, compressed or binary content, JSON or CSV.
        var head = new byte[4096];
        int n = 0;
        // BufferedStream can't un-read, so the sniffed bytes are put back in front with a small concatenating stream.
        while (n < head.Length)
        {
            int r = buffered.Read(head, n, head.Length - n);
            if (r == 0) break;
            n += r;
        }
        if (n == 0) return ImportResult.Fail("This file is empty.");
        if (n >= 2 && head[0] == 0x1F && head[1] == 0x8B) return ImportResult.Fail("This file is compressed (gzip). Unpack it first, or export the recording as CSV or JSON.");
        if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K') return ImportResult.Fail("This is a zip file. Unzip it first and import the CSV or JSON inside.");

        var encoding = DetectEncoding(head.AsSpan(0, n), out int bomLength);
        if (encoding is UTF8Encoding && head.AsSpan(bomLength, n - bomLength).IndexOf((byte)0) >= 0)
            return ImportResult.Fail("This isn't a text file, so it can't be a frame-time recording.");

        var text = encoding.GetString(head, bomLength, n - bomLength);
        var first = text.TrimStart().FirstOrDefault();
        var whole = new PrefixStream(head, bomLength, n - bomLength, buffered);
        try
        {
            if (first is '{' or '[')
            {
                if (encoding is not UTF8Encoding) return ImportResult.Fail("This JSON file isn't UTF-8, so PC Tidy can't read it as a CapFrameX session.");
                return CapFrameXJsonReader.Read(whole, fileName, limits, ct);
            }
            using var reader = new StreamReader(whole, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
            return CsvRecordingReader.Read(reader, fileName, limits, ct);
        }
        catch (LimitedStream.TooLargeException)
        {
            return ImportResult.Fail($"This file is larger than {Sizes.Format(limits.MaxBytes)}.");
        }
        catch (DecoderFallbackException)
        {
            return ImportResult.Fail("This file's text couldn't be read.");
        }
    }

    /// <summary>Text import for tests and pasted data.</summary>
    public static ImportResult ImportText(string text, string fileName, ImportLimits? limits = null)
    {
        using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        return Import(ms, fileName, limits ?? ImportLimits.Default, CancellationToken.None);
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> b, out int bom)
    {
        bom = 0;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { bom = 3; return new UTF8Encoding(false); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { bom = 2; return new UnicodeEncoding(false, false); }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { bom = 2; return new UnicodeEncoding(true, false); }
        // UTF-16 LE without a BOM (ASCII text with every other byte zero).
        if (b.Length >= 4 && b[0] != 0 && b[1] == 0 && b[2] != 0 && b[3] == 0) return new UnicodeEncoding(false, false);
        return new UTF8Encoding(false);
    }

    /// <summary>Read-only stream that refuses to read past a byte limit (for streams whose length isn't known up front).</summary>
    private sealed class LimitedStream : Stream
    {
        public sealed class TooLargeException : IOException { }

        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public LimitedStream(Stream inner, long max) { _inner = inner; _max = max; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            _read += n;
            if (_read > _max) throw new TooLargeException();
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The sniffed first bytes, then the rest of the stream.</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _pos;
        private readonly int _end;
        private readonly Stream _rest;

        public PrefixStream(byte[] prefix, int start, int length, Stream rest)
        {
            _prefix = prefix;
            _pos = start;
            _end = start + length;
            _rest = rest;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < _end)
            {
                int n = Math.Min(count, _end - _pos);
                Array.Copy(_prefix, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            return _rest.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
