using System.Collections;
using System.Text;

namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Compact, immutable in-memory tree of one scan (struct-of-arrays, no object per file; ~45 bytes + name per node).
/// Node 0 is the scanned root. Nodes are numbered breadth-first with siblings in size order (biggest first),
/// so a folder's children are one contiguous id range and names are stored in id order.
/// Folder sizes, sizes on disk, file counts and last-touched times are aggregated from their contents.
/// Safe to read from any thread.
/// </summary>
public sealed class SpaceIndex
{
    public const int RootNode = 0;

    private readonly int[] _parent;
    private readonly int[] _firstChild;   // Count + 1 entries: children of n are [_firstChild[n], _firstChild[n + 1])
    private readonly int[] _nameOffset;   // Count + 1 entries: name of n is _names[_nameOffset[n] .. _nameOffset[n + 1])
    private readonly char[] _names;
    private readonly ushort[] _flags;
    private readonly ushort[] _ext;
    private readonly byte[] _category;
    private readonly long[] _size;
    private readonly long[] _onDisk;
    private readonly int[] _touched;
    private readonly int[] _accessed;
    private readonly int[] _fileCount;
    private readonly string[] _extNames;

    internal SpaceIndex(
        int count, int[] parent, int[] firstChild, int[] nameOffset, char[] names,
        ushort[] flags, ushort[] ext, byte[] category, long[] size, long[] onDisk, int[] touched, int[] accessed,
        int[] fileCount, string[] extNames, SpaceScanSummary summary, int unscannedNode)
    {
        Count = count;
        _parent = parent; _firstChild = firstChild;
        _nameOffset = nameOffset; _names = names;
        _flags = flags; _ext = ext; _category = category;
        _size = size; _onDisk = onDisk; _touched = touched; _accessed = accessed; _fileCount = fileCount;
        _extNames = extNames;
        Summary = summary;
        UnscannedNode = unscannedNode;
    }

    public int Count { get; }
    public SpaceScanSummary Summary { get; }
    /// <summary>The "Unscanned / system" pseudo-node, or -1 when the drive's used space wasn't known.</summary>
    public int UnscannedNode { get; }
    public string RootPath => Summary.RootPath;
    public int ExtensionCount => _extNames.Length;

    public int Parent(int node) => _parent[node];
    /// <summary>Children of a folder, biggest first.</summary>
    public NodeRange Children(int node) => new(_firstChild[node], _firstChild[node + 1] - _firstChild[node]);
    public int ChildCount(int node) => _firstChild[node + 1] - _firstChild[node];
    public ReadOnlySpan<char> NameSpan(int node) => _names.AsSpan(_nameOffset[node], _nameOffset[node + 1] - _nameOffset[node]);
    public string Name(int node) => new(NameSpan(node));
    public SpaceNodeFlags Flags(int node) => (SpaceNodeFlags)_flags[node];
    public bool Has(int node, SpaceNodeFlags flag) => (_flags[node] & (ushort)flag) != 0;
    public bool IsDirectory(int node) => Has(node, SpaceNodeFlags.Directory);
    public long Size(int node) => _size[node];
    public long SizeOnDisk(int node) => _onDisk[node];
    public int FileCount(int node) => _fileCount[node];
    public DateTime LastTouchedUtc(int node) => SpaceTime.FromMinutes(_touched[node]);
    public DateTime LastAccessUtc(int node) => SpaceTime.FromMinutes(_accessed[node]);
    /// <summary>File: its type. Folder: the type that takes the most bytes inside it.</summary>
    public FileCategory Category(int node) => (FileCategory)_category[node];
    public int ExtensionId(int node) => _ext[node];
    public string ExtensionName(int extId) => _extNames[extId];

    /// <summary>Full path, or null for the pseudo "Unscanned" node.</summary>
    public string? FullPath(int node)
    {
        if (Has(node, SpaceNodeFlags.Pseudo)) return null;
        if (node == RootNode) return RootPath;
        Span<int> chain = stackalloc int[512];
        int depth = 0;
        for (int n = node; n != RootNode && depth < chain.Length; n = _parent[n]) chain[depth++] = n;
        var sb = new StringBuilder(RootPath.Length + depth * 16);
        sb.Append(RootPath.TrimEnd('\\'));
        for (int i = depth - 1; i >= 0; i--) sb.Append('\\').Append(NameSpan(chain[i]));
        return sb.ToString();
    }

    public int Depth(int node)
    {
        int d = 0;
        for (int n = node; n != RootNode; n = _parent[n]) d++;
        return d;
    }

    public bool IsAncestorOrSelf(int ancestor, int node)
    {
        for (int n = node; ; n = _parent[n])
        {
            if (n == ancestor) return true;
            if (n == RootNode) return false;
        }
    }

    /// <summary>The ancestors of <paramref name="node"/> from the root down to the node itself.</summary>
    public IReadOnlyList<int> PathFromRoot(int node)
    {
        var list = new List<int>();
        for (int n = node; ; n = _parent[n]) { list.Add(n); if (n == RootNode) break; }
        list.Reverse();
        return list;
    }

    /// <summary>Finds a node by full path (case-insensitive), or -1.</summary>
    public int Find(string fullPath)
    {
        var root = RootPath.TrimEnd('\\');
        var path = fullPath.TrimEnd('\\');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return RootNode;
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return -1;
        int node = RootNode;
        foreach (var part in path[(root.Length + 1)..].Split('\\'))
        {
            int next = -1;
            foreach (var c in Children(node))
                if (NameSpan(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next < 0) return -1;
            node = next;
        }
        return node;
    }

    /// <summary>Calls <paramref name="visit"/> for every node under <paramref name="subtreeRoot"/> (inclusive), depth-first.</summary>
    public void ForEachInSubtree(int subtreeRoot, Action<int> visit)
    {
        var stack = new Stack<int>();
        stack.Push(subtreeRoot);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            visit(n);
            foreach (var c in Children(n)) stack.Push(c);
        }
    }

    /// <summary>Bytes held by this index's arrays (what the plan's "~100 bytes/file" budget refers to).</summary>
    public long FootprintBytes =>
        (long)_parent.Length * 4 + _firstChild.Length * 4L + _nameOffset.Length * 4L
        + _names.Length * 2L + _flags.Length * 2L + _ext.Length * 2L + _category.Length
        + _size.Length * 8L + _onDisk.Length * 8L + _touched.Length * 4L + _accessed.Length * 4L + _fileCount.Length * 4L;
}

/// <summary>A contiguous run of node ids (a folder's children). Enumerable without allocating.</summary>
public readonly struct NodeRange : IReadOnlyList<int>
{
    public NodeRange(int start, int length) { Start = start; Length = length; }

    public int Start { get; }
    public int Length { get; }
    public int Count => Length;
    public int this[int index] => (uint)index < (uint)Length ? Start + index : throw new ArgumentOutOfRangeException(nameof(index));

    public Enumerator GetEnumerator() => new(Start, Start + Length);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<int>
    {
        private readonly int _end;
        private int _current;
        public Enumerator(int start, int end) { _current = start - 1; _end = end; }
        public int Current => _current;
        object IEnumerator.Current => _current;
        public bool MoveNext() => ++_current < _end;
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }
    }
}

/// <summary>Totals and facts about a finished scan.</summary>
public sealed record SpaceScanSummary
{
    public required string RootPath { get; init; }
    public long Files { get; init; }
    public long Folders { get; init; }
    public long Links { get; init; }
    /// <summary>Files and folders whose full path is over 260 characters.</summary>
    public long LongPaths { get; init; }
    /// <summary>Folders that couldn't be listed (access denied, vanished). Their contents are in "Unscanned".</summary>
    public long Inaccessible { get; init; }
    public long ScannedBytes { get; init; }
    public long ScannedBytesOnDisk { get; init; }
    /// <summary>Drive total / used, when the scan root is a whole drive.</summary>
    public long? DriveTotalBytes { get; init; }
    public long? DriveUsedBytes { get; init; }
    public int ClusterSize { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTime FinishedUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}
