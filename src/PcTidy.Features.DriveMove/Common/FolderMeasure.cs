using PcTidy.Core;

namespace PcTidy.Features.DriveMove.Common;

/// <summary>What a read-only walk of one folder found.</summary>
/// <param name="SampleFiles">Up to the requested number of file paths (for the in-use check); placeholders are never included.</param>
public sealed record FolderTotals(
    bool Exists,
    long Bytes,
    long Files,
    long Folders,
    long Links,
    long Placeholders,
    long Inaccessible,
    DateTime? LastWriteUtc,
    IReadOnlyList<string> SampleFiles)
{
    public static readonly FolderTotals Missing = new(false, 0, 0, 0, 0, 0, 0, null, Array.Empty<string>());
}

/// <summary>Sizes folders with the shared read-only walker: never follows links, never opens files.</summary>
internal static class FolderMeasure
{
    public static FolderTotals Measure(IFileWalker walker, string folder, CancellationToken ct, int sampleFiles = 0)
    {
        DateTime? lastWrite;
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return FolderTotals.Missing;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FolderTotals.Missing;
        }

        long bytes = 0, files = 0, folders = 0, links = 0, placeholders = 0;
        var sample = new List<string>();
        var stats = walker.Walk(folder, new WalkOptions { IncludeHidden = true, IncludeSystem = true }, e =>
        {
            if (e.IsReparsePoint) { links++; return false; }
            if (e.IsDirectory) { folders++; return true; }
            files++;
            bytes += e.Size;
            if (e.IsCloudPlaceholder) placeholders++;
            else if (sample.Count < sampleFiles) sample.Add(e.FullPath);
            return true;
        }, ct);
        return new FolderTotals(true, bytes, files, folders, links, placeholders, stats.Inaccessible, lastWrite, sample);
    }

    /// <summary>Size of one file (WSL / Docker disks), or 0 when it can't be read.</summary>
    public static long FileSize(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    public static bool FileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>True for a folder that doesn't exist or has nothing in it.</summary>
    public static bool IsMissingOrEmpty(string path)
    {
        try { return !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string[] SubFolders(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
