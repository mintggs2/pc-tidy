using PcTidy.Core;

namespace PcTidy.Features.SpaceMap.Index;

public sealed record TypeTotal(string Extension, FileCategory Category, long Bytes, long BytesOnDisk, long Files);

public sealed record CategoryTotal(FileCategory Category, long Bytes, long Files);

/// <summary>"Big and old": files over 500 MB not touched for 6+ months (plan §8.2).</summary>
public sealed record BigAndOldRule
{
    public static readonly BigAndOldRule Default = new();

    /// <summary>Strictly larger than this many bytes (500 MB).</summary>
    public long MinBytes { get; init; } = 500L * 1024 * 1024;
    public int MonthsUntouched { get; init; } = 6;

    /// <summary>
    /// "Last touched" = newer of modified and created. Last-access is unreliable on Windows (often not updated),
    /// so it's used only to EXCLUDE a file that was clearly opened recently, never to include one.
    /// </summary>
    public bool Matches(long size, DateTime lastTouchedUtc, DateTime lastAccessUtc, DateTime nowUtc)
    {
        if (size <= MinBytes) return false;
        var cutoff = nowUtc.AddMonths(-MonthsUntouched);
        if (lastTouchedUtc >= cutoff) return false;
        return lastAccessUtc < cutoff;
    }
}

/// <summary>Read-only queries over a finished index (run them off the UI thread for big subtrees).</summary>
public static class SpaceQueries
{
    private const SpaceNodeFlags NeverActionable =
        SpaceNodeFlags.Directory | SpaceNodeFlags.Link | SpaceNodeFlags.Pseudo | SpaceNodeFlags.Protected;

    /// <summary>The <paramref name="count"/> biggest files under <paramref name="subtreeRoot"/>, biggest first.</summary>
    public static IReadOnlyList<int> TopFiles(SpaceIndex index, int subtreeRoot, int count = 100)
    {
        var heap = new PriorityQueue<int, long>(count + 1);
        index.ForEachInSubtree(subtreeRoot, n =>
        {
            if (index.IsDirectory(n) || index.Has(n, SpaceNodeFlags.Pseudo)) return;
            long size = index.Size(n);
            if (heap.Count < count) heap.Enqueue(n, size);
            else if (heap.TryPeek(out _, out var smallest) && size > smallest) heap.EnqueueDequeue(n, size);
        });
        var result = new List<int>(heap.Count);
        while (heap.Count > 0) result.Add(heap.Dequeue());
        result.Reverse();
        return result;
    }

    /// <summary>Bytes and file counts per extension under <paramref name="subtreeRoot"/>, biggest first.</summary>
    public static IReadOnlyList<TypeTotal> ByExtension(SpaceIndex index, int subtreeRoot)
    {
        var bytes = new long[index.ExtensionCount];
        var onDisk = new long[index.ExtensionCount];
        var files = new long[index.ExtensionCount];
        index.ForEachInSubtree(subtreeRoot, n =>
        {
            if (index.IsDirectory(n) || index.Has(n, SpaceNodeFlags.Pseudo)) return;
            int e = index.ExtensionId(n);
            bytes[e] += index.Size(n);
            onDisk[e] += index.SizeOnDisk(n);
            files[e]++;
        });
        var list = new List<TypeTotal>();
        for (int e = 0; e < bytes.Length; e++)
        {
            if (files[e] == 0) continue;
            var name = index.ExtensionName(e);
            list.Add(new TypeTotal(name, FileCategories.FromExtension(name), bytes[e], onDisk[e], files[e]));
        }
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    /// <summary>Bytes per colour group (uses the per-file category, so game-library files count as Games).</summary>
    public static IReadOnlyList<CategoryTotal> ByCategory(SpaceIndex index, int subtreeRoot)
    {
        var bytes = new long[FileCategories.Count];
        var files = new long[FileCategories.Count];
        index.ForEachInSubtree(subtreeRoot, n =>
        {
            if (index.IsDirectory(n) || index.Has(n, SpaceNodeFlags.Pseudo)) return;
            int c = (int)index.Category(n);
            bytes[c] += index.Size(n);
            files[c]++;
        });
        return Enumerable.Range(0, FileCategories.Count)
            .Where(c => files[c] > 0)
            .Select(c => new CategoryTotal((FileCategory)c, bytes[c], files[c]))
            .OrderByDescending(t => t.Bytes)
            .ToList();
    }

    /// <summary>
    /// Big and old files under <paramref name="subtreeRoot"/>, biggest first. Skips protected areas, links,
    /// online-only files (moving them would download them), OneDrive and AskFirst areas, and files inside
    /// code projects / app / game folders (those are treated as one unit).
    /// </summary>
    public static IReadOnlyList<int> BigAndOld(SpaceIndex index, int subtreeRoot, DateTime nowUtc, ISafetyGuard safety, BigAndOldRule? rule = null)
    {
        rule ??= BigAndOldRule.Default;
        var candidates = new List<int>();
        index.ForEachInSubtree(subtreeRoot, n =>
        {
            if ((index.Flags(n) & (NeverActionable | SpaceNodeFlags.CloudPlaceholder | SpaceNodeFlags.AskFirst)) != 0) return;
            if (rule.Matches(index.Size(n), index.LastTouchedUtc(n), index.LastAccessUtc(n), nowUtc)) candidates.Add(n);
        });
        // Few candidates: now it's affordable to ask the SafetyGuard about each one.
        var result = candidates.Where(n =>
        {
            var path = index.FullPath(n)!;
            try { return !safety.IsInCloudRoot(path) && !safety.IsInsideProjectOrAppFolder(path, out _); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        }).ToList();
        result.Sort((a, b) => index.Size(b).CompareTo(index.Size(a)));
        return result;
    }
}
