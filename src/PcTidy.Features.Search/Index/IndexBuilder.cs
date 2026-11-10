using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Search.Index;

/// <summary>Progress of an index build ("Indexing Documents: 41,200 items").</summary>
public sealed record IndexProgress(string Root, long Entries, string Message);

/// <summary>
/// Builds one <see cref="IndexSegment"/> by walking a root with <see cref="IFileWalker"/> (read-only: names and
/// metadata only, never file contents). Links are listed but never followed (the walker guarantees it), and folders
/// the safety guard never lets PC Tidy touch (Windows, apps, game libraries, PC Tidy's own data, other accounts) are
/// skipped together with what's inside them, as are AppData (unless asked for) and the user's scan exclusions.
/// </summary>
public sealed class IndexBuilder
{
    private readonly IFileWalker _walker;
    private readonly ISafetyGuard _safety;
    private readonly IKnownLocations _locations;
    private readonly bool _includeAppData;

    public IndexBuilder(IFileWalker walker, ISafetyGuard safety, IKnownLocations locations, bool includeAppData = false)
    {
        _walker = walker;
        _safety = safety;
        _locations = locations;
        _includeAppData = includeAppData;
    }

    /// <param name="onSkipped">Told about every folder left out (so change hints from inside it can be ignored).</param>
    public IndexSegment Build(string root, DateTime builtUtc, IProgress<IndexProgress>? progress, CancellationToken ct, Action<string>? onSkipped = null)
    {
        root = PathUtil.Normalize(root);
        var builder = new IndexSegmentBuilder(root, 4096);
        builder.InaccessibleFolders = WalkInto(builder, root, root, IndexSegment.RootParent, recurse: true, progress, ct, onSkipped);
        progress?.Report(new IndexProgress(root, builder.Count, $"Sorting {builder.Count:N0} names"));
        return builder.Build(builtUtc, ct);
    }

    /// <summary>
    /// Adds what's in <paramref name="folder"/> to <paramref name="builder"/> under entry <paramref name="parentIndex"/>:
    /// everything below it, or (recurse false) only its direct children, with <paramref name="onSubfolder"/> deciding
    /// what goes in each child folder. Returns how many folders couldn't be opened.
    /// </summary>
    private long WalkInto(IndexSegmentBuilder builder, string root, string folder, int parentIndex, bool recurse, IProgress<IndexProgress>? progress,
        CancellationToken ct, Action<string>? onSkipped, Action<string, int>? onSubfolder = null)
    {
        // Only folders are remembered here (to find each entry's parent); dropped when the walk ends.
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [folder] = parentIndex };
        var appData = SafeNormalize(Path.Combine(_locations.UserProfile, "AppData"));
        long lastReport = builder.Count;
        var options = new WalkOptions { Recurse = recurse, IncludeHidden = true, IncludeSystem = false };
        var subfolders = new List<(string Path, int Index)>();

        var stats = _walker.Walk(folder, options, entry =>
        {
            var parentPath = Path.GetDirectoryName(entry.FullPath);
            if (parentPath is null || !folders.TryGetValue(parentPath, out var parent)) return false;
            if (entry.IsDirectory && !entry.IsReparsePoint && Skip(entry.FullPath, appData))
            {
                builder.SkippedFolders++;
                onSkipped?.Invoke(entry.FullPath);
                return false;
            }
            var index = builder.Add(parent, entry.Name, entry.IsDirectory, entry.Size, entry.LastWriteUtc, entry.Attributes);
            if (entry.IsDirectory && !entry.IsReparsePoint)
            {
                folders[entry.FullPath] = index;
                if (!recurse) subfolders.Add((entry.FullPath, index));
            }
            if (progress is not null && builder.Count - lastReport >= 5000)
            {
                lastReport = builder.Count;
                progress.Report(new IndexProgress(root, builder.Count, $"Indexing {RootLabel(root)}: {builder.Count:N0} items"));
            }
            // Links are recorded as entries, never entered (the walker refuses too).
            return entry.IsDirectory && !entry.IsReparsePoint;
        }, ct);

        long inaccessible = stats.Inaccessible;
        // After the listing (the walker is done with this folder): what goes inside each child folder.
        foreach (var (path, index) in subfolders) onSubfolder?.Invoke(path, index);
        return inaccessible;
    }

    /// <summary>
    /// A new segment for <paramref name="old"/>'s root in which only <paramref name="changedFolders"/> are listed again
    /// from disk (their direct children: new, gone, renamed or changed files and folders). Everything else is copied
    /// from <paramref name="old"/>; a folder that's new is walked in full. A hint names the changed item, so this is
    /// usually a handful of small listings instead of a walk of the whole drive. Returns null when the changes can't
    /// be placed (the caller then walks the whole root).
    /// </summary>
    public IndexSegment? Patch(IndexSegment old, IReadOnlyCollection<string> changedFolders, DateTime builtUtc, CancellationToken ct, Action<string>? onSkipped = null)
    {
        var root = old.Root;
        // The old tree: each folder's children, and a way to find a folder by path.
        var children = new List<int>?[old.Count + 1]; // slot old.Count = the root
        for (int i = 0; i < old.Count; i++)
        {
            int p = old.ParentOf(i) == IndexSegment.RootParent ? old.Count : old.ParentOf(i);
            (children[p] ??= new List<int>()).Add(i);
        }
        int? Find(string folder)
        {
            var full = PathUtil.Normalize(folder);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return old.Count;
            if (!PathUtil.IsUnder(full, root)) return null;
            int at = old.Count;
            foreach (var part in Path.GetRelativePath(root, full).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                int next = -1;
                foreach (var c in children[at] ?? Enumerable.Empty<int>())
                    if (old.IsDirectory(c) && old.Name(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                if (next < 0) return null;
                at = next;
            }
            return at;
        }

        // Each changed folder, or its nearest indexed ancestor when it's new or gone.
        var relist = new HashSet<int>();
        foreach (var changed in changedFolders)
        {
            string? folder = changed;
            int? found = null;
            while (folder is not null && (found = Find(folder)) is null) folder = Path.GetDirectoryName(folder);
            if (found is null) return null;
            relist.Add(found.Value);
        }

        var builder = new IndexSegmentBuilder(root, Math.Max(4096, old.Count + 1024)) { SkippedFolders = old.SkippedFolders };
        long inaccessible = old.InaccessibleFolders;

        void Copy(int oldFolder, int newParent)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var c in children[oldFolder] ?? Enumerable.Empty<int>())
            {
                var index = builder.Add(newParent, old.Name(c), old.IsDirectory(c), old.SizeOf(c), old.ModifiedUtc(c), old.AttributesOf(c));
                if (old.IsDirectory(c)) Fill(c, index, null);
            }
        }

        void Fill(int oldFolder, int newIndex, string? path)
        {
            if (!relist.Contains(oldFolder)) { Copy(oldFolder, newIndex); return; }
            path ??= oldFolder == old.Count ? root : old.FullPath(oldFolder);
            inaccessible += WalkInto(builder, root, path, newIndex, recurse: false, null, ct, onSkipped, (subPath, subIndex) =>
            {
                // A folder that was there before keeps its old contents (unless it changed too); a new one is walked.
                var name = Path.GetFileName(subPath);
                int existing = -1;
                foreach (var c in children[oldFolder] ?? Enumerable.Empty<int>())
                    if (old.IsDirectory(c) && old.Name(c).Equals(name, StringComparison.OrdinalIgnoreCase)) { existing = c; break; }
                if (existing >= 0) Fill(existing, subIndex, subPath);
                else inaccessible += WalkInto(builder, root, subPath, subIndex, recurse: true, null, ct, onSkipped);
            });
        }

        Fill(old.Count, IndexSegment.RootParent, root);
        builder.InaccessibleFolders = inaccessible;
        return builder.Build(builtUtc, ct);
    }

    /// <summary>A child name that never exists; only its location is classified.</summary>
    private const string ProbeChild = "pctidy-index-probe";

    private bool Skip(string folder, string? appData)
    {
        if (!_includeAppData && appData is not null && string.Equals(folder, appData, StringComparison.OrdinalIgnoreCase)) return true;
        SafetyVerdict verdict;
        // Ask about what's INSIDE the folder: a user folder itself (Downloads) is "never moved" but its contents are fine,
        // while inside Windows, an app, a game library or PC Tidy's data nothing is ever touched.
        try { verdict = _safety.ClassifyPath(Path.Combine(folder, ProbeChild)); }
        catch (Exception) { return true; } // can't tell: leave it out rather than index a protected area
        return !verdict.Allowed && verdict.Protection == PathProtection.Never;
    }

    /// <summary>"Documents" for a folder, "D:\" for a drive.</summary>
    public static string RootLabel(string root) => Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } name ? name : root;

    private static string? SafeNormalize(string path)
    {
        try { return PathUtil.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
