using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Tests;

/// <summary>Per-test folder under testbed\spacemap\, deleted afterwards.</summary>
internal sealed class TestDir : IDisposable
{
    public static readonly string AreaRoot = Path.Combine(FindCodeRoot(), "testbed", "spacemap");

    public TestDir(string? name = null)
    {
        Root = Path.Combine(AreaRoot, "tmp-" + (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, int bytes = 100)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    public string Dir(string relative)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so the recursive delete never walks into their targets.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if ((System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}

internal static class Junction
{
    /// <summary>Makes a directory junction (no admin needed). Test-only helper.</summary>
    public static bool TryCreate(string link, string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && (System.IO.File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return false; }
    }
}

/// <summary>SafetyGuard stand-in: listed folders are Never / AskFirst; Check() blocks Never sources and destinations.</summary>
internal sealed class FakeSafety : ISafetyGuard
{
    public List<string> Never { get; } = new();
    /// <summary>Blocked as a whole, like a drive root or Users; their contents follow the other lists.</summary>
    public List<string> ExactOnly { get; } = new();
    public List<string> AskFirst { get; } = new();
    public List<string> Cloud { get; } = new();
    public List<string> Projects { get; } = new();
    public int ClassifyCalls;
    public int CloudCalls;

    public SafetyVerdict ClassifyPath(string path)
    {
        Interlocked.Increment(ref ClassifyCalls);
        if (Never.Any(n => PathUtil.IsUnder(path, n))) return SafetyVerdict.Block("Windows needs this folder.");
        if (ExactOnly.Any(n => PathUtil.Normalize(n).TrimEnd('\\').Equals(PathUtil.Normalize(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return SafetyVerdict.Block("This folder itself stays put.");
        // Like the real guard: anything in a cloud folder is ask-first.
        if (AskFirst.Concat(Cloud).Any(n => PathUtil.IsUnder(path, n))) return new SafetyVerdict(true, "Ask first", PathProtection.AskFirst);
        return SafetyVerdict.Ok;
    }

    public SafetyVerdict Check(PlanItem item)
    {
        foreach (var p in new[] { item.SourcePath, item.DestinationPath })
            if (p is not null && ClassifyPath(p) is { Protection: PathProtection.Never } v) return v;
        return SafetyVerdict.Ok;
    }

    public bool IsInCloudRoot(string path)
    {
        Interlocked.Increment(ref CloudCalls);
        return Cloud.Any(c => PathUtil.IsUnder(path, c));
    }

    public bool IsInsideProjectOrAppFolder(string path, out string? why)
    {
        why = Projects.Any(p => PathUtil.IsUnder(path, p)) ? "code project" : null;
        return why is not null;
    }
}

internal sealed class FakeTags : IReparseTagReader
{
    public const uint MountPoint = 0xA0000003, Symlink = 0xA000000C, CloudFiles = 0x9000001A;
    public Dictionary<string, uint> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public uint? GetTag(string path) => Tags.TryGetValue(path, out var t) ? t : null;
}

internal sealed class FakeDrives : IDriveSpaceProbe
{
    public Dictionary<string, DriveSpace> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DriveSpace? ScanRoot { get; set; }
    public DriveSpace? ForScanRoot(string scanRoot) => ScanRoot;
    public DriveSpace? ForFixedDrive(string driveRoot) => Drives.TryGetValue(driveRoot.TrimEnd('\\'), out var d) ? d : null;
}

/// <summary>Answers "does it still exist?" from the synthetic tree instead of the disk.</summary>
internal sealed class FakeFacts : IPathFacts
{
    public Dictionary<string, (long, DateTime)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (long Size, DateTime LastWriteUtc)? File(string path) => Files.TryGetValue(path, out var f) ? f : null;
    public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\')) || Dirs.Contains(path);
}

internal sealed class NullInUse : IInUseChecker
{
    public Task<IReadOnlyList<InUseInfo>> CheckAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InUseInfo>>(Array.Empty<InUseInfo>());
    public bool IsLocked(string path) => false;
}

/// <summary>
/// In-memory IFileWalker: behaves like ManagedFileWalker (folder by folder, depth-first, never descends into
/// reparse points) over listings supplied by a function, so 200k-entry trees cost no disk I/O.
/// </summary>
internal sealed class ListingWalker : IFileWalker
{
    private readonly Func<string, IReadOnlyList<FileEntry>?> _list;
    public ListingWalker(Func<string, IReadOnlyList<FileEntry>?> list) => _list = list;
    public int Walks;

    public WalkStats Walk(string root, WalkOptions options, Func<FileEntry, bool> onEntry, CancellationToken ct)
    {
        Interlocked.Increment(ref Walks);
        var sw = Stopwatch.StartNew();
        long files = 0, dirs = 0, bytes = 0, denied = 0;
        var stack = new Stack<string>();
        stack.Push(root.TrimEnd('\\'));
        bool first = true;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var entries = _list(dir);
            if (entries is null) { denied++; continue; }
            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    dirs++;
                    if (onEntry(e) && options.Recurse && !e.IsReparsePoint) stack.Push(e.FullPath);
                }
                else { files++; bytes += e.Size; onEntry(e); }
            }
            if (first && !options.Recurse) break;
            first = false;
        }
        return new WalkStats(files, dirs, bytes, denied, sw.Elapsed);
    }
}

/// <summary>Hand-built tree for precise tests.</summary>
internal sealed class FakeTree
{
    public static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, List<FileEntry>> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public FakeTree(string root) { Root = root.Length <= 3 ? root : root.TrimEnd('\\'); _dirs[Root.TrimEnd('\\')] = new(); }

    public string Root { get; }
    public FakeFacts Facts { get; } = new();
    public ListingWalker Walker => new(p => _dirs.TryGetValue(p.TrimEnd('\\'), out var l) ? l : null);

    public string Dir(string relative, FileAttributes attrs = FileAttributes.Directory, bool listable = true)
    {
        var full = Path.Combine(Root, relative);
        EnsureParent(full);
        if (!_dirs.ContainsKey(full))
        {
            Add(full, new FileEntry(full, Path.GetFileName(full), true, 0, attrs | FileAttributes.Directory, Old, Old, Old, 0));
            if (listable) _dirs[full] = new();
        }
        Facts.Dirs.Add(full);
        return full;
    }

    public string File(string relative, long size, DateTime? touched = null, DateTime? accessed = null, FileAttributes attrs = FileAttributes.Normal, DateTime? created = null)
    {
        var full = Path.Combine(Root, relative);
        EnsureParent(full);
        var t = touched ?? Old;
        Add(full, new FileEntry(full, Path.GetFileName(full), false, size, attrs, created ?? t, t, accessed ?? t, 0));
        Facts.Files[full] = (size, t);
        return full;
    }

    private void EnsureParent(string full)
    {
        var parent = Path.GetDirectoryName(full)!;
        if (!parent.TrimEnd('\\').Equals(Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !_dirs.ContainsKey(parent))
            Dir(Path.GetRelativePath(Root, parent));
    }

    private void Add(string full, FileEntry e) => _dirs[Path.GetDirectoryName(full)!.TrimEnd('\\')].Add(e);
}

/// <summary>
/// Deterministic synthetic tree generated on demand (nothing stored): 12 × 12 × 10 folders, files at every level,
/// ~199k entries. Names, sizes and dates vary like a real profile.
/// </summary>
internal static class SyntheticTree
{
    public const string Root = @"X:\Synth";
    private static readonly string[] Exts = { "jpg", "png", "mp4", "docx", "pdf", "txt", "dll", "exe", "zip", "json", "cs", "mp3", "log", "dat", "" };
    private static readonly string[] Stems = { "IMG_2023", "Report", "notes", "Screenshot 2024-05", "setup", "data", "frame", "track", "invoice", "build", "cache", "photo" };

    public static ListingWalker Walker() => new(List);

    public static (int Dirs, int Files) Shape(int depth) => depth switch
    {
        0 => (12, 5),
        1 => (12, 10),
        2 => (10, 20),
        _ => (0, 135),
    };

    public static int ExpectedFiles => 5 + 12 * 10 + 144 * 20 + 1440 * 135;
    public static int ExpectedFolders => 12 + 144 + 1440;

    private static IReadOnlyList<FileEntry>? List(string dir)
    {
        int depth = dir.Length == Root.Length ? 0 : dir[(Root.Length + 1)..].Count(c => c == '\\') + 1;
        var (dirs, files) = Shape(depth);
        var list = new List<FileEntry>(dirs + files);
        uint seed = Hash(dir);
        var baseTime = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < dirs; i++)
        {
            var name = $"Folder {i:00} {Stems[(seed + i) % Stems.Length]}";
            list.Add(new FileEntry(dir + "\\" + name, name, true, 0, FileAttributes.Directory, baseTime, baseTime, baseTime, depth + 1));
        }
        for (int i = 0; i < files; i++)
        {
            uint h = Mix(seed + (uint)i * 2654435761u);
            var ext = Exts[h % Exts.Length];
            var name = $"{Stems[(h >> 4) % Stems.Length]}_{h % 100000:00000}" + (ext.Length > 0 ? "." + ext : "");
            // Heavy-tailed sizes: mostly small, a few huge.
            long size = (h & 0xFF) == 0 ? (long)(h % 900) * 1024 * 1024 : (h >> 8) % 200_000;
            var t = baseTime.AddDays(-(double)((h >> 12) % 2000));
            list.Add(new FileEntry(dir + "\\" + name, name, false, size, FileAttributes.Archive, t, t, t, depth + 1));
        }
        return list;
    }

    private static uint Hash(string s) { uint h = 2166136261; foreach (var c in s) h = (h ^ c) * 16777619; return h; }
    private static uint Mix(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }
}

internal static class Perf
{
    public static readonly string ResultsFile = Path.Combine(TestDir.AreaRoot, "perf-results.txt");
    private static readonly object Gate = new();

    public static void Record(string line)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(TestDir.AreaRoot);
            System.IO.File.AppendAllText(ResultsFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
    }
}
