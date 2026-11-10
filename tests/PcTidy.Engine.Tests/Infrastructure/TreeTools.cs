using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Tests;

/// <summary>Builds test trees and takes byte-for-byte snapshots (content hash, size, times, attributes).</summary>
public static class TreeTools
{
    public static readonly DateTime OldTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    public static readonly DateTime OldCreation = new(1999, 12, 31, 23, 59, 58, DateTimeKind.Utc);

    private const FileAttributes Compared = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    public static string WriteFile(string path, int size, int seed = 1, DateTime? lastWrite = null, FileAttributes extra = 0)
    {
        var io = LongPath.Io(path);
        Directory.CreateDirectory(Path.GetDirectoryName(io)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(io, data);
        File.SetCreationTimeUtc(io, OldCreation.AddSeconds(seed));
        File.SetLastWriteTimeUtc(io, lastWrite ?? OldTime.AddMinutes(seed));
        if (extra != 0) File.SetAttributes(io, File.GetAttributes(io) | extra);
        return LongPath.Display(io);
    }

    public static string WriteText(string path, string text)
    {
        var io = LongPath.Io(path);
        Directory.CreateDirectory(Path.GetDirectoryName(io)!);
        File.WriteAllText(io, text);
        return LongPath.Display(io);
    }

    /// <summary>
    /// A realistic awkward tree: nested folders, an empty folder, 0-byte and multi-megabyte files,
    /// read-only + hidden files, a read-only folder, Unicode names, trailing dot/space names, a reserved name.
    /// </summary>
    public static string MakeTree(string root, bool oddNames = true)
    {
        Directory.CreateDirectory(LongPath.Io(root));
        WriteFile(Path.Combine(root, "readme.txt"), 120, 1);
        WriteFile(Path.Combine(root, "empty.bin"), 0, 2);
        WriteFile(Path.Combine(root, "big.dat"), 2_500_000, 3); // crosses the 1 MB copy buffer
        WriteFile(Path.Combine(root, "sub", "deeper", "photo.jpg"), 40_000, 4);
        WriteFile(Path.Combine(root, "sub", "locked.txt"), 300, 5, extra: FileAttributes.ReadOnly);
        WriteFile(Path.Combine(root, "sub", "secret.ini"), 64, 6, extra: FileAttributes.Hidden);
        Directory.CreateDirectory(LongPath.Io(Path.Combine(root, "empty folder")));
        var ro = Path.Combine(root, "ro folder");
        WriteFile(Path.Combine(ro, "inside.txt"), 10, 7);
        if (oddNames)
        {
            WriteFile(Path.Combine(root, "ünïcødé ✓ 日本語.txt"), 77, 8);
            WriteFile(Path.Combine(root, "emoji 😀 file.md"), 33, 9);
            WriteFile(Path.Combine(root, "trailing dot."), 11, 10);
            WriteFile(Path.Combine(root, "trailing space "), 12, 11);
            WriteFile(Path.Combine(root, "CON.txt"), 13, 12);
            WriteFile(Path.Combine(root, "dir with dot.", "x.txt"), 14, 13);
        }
        // Folder times last (creating children changes them), read-only folder attribute last of all.
        foreach (var d in Directory.EnumerateDirectories(LongPath.Io(root), "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            Directory.SetLastWriteTimeUtc(d, OldTime.AddDays(1));
            Directory.SetCreationTimeUtc(d, OldCreation);
        }
        File.SetAttributes(LongPath.Io(ro), FileAttributes.Directory | FileAttributes.ReadOnly);
        return LongPath.Display(LongPath.Io(root));
    }

    /// <summary>
    /// Snapshot of everything below <paramref name="root"/> (root excluded): for files XXH3, size, created/modified
    /// times and attributes; for folders attributes (and times when asked). Never follows links.
    /// </summary>
    public static List<string> Snapshot(string root, bool includeFolderTimes = true)
    {
        var lines = new List<string>();
        var rootIo = LongPath.Io(root);
        void Walk(string dirIo, string rel)
        {
            foreach (var e in new DirectoryInfo(dirIo).EnumerateFileSystemInfos())
            {
                var r = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
                var attrs = e.Attributes & Compared;
                if (e is DirectoryInfo d)
                {
                    lines.Add(includeFolderTimes
                        ? $"D|{r}|{attrs}|{d.CreationTimeUtc.Ticks}|{d.LastWriteTimeUtc.Ticks}|link={(d.Attributes & FileAttributes.ReparsePoint) != 0}"
                        : $"D|{r}|{attrs}|link={(d.Attributes & FileAttributes.ReparsePoint) != 0}");
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) Walk(d.FullName, r);
                }
                else
                {
                    var f = (FileInfo)e;
                    lines.Add($"F|{r}|{f.Length}|{FileHash.Full(f.FullName)}|{f.CreationTimeUtc.Ticks}|{f.LastWriteTimeUtc.Ticks}|{attrs}");
                }
            }
        }
        if (Directory.Exists(rootIo)) Walk(rootIo, "");
        return lines.OrderBy(l => l, StringComparer.Ordinal).ToList();
    }

    /// <summary>Snapshot of a single file: content hash, size, times, attributes.</summary>
    public static string FileSnapshot(string path)
    {
        var f = new FileInfo(LongPath.Io(path));
        return $"{f.Length}|{FileHash.Full(f.FullName)}|{f.CreationTimeUtc.Ticks}|{f.LastWriteTimeUtc.Ticks}|{f.Attributes & Compared}";
    }

    public static bool Exists(string path) => FsOps.Exists(path);

    /// <summary>A folder path deeper than MAX_PATH (260) below <paramref name="root"/>.</summary>
    public static string LongFolder(string root)
    {
        var sb = new StringBuilder(root);
        int i = 0;
        while (sb.Length < 300) sb.Append($"\\level {i++} with a fairly long folder name");
        return sb.ToString();
    }

    public static PlanItem Item(ActionKind kind, string? source = null, string? dest = null, bool isDir = false)
        => new()
        {
            Kind = kind, FeatureId = "test", Description = $"{kind} {Path.GetFileName(source ?? dest ?? "")}",
            SourcePath = source, DestinationPath = dest, IsDirectory = isDir,
        };

    /// <summary>A plan item carrying the scan-time identity (size + modified time), like real scanners produce.</summary>
    public static PlanItem ScannedItem(ActionKind kind, string source, string? dest = null)
    {
        var info = FsOps.Stat(source)!;
        return Item(kind, source, dest, info is DirectoryInfo) with
        {
            ExpectedSize = info is FileInfo f ? f.Length : 0,
            ExpectedLastWriteUtc = info.LastWriteTimeUtc,
        };
    }

    public static ActionPlan Plan(params PlanItem[] items) => new() { FeatureId = "test", Title = "Test run", Items = items };

    public static ExecuteOptions Real => new() { Mode = ExecutionMode.Real };
    public static ExecuteOptions Simulate => new() { Mode = ExecutionMode.Simulate };
}
