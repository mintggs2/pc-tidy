using System.Diagnostics;
using System.IO.Enumeration;
using System.IO.Hashing;

namespace PcTidy.Core;

public static class CloudFile
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>OneDrive/cloud placeholder whose content is not on disk. Never open it.</summary>
    public static bool IsPlaceholder(FileAttributes a)
        => (a & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;
}

public static class Sizes
{
    public static string Format(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}

public static class PathUtil
{
    /// <summary>Normalised full path without trailing separator (except drive roots), for comparisons.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 3) full = full.TrimEnd('\\', '/');
        return full;
    }

    /// <summary>True if <paramref name="path"/> equals or is inside <paramref name="folder"/> (case-insensitive).</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        var p = Normalize(path); var f = Normalize(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        var fs = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(fs, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"name.ext" → "name (2).ext", "name (3).ext"... until free.</summary>
    public static string NextFreeName(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (int i = 2; i < 100000; i++)
        {
            var c = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
        throw new IOException("No free name for " + desiredPath);
    }

    public static string? DriveOf(string path) => Path.GetPathRoot(Path.GetFullPath(path));

    public static bool SameVolume(string a, string b)
        => string.Equals(DriveOf(a), DriveOf(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>XXH3-64 hashing (System.IO.Hashing). Read-only.</summary>
public static class FileHash
{
    /// <summary>Full-file XXH3-64 as hex. Caller must make sure the file is not a cloud placeholder.</summary>
    public static string Full(string path, CancellationToken ct = default)
    {
        var h = new XxHash3();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); h.Append(buf.AsSpan(0, n)); }
        return Convert.ToHexString(h.GetCurrentHash());
    }

    /// <summary>Hash of the first <paramref name="bytes"/> bytes (duplicate pre-filter only, never enough to act on).</summary>
    public static string Partial(string path, int bytes = 64 * 1024)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(bytes, (int)Math.Min(int.MaxValue, Math.Max(0, fs.Length)))];
        int read = 0, n;
        while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
        return Convert.ToHexString(XxHash3.Hash(buf.AsSpan(0, read)));
    }
}

/// <summary>
/// Default read-only walker on System.IO.Enumeration (uses NtQueryDirectoryFile with large buffers).
/// Never follows reparse points, never opens files, skips inaccessible folders.
/// </summary>
public sealed class ManagedFileWalker : IFileWalker
{
    public WalkStats Walk(string root, WalkOptions options, Func<FileEntry, bool> onEntry, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long files = 0, dirs = 0, bytes = 0, denied = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var eo = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            // false: an unreadable folder throws when opened, so it's counted in WalkStats.Inaccessible below.
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            IEnumerable<FileEntry> entries;
            try
            {
                entries = new FileSystemEnumerable<FileEntry>(dir,
                    (ref FileSystemEntry e) => new FileEntry(
                        e.ToFullPath(), e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                        e.Attributes, e.CreationTimeUtc.UtcDateTime, e.LastWriteTimeUtc.UtcDateTime, e.LastAccessTimeUtc.UtcDateTime, depth + 1),
                    eo).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                denied++; continue;
            }
            foreach (var e in entries)
            {
                if (!options.IncludeHidden && e.IsHidden) continue;
                if (!options.IncludeSystem && e.IsSystem) continue;
                if (e.IsDirectory)
                {
                    dirs++;
                    var descend = onEntry(e);
                    if (descend && options.Recurse && !e.IsReparsePoint && e.Depth < options.MaxDepth)
                        stack.Push((e.FullPath, e.Depth));
                }
                else
                {
                    files++; bytes += e.Size;
                    onEntry(e);
                }
            }
        }
        return new WalkStats(files, dirs, bytes, denied, sw.Elapsed);
    }
}
