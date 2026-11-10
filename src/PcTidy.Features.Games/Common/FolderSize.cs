using PcTidy.Core;

namespace PcTidy.Features.Games.Common;

/// <summary>Read-only folder measuring with the shared walker (never follows links, never opens files).</summary>
internal static class FolderSize
{
    public readonly record struct Totals(long Bytes, int Files, DateTime NewestWriteUtc);

    public static Totals Measure(IFileWalker walker, string folder, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;
        var newest = DateTime.MinValue;
        try
        {
            walker.Walk(folder, new WalkOptions { IncludeHidden = true, IncludeSystem = false }, e =>
            {
                if (e.IsDirectory) return !e.IsReparsePoint;
                bytes += e.Size;
                files++;
                if (e.LastWriteUtc > newest) newest = e.LastWriteUtc;
                return true;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* unreadable: whatever was counted so far */ }
        return new Totals(bytes, files, newest);
    }

    /// <summary>Direct children (files and folders) of a folder; empty when it can't be listed.</summary>
    public static IReadOnlyList<FileEntry> Children(IFileWalker walker, string folder, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        try
        {
            walker.Walk(folder, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = false }, e =>
            {
                list.Add(e);
                return false;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* unreadable folder: nothing offered */ }
        return list;
    }

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    public static bool FileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }
}
