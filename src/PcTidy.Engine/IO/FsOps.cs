using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PcTidy.Engine.IO;

internal enum RenameOutcome { Renamed, NotSameVolume }

/// <summary>Small, careful file-system primitives. Every path argument may be in display or extended form.</summary>
internal static class FsOps
{
    /// <summary>
    /// Atomic rename. Never replaces anything and never copies: across volumes it returns
    /// <see cref="RenameOutcome.NotSameVolume"/> so the caller can do a verified copy instead.
    /// </summary>
    public static RenameOutcome TryRename(string source, string destination)
    {
        if (NativeMethods.MoveFileEx(LongPath.Io(source), LongPath.Io(destination), 0)) return RenameOutcome.Renamed;
        var err = Marshal.GetLastWin32Error();
        if (err == NativeMethods.ErrorNotSameDevice) return RenameOutcome.NotSameVolume;
        if (err is NativeMethods.ErrorAlreadyExists or NativeMethods.ErrorFileExists)
            throw new IOException($"'{Path.GetFileName(LongPath.Display(destination))}' already exists there.");
        throw Win32Io(err, source);
    }

    /// <summary>Rename that must succeed (same folder / same volume, e.g. temp names).</summary>
    public static void Rename(string source, string destination)
    {
        if (TryRename(source, destination) == RenameOutcome.NotSameVolume)
            throw new IOException("Can't rename across drives.");
    }

    public static IOException Win32Io(int error, string path)
    {
        var msg = new Win32Exception(error).Message;
        return new IOException($"{msg} ({Path.GetFileName(LongPath.Display(path))})", unchecked((int)0x80070000 | error));
    }

    /// <summary>File or directory info, or null when nothing is there. Never follows links.</summary>
    public static FileSystemInfo? Stat(string path)
    {
        var io = LongPath.Io(path);
        try
        {
            var attrs = File.GetAttributes(io);
            return (attrs & FileAttributes.Directory) != 0 ? new DirectoryInfo(io) : new FileInfo(io);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool Exists(string path)
    {
        var io = LongPath.Io(path);
        return File.Exists(io) || Directory.Exists(io);
    }

    public static bool IsDirectory(string path) => Stat(path) is DirectoryInfo;

    public static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>"name.ext" → "name (2).ext", "(3)"... until free (and not <paramref name="alsoTaken"/>).</summary>
    public static string NextFreeName(string desired, Func<string, bool>? alsoTaken = null)
    {
        bool Taken(string p) => Exists(p) || alsoTaken?.Invoke(p) == true;
        if (!Taken(desired)) return desired;
        var (dir, stem, ext) = Split(desired);
        for (int i = 2; i < 100_000; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Taken(c)) return c;
        }
        throw new IOException("No free name for " + LongPath.Display(desired));
    }

    /// <summary>Restore names: "name (restored).ext", then "(restored 2)"... Never overwrites.</summary>
    public static string FreeRestoreName(string original)
    {
        if (!Exists(original)) return original;
        var (dir, stem, ext) = Split(original);
        for (int i = 1; i < 100_000; i++)
        {
            var c = Path.Combine(dir, i == 1 ? $"{stem} (restored){ext}" : $"{stem} (restored {i}){ext}");
            if (!Exists(c)) return c;
        }
        throw new IOException("No free name for " + LongPath.Display(original));
    }

    private static (string Dir, string Stem, string Ext) Split(string path)
    {
        var display = LongPath.Display(LongPath.Io(path));
        var dir = Path.GetDirectoryName(display) ?? throw new IOException("Path has no folder: " + display);
        return (dir, Path.GetFileNameWithoutExtension(display), Path.GetExtension(display));
    }

    /// <summary>Creates the missing folders above <paramref name="path"/> (not the path itself). Returns those it created, top-down.</summary>
    public static List<string> CreateParents(string path)
    {
        var parent = Path.GetDirectoryName(LongPath.Display(LongPath.Io(path)));
        return parent is null ? new List<string>() : CreateDirectories(parent);
    }

    /// <summary>Creates <paramref name="path"/> and its missing ancestors. Returns the folders it created, top-down.</summary>
    public static List<string> CreateDirectories(string path)
    {
        var missing = MissingDirectories(path);
        foreach (var d in missing) Directory.CreateDirectory(LongPath.Io(d));
        return missing;
    }

    /// <summary>Folders that would have to be created for <paramref name="path"/> to exist, top-down.</summary>
    public static List<string> MissingDirectories(string path)
    {
        var missing = new List<string>();
        var cur = LongPath.Display(LongPath.Io(path));
        while (!string.IsNullOrEmpty(cur) && !Exists(cur))
        {
            missing.Insert(0, cur);
            cur = Path.GetDirectoryName(cur);
        }
        return missing;
    }

    /// <summary>Removes the given folders deepest-first, only while each is empty. Returns those kept (not empty).</summary>
    public static List<string> RemoveEmptyDirectories(IEnumerable<string> createdTopDown)
    {
        var kept = new List<string>();
        foreach (var d in createdTopDown.Reverse())
        {
            var io = LongPath.Io(d);
            if (!Directory.Exists(io)) continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(io).Any()) { kept.Add(d); continue; }
                ClearReadOnly(io);
                Directory.Delete(io, recursive: false);
            }
            catch (IOException) { kept.Add(d); }
            catch (UnauthorizedAccessException) { kept.Add(d); }
        }
        return kept;
    }

    public static void ClearReadOnly(string io)
    {
        var a = File.GetAttributes(io);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(io, a & ~FileAttributes.ReadOnly);
    }

    /// <summary>
    /// Deletes a tree PC Tidy itself created (quarantine payloads on purge, partial copies), bottom-up.
    /// Never descends into a junction/symlink: the link itself is removed, its target is never touched.
    /// Returns the paths that could not be removed (stops at the first failure when asked to).
    /// </summary>
    public static List<string> DeleteTree(string path, bool stopOnFirstFailure = false)
    {
        var failures = new List<string>();
        var info = Stat(path);
        if (info is null) return failures;
        DeleteNode(info, failures, stopOnFirstFailure);
        return failures;
    }

    private static bool DeleteNode(FileSystemInfo node, List<string> failures, bool stop)
    {
        if (node is DirectoryInfo dir && !IsReparsePoint(dir))
        {
            foreach (var child in dir.EnumerateFileSystemInfos())
            {
                if (!DeleteNode(child, failures, stop) && stop) return false;
            }
        }
        var original = node.Attributes;
        try
        {
            if ((original & FileAttributes.ReadOnly) != 0) node.Attributes = original & ~FileAttributes.ReadOnly;
            if (node is DirectoryInfo d) Directory.Delete(d.FullName, recursive: false); // link or now-empty folder
            else File.Delete(node.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever stays behind keeps its attributes exactly as they were.
            try { if ((original & FileAttributes.ReadOnly) != 0) File.SetAttributes(node.FullName, original); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { /* nothing more to do */ }
            failures.Add(LongPath.Display(node.FullName));
            return false;
        }
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes a file atomically: temp file, then replaced (readers see the old or the new text, never half).
    /// <paramref name="flushToDisk"/> also forces the text to the disk first (needed when nothing else, such as a durable
    /// journal record, could rebuild it after a power cut).
    /// </summary>
    public static void WriteAllTextAtomic(string path, string text, bool flushToDisk = true)
    {
        var io = LongPath.Io(path);
        var tmp = io + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        {
            fs.Write(Utf8NoBom.GetBytes(text));
            fs.Flush(flushToDisk);
        }
        File.Move(tmp, io, overwrite: true);
    }

    public static string EncodeList(IEnumerable<string> items) => JsonSerializer.Serialize(items);

    public static List<string> DecodeList(string? json)
        => string.IsNullOrEmpty(json) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
}
