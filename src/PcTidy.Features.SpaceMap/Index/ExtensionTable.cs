namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Interns file extensions to small ids without allocating a string per file
/// (open addressing keyed by the lower-cased span). Not thread-safe: one per scan worker.
/// Id 0 = no extension; <see cref="OverflowId"/> = everything past the id limit.
/// </summary>
internal sealed class ExtensionTable
{
    public const int MaxExtensionLength = 15;
    public const ushort NoExtensionId = 0;
    public const ushort OverflowId = 1;
    private const int MaxIds = ushort.MaxValue;

    private readonly List<string> _names = new() { "", "(other)" };
    private int[] _slots = new int[256]; // stores id+1; 0 = empty
    private int _count;

    public int Count => _names.Count;
    public string this[int id] => _names[id];

    /// <summary>Extension of a file name: text after the last dot, if the dot isn't the first char and it's short and space-free.</summary>
    public static ReadOnlySpan<char> ExtensionOf(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return default;
        var ext = fileName[(dot + 1)..];
        if (ext.Length > MaxExtensionLength || ext.IndexOf(' ') >= 0) return default;
        return ext;
    }

    public ushort GetOrAdd(ReadOnlySpan<char> ext)
    {
        if (ext.IsEmpty) return NoExtensionId;
        Span<char> lower = stackalloc char[MaxExtensionLength];
        int n = ext.ToLowerInvariant(lower);
        if (n < 0) return OverflowId;
        var key = lower[..n];

        uint hash = Hash(key);
        int mask = _slots.Length - 1;
        int i = (int)(hash & mask);
        while (true)
        {
            int slot = _slots[i];
            if (slot == 0) break;
            if (key.SequenceEqual(_names[slot - 1])) return (ushort)(slot - 1);
            i = (i + 1) & mask;
        }
        if (_names.Count >= MaxIds) return OverflowId;
        int id = _names.Count;
        _names.Add(new string(key));
        _slots[i] = id + 1;
        if (++_count * 2 > _slots.Length) Grow();
        return (ushort)id;
    }

    private void Grow()
    {
        var old = _slots;
        _slots = new int[old.Length * 2];
        int mask = _slots.Length - 1;
        foreach (var slot in old)
        {
            if (slot == 0) continue;
            int i = (int)(Hash(_names[slot - 1]) & mask);
            while (_slots[i] != 0) i = (i + 1) & mask;
            _slots[i] = slot;
        }
    }

    private static uint Hash(ReadOnlySpan<char> s)
    {
        uint h = 2166136261;
        foreach (var c in s) h = (h ^ c) * 16777619;
        return h;
    }
}
