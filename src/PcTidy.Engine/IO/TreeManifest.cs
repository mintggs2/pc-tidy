using System.IO.Enumeration;
using System.IO.Hashing;
using System.Text;
using PcTidy.Core;

namespace PcTidy.Engine.IO;

internal sealed record ManifestEntry(string RelativePath, bool IsDirectory, long Size, long LastWriteTicks, FileAttributes Attributes);

/// <summary>
/// Read-only description of a file or folder tree: every entry with size and modified time,
/// plus anything that makes copying unsafe (online-only cloud files, links, unreadable folders).
/// Used to verify that something is still exactly what PC Tidy moved.
/// </summary>
internal sealed class TreeManifest
{
    public required bool IsDirectory { get; init; }
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    public List<string> CloudPlaceholders { get; } = new();
    /// <summary>Junctions/symlinks inside the tree (never followed).</summary>
    public List<string> Links { get; } = new();
    public List<string> Unreadable { get; } = new();

    /// <summary>
    /// XXH3 of the sorted "path|size|modified" lines of every file (and the names of folders).
    /// Folder timestamps and attributes are left out on purpose: Windows updates them on its own.
    /// </summary>
    public string MetadataHash
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var e in Entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
                sb.Append(e.IsDirectory ? $"D|{e.RelativePath.ToUpperInvariant()}\n" : $"F|{e.RelativePath.ToUpperInvariant()}|{e.Size}|{e.LastWriteTicks}\n");
            return Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }

    /// <summary>True when the tree can be copied byte-for-byte without downloading or following anything.</summary>
    public bool SafeToCopy => CloudPlaceholders.Count == 0 && Links.Count == 0 && Unreadable.Count == 0;

    public string? CopyProblem
    {
        get
        {
            if (CloudPlaceholders.Count > 0) return $"It contains {CloudPlaceholders.Count} online-only OneDrive file(s). PC Tidy never downloads them, so it was left alone.";
            if (Links.Count > 0) return $"It contains {Links.Count} link(s) to other folders. PC Tidy never follows links, so it was left alone.";
            if (Unreadable.Count > 0) return $"PC Tidy can't read {Unreadable.Count} folder(s) inside it, so it was left alone.";
            return null;
        }
    }

    public static TreeManifest Build(string path, CancellationToken ct = default)
    {
        var info = FsOps.Stat(path) ?? throw new FileNotFoundException("It's no longer there.", LongPath.Display(path));
        if (info is FileInfo f)
        {
            var m = new TreeManifest
            {
                IsDirectory = false,
                Entries = new[] { new ManifestEntry("", false, f.Length, f.LastWriteTimeUtc.Ticks, f.Attributes) },
                TotalBytes = f.Length,
                FileCount = 1,
            };
            if (CloudFile.IsPlaceholder(f.Attributes)) m.CloudPlaceholders.Add(LongPath.Display(f.FullName));
            else if (FsOps.IsReparsePoint(f) && f.LinkTarget is not null) m.Links.Add(LongPath.Display(f.FullName));
            return m;
        }
        return BuildDirectory((DirectoryInfo)info, ct);
    }

    private static TreeManifest BuildDirectory(DirectoryInfo root, CancellationToken ct)
    {
        var entries = new List<ManifestEntry>();
        var placeholders = new List<string>();
        var links = new List<string>();
        var unreadable = new List<string>();
        long bytes = 0; int files = 0;
        if (FsOps.IsReparsePoint(root)) links.Add(LongPath.Display(root.FullName));

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false, BufferSize = 64 * 1024,
        };
        var stack = new Stack<string>();
        if (links.Count == 0) stack.Push("");
        var rootIo = LongPath.Io(root.FullName);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var rel = stack.Pop();
            var dirIo = rel.Length == 0 ? rootIo : Path.Combine(rootIo, rel);
            List<(string Name, bool IsDir, long Size, DateTime Write, FileAttributes Attr)> children;
            try
            {
                children = new FileSystemEnumerable<(string, bool, long, DateTime, FileAttributes)>(dirIo,
                    (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes),
                    options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                unreadable.Add(LongPath.Display(dirIo));
                continue;
            }
            foreach (var c in children)
            {
                var childRel = rel.Length == 0 ? c.Name : rel + "\\" + c.Name;
                var isLink = (c.Attr & FileAttributes.ReparsePoint) != 0;
                if (c.IsDir)
                {
                    entries.Add(new ManifestEntry(childRel, true, 0, c.Write.Ticks, c.Attr));
                    if (isLink) links.Add(LongPath.Display(Path.Combine(rootIo, childRel)));
                    else stack.Push(childRel);
                }
                else
                {
                    entries.Add(new ManifestEntry(childRel, false, c.Size, c.Write.Ticks, c.Attr));
                    bytes += c.Size; files++;
                    if (CloudFile.IsPlaceholder(c.Attr)) placeholders.Add(LongPath.Display(Path.Combine(rootIo, childRel)));
                    else if (isLink && new FileInfo(Path.Combine(rootIo, childRel)).LinkTarget is not null)
                        links.Add(LongPath.Display(Path.Combine(rootIo, childRel)));
                }
            }
        }
        var m = new TreeManifest { IsDirectory = true, Entries = entries, TotalBytes = bytes, FileCount = files };
        m.CloudPlaceholders.AddRange(placeholders);
        m.Links.AddRange(links);
        m.Unreadable.AddRange(unreadable);
        return m;
    }
}
