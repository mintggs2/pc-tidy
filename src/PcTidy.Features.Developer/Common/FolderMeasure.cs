using PcTidy.Core;

namespace PcTidy.Features.Developer.Common;

/// <summary>Read-only folder measuring and listing with the shared walker (never follows links, never opens files).</summary>
internal static class FolderMeasure
{
    /// <param name="Links">Junctions/symlinks inside: listed, never followed (pnpm builds node_modules from them).</param>
    /// <param name="Complete">False when part of it couldn't be read (the size is then a lower bound).</param>
    public readonly record struct Totals(long Bytes, int Files, int Links, DateTime NewestWriteUtc, bool Complete);

    public static Totals Measure(IFileWalker walker, string folder, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0, links = 0;
        var newest = DateTime.MinValue;
        var complete = true;
        try
        {
            var stats = walker.Walk(folder, new WalkOptions { IncludeHidden = true, IncludeSystem = true }, e =>
            {
                if (e.IsReparsePoint) { links++; return false; }
                if (e.IsDirectory) return true;
                bytes += e.Size;
                files++;
                if (e.LastWriteUtc > newest) newest = e.LastWriteUtc;
                return true;
            }, ct);
            complete = stats.Inaccessible == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { complete = false; /* whatever was counted so far */ }
        return new Totals(bytes, files, links, newest, complete);
    }

    /// <summary>Direct children (files and folders) of a folder; empty when it can't be listed.</summary>
    public static IReadOnlyList<FileEntry> Children(IFileWalker walker, string folder, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        try
        {
            walker.Walk(folder, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = true }, e =>
            {
                list.Add(e);
                return false;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* unreadable folder: nothing listed */ }
        return list;
    }

    /// <summary>The folder's own entry (attributes, times), read from its parent's listing; null when it's gone.</summary>
    public static FileEntry? Self(IFileWalker walker, string folder, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(PathUtil.Normalize(folder));
        if (parent is null) return null;
        var name = Path.GetFileName(PathUtil.Normalize(folder));
        foreach (var e in Children(walker, parent, ct))
            if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    public static bool FileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>Last-write time of a file, or null (missing, unreadable).</summary>
    public static DateTime? FileWriteUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
