using System.Diagnostics;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.Query;

/// <summary>
/// Runs a <see cref="ParsedQuery"/> over the own index. Each segment is scanned in name order, split into chunks
/// that run in parallel, so the matches come out already sorted by name; the segments are then merged. Nothing is
/// read from disk: names, sizes and dates all come from the in-memory arrays.
/// </summary>
public static class QueryEngine
{
    public const string BackendName = "PC Tidy index";
    private const int ChunkSize = 32 * 1024;

    public static OwnIndexResults Search(IndexSnapshot snapshot, ParsedQuery query, int maxResults = 2_000_000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (query.IsEmpty || snapshot.Segments.Count == 0)
            return new OwnIndexResults(snapshot, Array.Empty<long>(), sw.Elapsed, query.Warnings, false);

        var perSegment = new List<int>[snapshot.Segments.Count];
        for (int s = 0; s < snapshot.Segments.Count; s++)
            perSegment[s] = MatchSegment(new SegmentContext(snapshot.Segments[s]), query.Root!, ct);

        var merged = Merge(snapshot, perSegment, ct);
        if (query.Dupe) merged = KeepDuplicates(snapshot, merged);
        bool truncated = merged.Count > maxResults;
        var refs = (truncated ? merged.GetRange(0, maxResults) : merged).ToArray();
        return new OwnIndexResults(snapshot, refs, sw.Elapsed, query.Warnings, truncated);
    }

    internal static long Ref(int segment, int entry) => ((long)segment << 32) | (uint)entry;
    internal static int SegmentOf(long r) => (int)(r >> 32);
    internal static int EntryOf(long r) => (int)(r & 0xFFFFFFFF);

    private static List<int> MatchSegment(SegmentContext sc, QueryNode root, CancellationToken ct)
    {
        var seg = sc.Segment;
        var match = Compile(root, sc);
        int chunks = (seg.Count + ChunkSize - 1) / ChunkSize;
        var found = new List<int>[chunks];
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            var list = new List<int>();
            var order = seg.NameOrder;
            int end = Math.Min(seg.Count, (c + 1) * ChunkSize);
            for (int k = c * ChunkSize; k < end; k++)
            {
                int i = order[k];
                if (match(i)) list.Add(i);
            }
            found[c] = list;
        });
        var all = new List<int>(found.Sum(f => f.Count));
        foreach (var f in found) all.AddRange(f);
        return all;
    }

    /// <summary>k-way merge of per-segment name-sorted lists (k is the number of roots: small).</summary>
    private static List<long> Merge(IndexSnapshot snapshot, List<int>[] perSegment, CancellationToken ct)
    {
        var result = new List<long>(perSegment.Sum(p => p.Count));
        if (perSegment.Length == 1)
        {
            foreach (var i in perSegment[0]) result.Add(Ref(0, i));
            return result;
        }
        var heads = new int[perSegment.Length];
        while (true)
        {
            if ((result.Count & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int best = -1;
            for (int s = 0; s < perSegment.Length; s++)
            {
                if (heads[s] >= perSegment[s].Count) continue;
                if (best < 0) { best = s; continue; }
                var a = snapshot.Segments[s].Name(perSegment[s][heads[s]]);
                var b = snapshot.Segments[best].Name(perSegment[best][heads[best]]);
                if (a.CompareTo(b, StringComparison.OrdinalIgnoreCase) < 0) best = s;
            }
            if (best < 0) break;
            result.Add(Ref(best, perSegment[best][heads[best]++]));
        }
        return result;
    }

    /// <summary>
    /// dupe: files that share their name (ignoring case) and size with another match. The list is in name order,
    /// so same-named files are neighbours; within a run, sizes are grouped and only groups of two or more stay.
    /// </summary>
    private static List<long> KeepDuplicates(IndexSnapshot snapshot, List<long> sorted)
    {
        var files = sorted.Where(r => !snapshot.Segments[SegmentOf(r)].IsDirectory(EntryOf(r))).ToList();
        var keep = new List<long>();
        int start = 0;
        while (start < files.Count)
        {
            int end = start + 1;
            var name = NameOf(snapshot, files[start]);
            while (end < files.Count && NameOf(snapshot, files[end]).Equals(name, StringComparison.OrdinalIgnoreCase)) end++;
            if (end - start > 1)
            {
                foreach (var group in files.GetRange(start, end - start).GroupBy(r => snapshot.Segments[SegmentOf(r)].SizeOf(EntryOf(r))).OrderBy(g => g.Key))
                    if (group.Count() > 1) keep.AddRange(group);
            }
            start = end;
        }
        return keep;
    }

    private static ReadOnlySpan<char> NameOf(IndexSnapshot s, long r) => s.Segments[SegmentOf(r)].Name(EntryOf(r));

    // ------------------------------------------------------------------ compiled matchers

    private delegate bool Matcher(int entry);

    private static Matcher Compile(QueryNode node, SegmentContext sc)
    {
        var seg = sc.Segment;
        switch (node)
        {
            case AndNode and:
            {
                var parts = and.Items.Select(n => Compile(n, sc)).ToArray();
                return i => { foreach (var p in parts) if (!p(i)) return false; return true; };
            }
            case OrNode or:
            {
                var parts = or.Items.Select(n => Compile(n, sc)).ToArray();
                return i => { foreach (var p in parts) if (p(i)) return true; return false; };
            }
            case NotNode not:
            {
                var inner = Compile(not.Item, sc);
                return i => !inner(i);
            }
            case MatchAllNode:
                return _ => true;
            case TextNode { MatchPath: true } text:
                return PathMatcher(sc, text.Text, text.Wildcard);
            case TextNode { Wildcard: true } wild:
            {
                var pattern = wild.Text;
                return i => Wildcard.IsMatch(seg.Name(i), pattern);
            }
            case TextNode word:
            {
                var t = word.Text;
                return i => seg.Name(i).Contains(t, StringComparison.OrdinalIgnoreCase);
            }
            case PathNode path:
                return PathMatcher(sc, path.Text, path.Wildcard);
            case KindNode kind:
            {
                var inner = kind.Inner is null ? null : Compile(kind.Inner, sc);
                bool folders = kind.Folders;
                return i => seg.IsDirectory(i) == folders && (inner is null || inner(i));
            }
            case ExtNode ext:
                return ExtMatcher(seg, ext);
            case SizeNode size:
            {
                long min = size.Min, max = size.Max;
                return i => !seg.IsDirectory(i) && seg.Size[i] >= min && seg.Size[i] <= max;
            }
            case DateNode date:
            {
                long from = date.FromUtc.Ticks, to = date.ToUtc.Ticks;
                return i => seg.ModifiedTicks[i] >= from && seg.ModifiedTicks[i] < to;
            }
            default:
                return _ => false;
        }
    }

    private static Matcher ExtMatcher(IndexSegment seg, ExtNode ext)
    {
        if (ext.Extensions.Count == 0) return i => !seg.IsDirectory(i) && seg.ExtensionId[i] == IndexSegment.NoExtension;
        var wanted = new bool[seg.Extensions.Length];
        for (int k = 0; k < seg.Extensions.Length; k++) wanted[k] = ext.Extensions.Contains(seg.Extensions[k], StringComparer.Ordinal);
        var set = ext.Extensions.ToHashSet(StringComparer.Ordinal);
        return i =>
        {
            var id = seg.ExtensionId[i];
            if (id == IndexSegment.OverflowExtension) return set.Contains(seg.ExtensionOf(i));
            return wanted[id] && !seg.IsDirectory(i);
        };
    }

    /// <summary>
    /// Text anywhere in the full path. Without a backslash in the text a match can't span two names, so "does the
    /// folder's path contain it" is worked out once per folder and remembered; otherwise each full path is assembled
    /// in a reusable buffer (folder paths themselves are cached per query).
    /// </summary>
    private static Matcher PathMatcher(SegmentContext sc, string text, bool wildcard)
    {
        var seg = sc.Segment;
        if (wildcard) return i => Wildcard.IsMatch(sc.FullPathSpan(i), text);
        if (!text.Contains('\\'))
        {
            var memo = new sbyte[seg.Count];
            bool rootHas = seg.Root.Contains(text, StringComparison.OrdinalIgnoreCase);
            bool FolderHas(int d)
            {
                if (d == IndexSegment.RootParent) return rootHas;
                var m = Volatile.Read(ref memo[d]);
                if (m != 0) return m > 0;
                bool has = seg.Name(d).Contains(text, StringComparison.OrdinalIgnoreCase) || FolderHas(seg.Parent[d]);
                Volatile.Write(ref memo[d], has ? (sbyte)1 : (sbyte)-1);
                return has;
            }
            return i => seg.Name(i).Contains(text, StringComparison.OrdinalIgnoreCase) || FolderHas(seg.Parent[i]);
        }
        return i => sc.FullPathSpan(i).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Per-query helpers for one segment: lazily cached folder paths and a per-thread path buffer.</summary>
    private sealed class SegmentContext(IndexSegment segment)
    {
        [ThreadStatic] private static char[]? t_buffer;
        private string?[]? _folderPaths;

        public IndexSegment Segment { get; } = segment;

        public string FolderPath(int d)
        {
            if (d == IndexSegment.RootParent) return Segment.Root;
            var cache = _folderPaths ?? Interlocked.CompareExchange(ref _folderPaths, new string?[Segment.Count], null) ?? _folderPaths!;
            var known = Volatile.Read(ref cache[d]);
            if (known is not null) return known;
            var parent = FolderPath(Segment.Parent[d]);
            var path = parent.EndsWith('\\') ? string.Concat(parent, Segment.Name(d)) : string.Concat(parent, "\\", Segment.Name(d));
            Volatile.Write(ref cache[d], path);
            return path;
        }

        /// <summary>The full path of entry <paramref name="i"/> in this thread's buffer (valid until the next call).</summary>
        public ReadOnlySpan<char> FullPathSpan(int i)
        {
            var folder = FolderPath(Segment.Parent[i]);
            var name = Segment.Name(i);
            int sep = folder.EndsWith('\\') ? 0 : 1;
            int length = folder.Length + sep + name.Length;
            var buffer = t_buffer;
            if (buffer is null || buffer.Length < length) t_buffer = buffer = new char[Math.Max(length, 1024)];
            folder.CopyTo(buffer);
            if (sep == 1) buffer[folder.Length] = '\\';
            name.CopyTo(buffer.AsSpan(folder.Length + sep));
            return buffer.AsSpan(0, length);
        }
    }
}

/// <summary>Own-index results: 8 bytes per match (segment + entry); rows are built only when shown.</summary>
public sealed class OwnIndexResults : SearchResults
{
    private readonly IndexSnapshot _snapshot;
    private readonly long[] _refs;
    /// <summary>The matches in name order (the engine's natural order); every re-sort starts from it so ties stay by name.</summary>
    private readonly long[] _byName;

    internal OwnIndexResults(IndexSnapshot snapshot, long[] refs, TimeSpan elapsed, IReadOnlyList<string> notes, bool truncated, long[]? byName = null)
        : base(QueryEngine.BackendName, elapsed, notes, truncated)
    {
        _snapshot = snapshot;
        _refs = refs;
        _byName = byName ?? refs;
    }

    public override int Count => _refs.Length;

    public override SearchHit this[int ordinal]
    {
        get
        {
            var (seg, i) = Resolve(_refs[ordinal]);
            return new SearchHit(ordinal, seg.Name(i).ToString(), seg.FolderOf(i), seg.IsDirectory(i), seg.SizeOf(i), seg.ModifiedUtc(i));
        }
    }

    public override SearchResults SortedBy(SearchSort sort, bool descending)
    {
        long[] refs;
        switch (sort)
        {
            case SearchSort.Size: refs = StableBy(r => { var (s, i) = Resolve(r); return s.Size[i]; }); break;
            case SearchSort.Modified: refs = StableBy(r => { var (s, i) = Resolve(r); return s.ModifiedTicks[i]; }); break;
            case SearchSort.Folder:
                refs = _byName.OrderBy(r => { var (s, i) = Resolve(r); return s.FolderOf(i); }, StringComparer.OrdinalIgnoreCase).ToArray();
                break;
            default: refs = (long[])_byName.Clone(); break;
        }
        if (descending) Array.Reverse(refs);
        return new OwnIndexResults(_snapshot, refs, Elapsed, Notes, Truncated, _byName);
    }

    /// <summary>Stable sort by a numeric key (OrderBy keeps equal keys in name order).</summary>
    private long[] StableBy(Func<long, long> key) => _byName.OrderBy(key).ToArray();

    private (IndexSegment Segment, int Entry) Resolve(long r) => (_snapshot.Segments[QueryEngine.SegmentOf(r)], QueryEngine.EntryOf(r));
}
