using System.Collections.Concurrent;

namespace PcTidy.TestbedGen.Io;

/// <summary>
/// Writes the testbed: every file with exact creation/write times set on its own handle, optional attributes,
/// optional sparse length. Tracks every folder it creates so <see cref="StampDirectories"/> can give folders
/// deterministic times at the end (otherwise folder times would be "when the generator ran").
/// Thread-safe: bulk files are written in parallel.
/// </summary>
internal sealed class FsWriter
{
    private readonly string _root;
    private readonly DateTime _emptyDirTime;
    private readonly ConcurrentDictionary<string, DateTime?> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long MaxWrite, long MinCreate)> _childTimes = new(StringComparer.OrdinalIgnoreCase);
    private long _files, _bytes;

    public FsWriter(string root, DateTime emptyDirTime)
    {
        _root = root;
        _emptyDirTime = emptyDirTime;
    }

    public long FileCount => Interlocked.Read(ref _files);
    public long LogicalBytes => Interlocked.Read(ref _bytes);
    public int DirectoryCount => _dirs.Count;

    /// <summary>Creates a folder (and parents). <paramref name="lastWriteUtc"/> pins its time; otherwise it gets its newest child's.</summary>
    public void Dir(string path, DateTime? lastWriteUtc = null)
    {
        EnsureDir(path);
        if (lastWriteUtc is { } t) _dirs[path] = t;
    }

    public void File(string path, ReadOnlySpan<byte> content, DateTime lastWriteUtc, DateTime? creationUtc = null,
        FileAttributes attributes = 0, long sparseLength = 0)
    {
        var dir = Path.GetDirectoryName(path)!;
        EnsureDir(dir);
        var created = creationUtc ?? lastWriteUtc;
        using (var fs = new FileStream(LongPath.Ext(path), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1))
        {
            if (sparseLength > 0) Native.SetSparse(fs.SafeFileHandle);
            fs.Write(content);
            if (sparseLength > content.Length) fs.SetLength(sparseLength);
            fs.Flush();
            // Times set on the same handle after the last write, so closing the handle can't bump them.
            System.IO.File.SetCreationTimeUtc(fs.SafeFileHandle, created);
            System.IO.File.SetLastWriteTimeUtc(fs.SafeFileHandle, lastWriteUtc);
            System.IO.File.SetLastAccessTimeUtc(fs.SafeFileHandle, lastWriteUtc);
        }
        if (attributes != 0) System.IO.File.SetAttributes(LongPath.Ext(path), attributes);
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, Math.Max(content.Length, sparseLength));
        NoteChild(dir, lastWriteUtc, created);
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        var ext = LongPath.Ext(path);
        if (Directory.Exists(ext)) new DirectoryInfo(ext).Attributes |= attributes;
        else System.IO.File.SetAttributes(ext, System.IO.File.GetAttributes(ext) | attributes);
    }

    public void Junction(string link, string target)
    {
        EnsureDir(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(LongPath.Ext(link));
        Native.MakeJunction(link, target);
    }

    /// <summary>Gives every folder we created a deterministic time: pinned, else its newest child's, else the empty-folder time.
    /// Children are stamped before parents (longer paths first); stamping a child never changes its parent's time.</summary>
    public void StampDirectories()
    {
        foreach (var dir in _dirs.Keys.OrderByDescending(d => d.Length))
        {
            var pinned = _dirs[dir];
            var hasChildren = _childTimes.TryGetValue(dir, out var child);
            var write = pinned ?? (hasChildren ? new DateTime(child.MaxWrite, DateTimeKind.Utc) : _emptyDirTime);
            var create = hasChildren ? new DateTime(Math.Min(child.MinCreate, write.Ticks), DateTimeKind.Utc) : write;
            var ext = LongPath.Ext(dir);
            Directory.SetCreationTimeUtc(ext, create);
            Directory.SetLastWriteTimeUtc(ext, write);
            Directory.SetLastAccessTimeUtc(ext, write);
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && _dirs.ContainsKey(parent)) NoteChild(parent, write, create);
        }
    }

    private void EnsureDir(string dir)
    {
        if (_dirs.ContainsKey(dir) || dir.Length <= _root.Length) return;
        EnsureDir(Path.GetDirectoryName(dir)!);
        Directory.CreateDirectory(LongPath.Ext(dir));
        _dirs.TryAdd(dir, null);
    }

    private void NoteChild(string dir, DateTime write, DateTime create)
        => _childTimes.AddOrUpdate(dir, (write.Ticks, create.Ticks),
            (_, old) => (Math.Max(old.MaxWrite, write.Ticks), Math.Min(old.MinCreate, create.Ticks)));
}
