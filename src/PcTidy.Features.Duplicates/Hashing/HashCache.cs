using System.Collections.Concurrent;
using System.Text;

namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>Perceptual fingerprint of a picture (see <c>ImageFingerprint</c>).</summary>
public readonly record struct ImageHashes(ulong DHash, ulong PHash, int Width, int Height);

/// <summary>
/// Remembers hashes by path + size + modified time, so a rescan only reads files that changed (Czkawka-style).
/// Thread-safe. Lives in memory, and is kept as one file in PC Tidy's own data folder,
/// <c>&lt;DataRoot&gt;\duplicates\duplicates-hashes.bin</c> (<see cref="PathIn"/>): read with <see cref="LoadFrom"/>,
/// written only by <see cref="HashCacheStore"/> (the one class here allowed to write, and only there).
/// </summary>
public sealed class HashCache
{
    /// <summary>File name inside <c>&lt;DataRoot&gt;\duplicates</c>.</summary>
    public const string FileName = "duplicates-hashes.bin";
    private const uint Magic = 0x43485450; // "PTHC"
    private const int Version = 1;
    /// <summary>Beyond this many entries, the least recently used ones are dropped on save.</summary>
    public const int MaxEntries = 2_000_000;

    [Flags]
    private enum Has : byte { None = 0, Partial = 1, Full = 2, Image = 4, NotAnImage = 8 }

    private struct Entry
    {
        public long Size;
        public long WriteTicks;
        public Has Flags;
        public ulong Partial;
        public ulong Full;
        public ImageHashes Image;
        public long LastUsedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _hits;
    private int _dirty;

    /// <param name="partialBytes">How many leading bytes a partial hash covers. A cache saved with another value is ignored.</param>
    public HashCache(int partialBytes = DuplicateFinder.DefaultPartialBytes) => PartialBytes = partialBytes;

    public int PartialBytes { get; }
    public int Count => _entries.Count;
    public long Hits => Interlocked.Read(ref _hits);
    /// <summary>True when something was added since the last save/load (the shell can skip saving otherwise).</summary>
    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    /// <summary>A save didn't reach the disk: try again next time.</summary>
    internal void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Path of the cache file in a data folder: the Duplicates feature's own subfolder.</summary>
    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, DuplicatesModule.FeatureId, FileName);

    public bool TryGetPartial(string path, long size, DateTime writeUtc, out ulong hash) => TryGet(path, size, writeUtc, Has.Partial, e => e.Partial, out hash);
    public bool TryGetFull(string path, long size, DateTime writeUtc, out ulong hash) => TryGet(path, size, writeUtc, Has.Full, e => e.Full, out hash);

    /// <summary>Image fingerprint, or "known not to be a decodable picture" (<paramref name="notAnImage"/>).</summary>
    public bool TryGetImage(string path, long size, DateTime writeUtc, out ImageHashes hashes, out bool notAnImage)
    {
        notAnImage = false;
        hashes = default;
        if (!_entries.TryGetValue(path, out var e) || !Matches(e, size, writeUtc)) return false;
        if ((e.Flags & Has.NotAnImage) != 0) { notAnImage = true; Touch(path, e); return true; }
        if ((e.Flags & Has.Image) == 0) return false;
        hashes = e.Image;
        Touch(path, e);
        return true;
    }

    public void PutPartial(string path, long size, DateTime writeUtc, ulong hash) => Put(path, size, writeUtc, (ref Entry e) => { e.Partial = hash; e.Flags |= Has.Partial; });
    public void PutFull(string path, long size, DateTime writeUtc, ulong hash) => Put(path, size, writeUtc, (ref Entry e) => { e.Full = hash; e.Flags |= Has.Full; });
    public void PutImage(string path, long size, DateTime writeUtc, ImageHashes hashes) => Put(path, size, writeUtc, (ref Entry e) => { e.Image = hashes; e.Flags = (e.Flags | Has.Image) & ~Has.NotAnImage; });
    public void PutNotAnImage(string path, long size, DateTime writeUtc) => Put(path, size, writeUtc, (ref Entry e) => e.Flags = (e.Flags | Has.NotAnImage) & ~Has.Image);

    private bool TryGet(string path, long size, DateTime writeUtc, Has flag, Func<Entry, ulong> pick, out ulong hash)
    {
        hash = 0;
        if (!_entries.TryGetValue(path, out var e) || !Matches(e, size, writeUtc) || (e.Flags & flag) == 0) return false;
        hash = pick(e);
        Touch(path, e);
        return true;
    }

    private void Touch(string path, Entry e)
    {
        Interlocked.Increment(ref _hits);
        var now = DateTime.UtcNow.Ticks;
        // Only the "last used" stamp changes; a hash stored meanwhile by another thread is kept.
        _entries.AddOrUpdate(path, e, (_, current) => { current.LastUsedTicks = now; return current; });
    }

    private static bool Matches(Entry e, long size, DateTime writeUtc) => e.Size == size && e.WriteTicks == writeUtc.ToUniversalTime().Ticks;

    private delegate void Mutator(ref Entry e);

    private void Put(string path, long size, DateTime writeUtc, Mutator change)
    {
        var ticks = writeUtc.ToUniversalTime().Ticks;
        _entries.AddOrUpdate(path,
            _ => { var e = new Entry { Size = size, WriteTicks = ticks }; change(ref e); e.LastUsedTicks = DateTime.UtcNow.Ticks; return e; },
            (_, old) =>
            {
                // A changed file starts over: its old hashes describe old content.
                var e = old.Size == size && old.WriteTicks == ticks ? old : new Entry { Size = size, WriteTicks = ticks };
                change(ref e);
                e.LastUsedTicks = DateTime.UtcNow.Ticks;
                return e;
            });
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Writes the cache (binary, compact). The caller owns the stream (the shell writes it to <see cref="PathIn"/>).</summary>
    public void Save(Stream stream)
    {
        // Cleared before the snapshot, so a hash added while saving still marks the cache as changed.
        Interlocked.Exchange(ref _dirty, 0);
        var snapshot = _entries.ToArray();
        if (snapshot.Length > MaxEntries)
            snapshot = snapshot.OrderByDescending(kv => kv.Value.LastUsedTicks).Take(MaxEntries).ToArray();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(PartialBytes);
        w.Write(snapshot.Length);
        foreach (var (path, e) in snapshot)
        {
            w.Write(path);
            w.Write(e.Size);
            w.Write(e.WriteTicks);
            w.Write((byte)e.Flags);
            if ((e.Flags & Has.Partial) != 0) w.Write(e.Partial);
            if ((e.Flags & Has.Full) != 0) w.Write(e.Full);
            if ((e.Flags & Has.Image) != 0)
            {
                w.Write(e.Image.DHash); w.Write(e.Image.PHash); w.Write(e.Image.Width); w.Write(e.Image.Height);
            }
            w.Write(e.LastUsedTicks);
        }
        w.Flush();
    }

    /// <summary>
    /// Reads a cache written by <see cref="Save"/>. A damaged, older or foreign file gives an empty cache
    /// (hashes are simply recomputed), never an error.
    /// </summary>
    public static HashCache Load(Stream stream, int partialBytes = DuplicateFinder.DefaultPartialBytes)
    {
        var cache = new HashCache(partialBytes);
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadInt32() != partialBytes) return cache;
            int count = r.ReadInt32();
            if (count < 0 || count > MaxEntries) return cache;
            for (int i = 0; i < count; i++)
            {
                var path = r.ReadString();
                var e = new Entry { Size = r.ReadInt64(), WriteTicks = r.ReadInt64(), Flags = (Has)r.ReadByte() };
                if ((e.Flags & Has.Partial) != 0) e.Partial = r.ReadUInt64();
                if ((e.Flags & Has.Full) != 0) e.Full = r.ReadUInt64();
                if ((e.Flags & Has.Image) != 0) e.Image = new ImageHashes(r.ReadUInt64(), r.ReadUInt64(), r.ReadInt32(), r.ReadInt32());
                e.LastUsedTicks = r.ReadInt64();
                cache._entries[path] = e;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException)
        {
            return new HashCache(partialBytes);
        }
        return cache;
    }

    /// <summary>Loads the cache from a data folder if it's there (read-only), else an empty one.</summary>
    public static HashCache LoadFrom(string dataRoot)
    {
        try
        {
            var file = PathIn(dataRoot);
            if (!File.Exists(file)) return new HashCache();
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashCache();
        }
    }
}
