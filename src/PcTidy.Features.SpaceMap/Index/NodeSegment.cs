namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Growable struct-of-arrays node store filled by one scan worker. Parent indices are local to the
/// segment; <see cref="ExternalParent"/> (-1) means "the segment's root folder", which lives in another segment.
/// Merged into a <see cref="SpaceIndex"/> when the scan finishes.
/// </summary>
internal sealed class NodeSegment
{
    public const int ExternalParent = -1;

    public int Count;
    public int[] Parent;
    public int[] NameOffset;
    public ushort[] NameLength;
    public ushort[] Flags;
    public ushort[] Ext;
    public long[] Size;
    public long[] OnDisk;
    public int[] Touched;
    public int[] Accessed;

    public char[] Names;
    public int NamesLength;

    public readonly ExtensionTable Extensions = new();

    public NodeSegment(int capacity = 1024)
    {
        Parent = new int[capacity];
        NameOffset = new int[capacity];
        NameLength = new ushort[capacity];
        Flags = new ushort[capacity];
        Ext = new ushort[capacity];
        Size = new long[capacity];
        OnDisk = new long[capacity];
        Touched = new int[capacity];
        Accessed = new int[capacity];
        Names = new char[capacity * 16];
    }

    public int Add(int parent, ReadOnlySpan<char> name, SpaceNodeFlags flags, ushort ext, long size, long onDisk, int touched, int accessed)
    {
        if (Count == Parent.Length) GrowNodes();
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];
        if (NamesLength + name.Length > Names.Length)
            Array.Resize(ref Names, Math.Max(Names.Length * 2, NamesLength + name.Length));
        name.CopyTo(Names.AsSpan(NamesLength));

        int i = Count++;
        Parent[i] = parent;
        NameOffset[i] = NamesLength;
        NameLength[i] = (ushort)name.Length;
        NamesLength += name.Length;
        Flags[i] = (ushort)flags;
        Ext[i] = ext;
        Size[i] = size;
        OnDisk[i] = onDisk;
        Touched[i] = touched;
        Accessed[i] = accessed;
        return i;
    }

    public ReadOnlySpan<char> NameOf(int i) => Names.AsSpan(NameOffset[i], NameLength[i]);

    private void GrowNodes()
    {
        int n = Parent.Length * 2;
        Array.Resize(ref Parent, n);
        Array.Resize(ref NameOffset, n);
        Array.Resize(ref NameLength, n);
        Array.Resize(ref Flags, n);
        Array.Resize(ref Ext, n);
        Array.Resize(ref Size, n);
        Array.Resize(ref OnDisk, n);
        Array.Resize(ref Touched, n);
        Array.Resize(ref Accessed, n);
    }
}
