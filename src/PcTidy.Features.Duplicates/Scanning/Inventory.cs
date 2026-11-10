namespace PcTidy.Features.Duplicates.Scanning;

/// <summary>One file seen by the walk (never opened yet).</summary>
public readonly record struct FileRec(string Path, string Name, long Size, DateTime WriteUtc, DateTime CreationUtc, FileAttributes Attributes, int Dir)
{
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;
}

/// <summary>One folder seen by the walk, with enough counts to find empty folders and folder duplicates afterwards.</summary>
public sealed class DirRec
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required int Parent { get; init; }
    public DateTime WriteUtc { get; init; }
    public DateTime CreationUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    /// <summary>Every file directly inside, whatever it is (hidden, system, online-only, links).</summary>
    public int AllFiles;
    /// <summary>Things inside the walk didn't enter or can't judge: links, protected or project folders, online-only files.</summary>
    public bool HasOpaqueContent;
    public List<int> Children { get; } = new();
    /// <summary>Indexes into <see cref="RootInventory.Files"/> of the ordinary files directly inside.</summary>
    public List<int> FileIndexes { get; } = new();
}

/// <summary>Everything one root's walk saw. Directory 0 is the root itself; children always come after parents.</summary>
public sealed class RootInventory
{
    public required string Root { get; init; }
    public List<DirRec> Dirs { get; } = new();
    /// <summary>Ordinary files: not online-only, not links. (System files are counted in <see cref="DirRec.AllFiles"/> only.)</summary>
    public List<FileRec> Files { get; } = new();
    public long FilesSeen;
    public long BytesSeen;
    public long Placeholders;
    public long Links;
    public long ProtectedSkipped;
    public long ProjectsSkipped;
    public long Unreadable;
}

/// <summary>Read-only walk of the scan roots with the scope's skip rules. Never opens a file, never follows a link.</summary>
public static class InventoryBuilder
{
    /// <summary>Progress is reported every this many files.</summary>
    public const int ProgressStep = 2048;

    public static RootInventory Walk(string root, ScanScope scope, IFileWalker walker, Action<long>? onProgress, CancellationToken ct)
    {
        var inv = new RootInventory { Root = root };
        DateTime rootWrite = default, rootCreated = default;
        try
        {
            var di = new DirectoryInfo(root);
            rootWrite = di.LastWriteTimeUtc;
            rootCreated = di.CreationTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        inv.Dirs.Add(new DirRec { Path = root, Name = System.IO.Path.GetFileName(root), Parent = -1, WriteUtc = rootWrite, CreationUtc = rootCreated });
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [root] = 0 };

        int ParentOf(string fullPath)
        {
            var cut = fullPath.LastIndexOf('\\');
            if (cut <= 0) return 0;
            return index.TryGetValue(fullPath[..cut], out var i) ? i : (index.TryGetValue(fullPath[..(cut + 1)], out i) ? i : 0);
        }

        var stats = walker.Walk(root, new WalkOptions { IncludeHidden = true, IncludeSystem = true, Recurse = true }, e =>
        {
            var parent = ParentOf(e.FullPath);
            var p = inv.Dirs[parent];
            if (e.IsDirectory)
            {
                var skip = scope.ShouldSkip(e);
                if (skip != SkipKind.None)
                {
                    p.HasOpaqueContent = true;
                    switch (skip)
                    {
                        case SkipKind.Link: inv.Links++; break;
                        case SkipKind.Project: inv.ProjectsSkipped++; break;
                        default: inv.ProtectedSkipped++; break;
                    }
                    return false;
                }
                var dir = new DirRec
                {
                    Path = e.FullPath, Name = e.Name, Parent = parent, WriteUtc = e.LastWriteUtc, CreationUtc = e.CreationUtc, Attributes = e.Attributes,
                };
                index[e.FullPath] = inv.Dirs.Count;
                p.Children.Add(inv.Dirs.Count);
                inv.Dirs.Add(dir);
                return true;
            }

            p.AllFiles++;
            inv.FilesSeen++;
            inv.BytesSeen += e.Size;
            if (inv.FilesSeen % ProgressStep == 0) onProgress?.Invoke(inv.FilesSeen);
            if (e.IsReparsePoint) { inv.Links++; p.HasOpaqueContent = true; return false; }
            // Online-only files are never opened: their content isn't on this PC.
            if (e.IsCloudPlaceholder) { inv.Placeholders++; p.HasOpaqueContent = true; return false; }
            p.FileIndexes.Add(inv.Files.Count);
            inv.Files.Add(new FileRec(e.FullPath, e.Name, e.Size, e.LastWriteUtc, e.CreationUtc, e.Attributes, parent));
            return false;
        }, ct);
        inv.Unreadable = stats.Inaccessible;
        return inv;
    }
}
