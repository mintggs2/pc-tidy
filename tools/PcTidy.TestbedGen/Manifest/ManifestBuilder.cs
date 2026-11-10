using System.Buffers;
using System.IO.Enumeration;
using System.IO.Hashing;
using PcTidy.Core;
using PcTidy.TestbedGen.Io;

namespace PcTidy.TestbedGen.Manifest;

/// <summary>Walks a testbed (extended-length paths, never following links) and hashes every file in parallel.</summary>
internal static class ManifestBuilder
{
    private const string RootToken = "$ROOT";
    private const int SmallFileLimit = 1 << 20;

    public static TestbedManifest Build(string root, CancellationToken ct)
    {
        root = PathUtil.Normalize(root);
        var entries = new List<ManifestEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var found = new FileSystemEnumerable<(string Full, bool IsDir, FileAttributes Attr, long Size, DateTime Created, DateTime Written)>(
                LongPath.Ext(dir),
                (ref FileSystemEntry e) => (LongPath.Strip(e.ToFullPath()), e.IsDirectory, e.Attributes, e.Length, e.CreationTimeUtc.UtcDateTime, e.LastWriteTimeUtc.UtcDateTime),
                options);
            foreach (var f in found)
            {
                var rel = f.Full[(root.Length + 1)..];
                bool isLink = (f.Attr & FileAttributes.ReparsePoint) != 0;
                if (isLink)
                {
                    entries.Add(new ManifestEntry
                    {
                        Path = rel, Kind = f.IsDir ? EntryKind.Junction : EntryKind.FileLink,
                        Attributes = f.Attr, Target = LinkTarget(f.Full, f.IsDir, root),
                    });
                }
                else if (f.IsDir)
                {
                    entries.Add(new ManifestEntry { Path = rel, Kind = EntryKind.Directory, Attributes = f.Attr, CreationUtc = f.Created, LastWriteUtc = f.Written });
                    pending.Push(f.Full);
                }
                else
                {
                    entries.Add(new ManifestEntry
                    {
                        Path = rel, Kind = EntryKind.File, Size = f.Size, Attributes = f.Attr, CreationUtc = f.Created, LastWriteUtc = f.Written,
                    });
                }
            }
        }

        var hashed = new ManifestEntry[entries.Count];
        Parallel.For(0, entries.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var e = entries[i];
            hashed[i] = e.Kind == EntryKind.File ? e with { Hash = Hash(Path.Combine(root, e.Path), e.Size, ct) } : e;
        });
        Array.Sort(hashed, (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new TestbedManifest { Root = root, Entries = hashed };
    }

    /// <summary>Same XXH3-64 hex as Core's FileHash.Full; small files use a pooled buffer (300k files must be cheap).</summary>
    private static string Hash(string path, long size, CancellationToken ct)
    {
        try
        {
            var ext = LongPath.Ext(path);
            if (size > SmallFileLimit) return FileHash.Full(ext, ct);
            var buffer = ArrayPool<byte>.Shared.Rent(SmallFileLimit);
            try
            {
                using var fs = new FileStream(ext, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                int read = 0, n;
                while ((n = fs.Read(buffer, read, SmallFileLimit - read)) > 0) read += n;
                if (fs.Length > SmallFileLimit) return FileHash.Full(ext, ct);   // grew since it was listed
                return Convert.ToHexString(XxHash3.Hash(buffer.AsSpan(0, read)));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "!error: " + ex.GetType().Name;
        }
    }

    private static string? LinkTarget(string full, bool isDir, string root)
    {
        string? target;
        try
        {
            FileSystemInfo info = isDir ? new DirectoryInfo(LongPath.Ext(full)) : new FileInfo(LongPath.Ext(full));
            target = info.LinkTarget;
        }
        catch (IOException) { target = null; }
        if (target is null) return null;
        target = LongPath.Strip(target);
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return RootToken;
        return target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? RootToken + target[root.Length..] : target;
    }
}
