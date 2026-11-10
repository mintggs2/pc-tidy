namespace PcTidy.Features.Search.Index;

/// <summary>
/// Collects entries into growable arrays and freezes them into an <see cref="IndexSegment"/>.
/// Callers must add a folder before anything inside it (the walker and the tests both do).
/// </summary>
public sealed class IndexSegmentBuilder
{
    private const int MaxNameLength = byte.MaxValue; // NTFS names are at most 255 UTF-16 units

    private readonly string _root;
    private int _count;
    private int[] _parent;
    private int[] _nameStart;
    private byte[] _nameLength;
    private char[] _names;
    private int _namesUsed;
    private long[] _size;
    private long[] _modified;
    private int[] _attributes;
    private ushort[] _extensionId;
    private readonly List<string> _extensions = new() { "" };
    private readonly Dictionary<string, ushort> _extensionIds = new(StringComparer.Ordinal) { [""] = IndexSegment.NoExtension };

    public IndexSegmentBuilder(string root, int capacity = 1024)
    {
        _root = root;
        capacity = Math.Max(16, capacity);
        _parent = new int[capacity];
        _nameStart = new int[capacity];
        _nameLength = new byte[capacity];
        _names = new char[capacity * 16];
        _size = new long[capacity];
        _modified = new long[capacity];
        _attributes = new int[capacity];
        _extensionId = new ushort[capacity];
    }

    public int Count => _count;
    public long InaccessibleFolders { get; set; }
    public long SkippedFolders { get; set; }

    /// <summary>Adds one entry and returns its number (used as the parent of its children).</summary>
    public int Add(int parent, ReadOnlySpan<char> name, bool isDirectory, long size, DateTime modifiedUtc, FileAttributes attributes)
    {
        if (parent < IndexSegment.RootParent || parent >= _count) throw new ArgumentOutOfRangeException(nameof(parent), "A folder must be added before its contents.");
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (_count == _parent.Length) GrowEntries();
        if (_namesUsed + name.Length > _names.Length) Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + name.Length));

        int i = _count++;
        _parent[i] = parent;
        _nameStart[i] = _namesUsed;
        _nameLength[i] = (byte)name.Length;
        name.CopyTo(_names.AsSpan(_namesUsed));
        _namesUsed += name.Length;
        _size[i] = isDirectory ? 0 : Math.Max(0, size);
        _modified[i] = modifiedUtc.Kind == DateTimeKind.Local ? modifiedUtc.ToUniversalTime().Ticks : modifiedUtc.Ticks;
        var attrs = isDirectory ? attributes | FileAttributes.Directory : attributes & ~FileAttributes.Directory;
        _attributes[i] = (int)attrs;
        _extensionId[i] = isDirectory ? IndexSegment.NoExtension : ExtensionIdOf(name);
        return i;
    }

    private ushort ExtensionIdOf(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return IndexSegment.NoExtension;
        var ext = name[(dot + 1)..];
        if (ext.Length > 16) return IndexSegment.OverflowExtension; // not a real extension ("report.final version 2")
        var key = ext.ToString().ToLowerInvariant();
        if (_extensionIds.TryGetValue(key, out var id)) return id;
        if (_extensions.Count >= IndexSegment.OverflowExtension) return IndexSegment.OverflowExtension;
        id = (ushort)_extensions.Count;
        _extensions.Add(key);
        _extensionIds[key] = id;
        return id;
    }

    private void GrowEntries()
    {
        var n = _parent.Length * 2;
        Array.Resize(ref _parent, n);
        Array.Resize(ref _nameStart, n);
        Array.Resize(ref _nameLength, n);
        Array.Resize(ref _size, n);
        Array.Resize(ref _modified, n);
        Array.Resize(ref _attributes, n);
        Array.Resize(ref _extensionId, n);
    }

    /// <summary>Freezes the entries (arrays trimmed to size) and sorts the name order.</summary>
    public IndexSegment Build(DateTime builtUtc, CancellationToken ct = default)
    {
        int n = _count;
        var names = _names.AsSpan(0, _namesUsed).ToArray();
        var nameStart = _nameStart.AsSpan(0, n).ToArray();
        var nameLength = _nameLength.AsSpan(0, n).ToArray();
        ct.ThrowIfCancellationRequested();
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, new NameComparer(names, nameStart, nameLength));
        ct.ThrowIfCancellationRequested();
        return new IndexSegment(_root, builtUtc, n,
            _parent.AsSpan(0, n).ToArray(), nameStart, nameLength, names,
            _size.AsSpan(0, n).ToArray(), _modified.AsSpan(0, n).ToArray(), _attributes.AsSpan(0, n).ToArray(),
            _extensionId.AsSpan(0, n).ToArray(), _extensions.ToArray(), order, InaccessibleFolders, SkippedFolders);
    }

    /// <summary>Name order: ordinal ignoring case (culture-independent), ties by entry number so the sort is stable.</summary>
    internal sealed class NameComparer(char[] names, int[] start, byte[] length) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var c = ((ReadOnlySpan<char>)names.AsSpan(start[a], length[a])).CompareTo(names.AsSpan(start[b], length[b]), StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.CompareTo(b);
        }
    }
}
