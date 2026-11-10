using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Features.Search.Index;

/// <summary>
/// The ONLY code in the Search section that writes to disk: it saves and loads PC Tidy's own index snapshot in
/// <c>&lt;DataRoot&gt;\search\index.bin</c> (file names, sizes and dates PC Tidy derived itself; never user files).
/// Writes are atomic (temp file, flushed, then swapped in) and the file carries a checksum, so a half-written or
/// damaged snapshot is simply ignored and the index is rebuilt.
/// </summary>
public sealed class SearchIndexStore
{
    private const string FileName = "index.bin";
    private static readonly byte[] Magic = "PCTSRCH1"u8.ToArray();
    private const int FormatVersion = 1;
    private const int MaxSegments = 4096;
    private const int MaxEntriesPerSegment = 100_000_000;

    public SearchIndexStore(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
            throw new ArgumentException("The data folder must be a full path.", nameof(dataRoot));
        DataRoot = Path.GetFullPath(dataRoot);
        Folder = SearchIds.DataFolder(DataRoot);
    }

    public string DataRoot { get; }
    /// <summary>&lt;DataRoot&gt;\search</summary>
    public string Folder { get; }
    public string SnapshotPath => Path.Combine(Folder, FileName);

    /// <summary>Size of the saved snapshot on disk (0 if none).</summary>
    public long SnapshotBytes
    {
        get { try { var f = new FileInfo(SnapshotPath); return f.Exists ? f.Length : 0; } catch (IOException) { return 0; } }
    }

    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken ct)
    {
        EnsureOwnFolder();
        var tmp = SnapshotPath + ".tmp";
        try
        {
            await Task.Run(() => Write(snapshot, tmp, ct), ct).ConfigureAwait(false);
            File.Move(tmp, SnapshotPath, overwrite: true); // boundary-ok: own data in DataRoot
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>The saved snapshot, or null when there is none or it can't be trusted (it's rebuilt then).</summary>
    public IndexSnapshot? TryLoad(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(SnapshotPath) || IsLink(Folder)) return null;
            using var fs = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Read(fs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
                                       or ArgumentException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Removes the saved snapshot (Settings → "Rebuild the index"). Only PC Tidy's own file.</summary>
    public void Clear()
    {
        if (IsLink(Folder)) return;
        TryDelete(SnapshotPath);
        TryDelete(SnapshotPath + ".tmp");
    }

    // ------------------------------------------------------------------ format

    private static void Write(IndexSnapshot snapshot, string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16); // boundary-ok: own data in DataRoot
        var hashing = new HashingStream(fs);
        using (var w = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(DateTime.UtcNow.Ticks);
            w.Write(snapshot.Segments.Count);
            foreach (var s in snapshot.Segments)
            {
                ct.ThrowIfCancellationRequested();
                w.Write(s.Root);
                w.Write(s.BuiltUtc.Ticks);
                w.Write(s.Count);
                w.Write(s.Names.Length);
                w.Write(s.InaccessibleFolders);
                w.Write(s.SkippedFolders);
                w.Write(s.Extensions.Length);
                foreach (var e in s.Extensions) w.Write(e);
                w.Flush();
                WriteArray(hashing, s.Parent);
                WriteArray(hashing, s.NameStart);
                WriteArray(hashing, s.NameLength);
                WriteArray(hashing, s.Names);
                WriteArray(hashing, s.Size);
                WriteArray(hashing, s.ModifiedTicks);
                WriteArray(hashing, s.Attributes);
                WriteArray(hashing, s.ExtensionId);
                WriteArray(hashing, s.NameOrder);
            }
            w.Flush();
        }
        var hash = hashing.Hash();
        fs.Write(BitConverter.GetBytes(hash));
        fs.Flush(flushToDisk: true);
    }

    private static IndexSnapshot Read(Stream fs, CancellationToken ct)
    {
        var hashing = new HashingStream(fs);
        var segments = new List<IndexSegment>();
        using (var r = new BinaryReader(hashing, Encoding.UTF8, leaveOpen: true))
        {
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a PC Tidy index.");
            if (r.ReadInt32() != FormatVersion) throw new InvalidDataException("Older index format.");
            r.ReadInt64(); // saved time (informational)
            int segmentCount = r.ReadInt32();
            if (segmentCount is < 0 or > MaxSegments) throw new InvalidDataException("Bad segment count.");
            for (int k = 0; k < segmentCount; k++)
            {
                ct.ThrowIfCancellationRequested();
                var root = r.ReadString();
                var built = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                int count = r.ReadInt32();
                int namesLength = r.ReadInt32();
                long inaccessible = r.ReadInt64();
                long skipped = r.ReadInt64();
                int extCount = r.ReadInt32();
                if (count is < 0 or > MaxEntriesPerSegment || namesLength < 0 || namesLength > count * 255L || extCount is < 1 or > ushort.MaxValue)
                    throw new InvalidDataException("Bad segment header.");
                var extensions = new string[extCount];
                for (int e = 0; e < extCount; e++) extensions[e] = r.ReadString();
                var parent = ReadArray<int>(hashing, count);
                var nameStart = ReadArray<int>(hashing, count);
                var nameLength = ReadArray<byte>(hashing, count);
                var names = ReadArray<char>(hashing, namesLength);
                var size = ReadArray<long>(hashing, count);
                var modified = ReadArray<long>(hashing, count);
                var attributes = ReadArray<int>(hashing, count);
                var extensionId = ReadArray<ushort>(hashing, count);
                var nameOrder = ReadArray<int>(hashing, count);
                for (int i = 0; i < count; i++)
                {
                    if (parent[i] < IndexSegment.RootParent || parent[i] >= i) throw new InvalidDataException("Bad parent.");
                    if ((uint)nameStart[i] + nameLength[i] > (uint)namesLength) throw new InvalidDataException("Bad name.");
                    if (extensionId[i] >= extCount && extensionId[i] != IndexSegment.OverflowExtension) throw new InvalidDataException("Bad extension.");
                    if ((uint)nameOrder[i] >= (uint)count) throw new InvalidDataException("Bad order.");
                }
                segments.Add(new IndexSegment(root, built, count, parent, nameStart, nameLength, names, size, modified, attributes,
                    extensionId, extensions, nameOrder, inaccessible, skipped));
            }
        }
        var expected = hashing.Hash();
        Span<byte> footer = stackalloc byte[8];
        fs.ReadExactly(footer);
        if (BitConverter.ToUInt64(footer) != expected) throw new InvalidDataException("Checksum mismatch.");
        return new IndexSnapshot(segments, 1);
    }

    private static void WriteArray<T>(Stream s, T[] array) where T : unmanaged => s.Write(MemoryMarshal.AsBytes(array.AsSpan()));

    private static T[] ReadArray<T>(Stream s, int count) where T : unmanaged
    {
        var array = new T[count];
        s.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }

    // ------------------------------------------------------------------ own folder only

    private void EnsureOwnFolder()
    {
        if (IsLink(DataRoot) || IsLink(Folder)) throw new IOException("PC Tidy's search folder is a link; the index wasn't saved.");
        Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
    }

    private static bool IsLink(string folder)
    {
        try { return Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } // boundary-ok: own data in DataRoot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the next save to replace */ }
    }

    /// <summary>Pass-through stream that hashes every byte written or read (XXH64), for the snapshot checksum.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly XxHash64 _hash = new();

        public ulong Hash() => _hash.GetCurrentHashAsUInt64();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _hash.Append(buffer[..n]);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.Append(buffer);
            inner.Write(buffer);
        }
    }
}
