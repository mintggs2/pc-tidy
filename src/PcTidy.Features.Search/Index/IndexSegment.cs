namespace PcTidy.Features.Search.Index;

/// <summary>
/// The index of one root folder, stored as compact parallel arrays instead of one object per file
/// (Research §6: ~100 B per file). Per entry: parent (4 B), name start (4 B), name length (1 B), size (8 B),
/// modified (8 B), attributes (4 B), extension id (2 B), name-order slot (4 B), plus the name's characters
/// in one shared buffer (2 B per char). Immutable once built, so queries read it from any thread without locks.
/// Invariant: a folder's entry always comes before its children (parent index &lt; child index).
/// </summary>
public sealed class IndexSegment
{
    /// <summary>Extension id for names without an extension.</summary>
    public const ushort NoExtension = 0;
    /// <summary>Extension id used when the table is full; matching then falls back to the name itself.</summary>
    public const ushort OverflowExtension = ushort.MaxValue;
    /// <summary>Parent value for entries directly inside <see cref="Root"/>.</summary>
    public const int RootParent = -1;

    internal IndexSegment(string root, DateTime builtUtc, int count, int[] parent, int[] nameStart, byte[] nameLength, char[] names,
        long[] size, long[] modifiedTicks, int[] attributes, ushort[] extensionId, string[] extensions, int[] nameOrder,
        long inaccessibleFolders = 0, long skippedFolders = 0)
    {
        Root = root;
        BuiltUtc = builtUtc;
        Count = count;
        Parent = parent;
        NameStart = nameStart;
        NameLength = nameLength;
        Names = names;
        Size = size;
        ModifiedTicks = modifiedTicks;
        Attributes = attributes;
        ExtensionId = extensionId;
        Extensions = extensions;
        NameOrder = nameOrder;
        InaccessibleFolders = inaccessibleFolders;
        SkippedFolders = skippedFolders;
    }

    /// <summary>The indexed folder (normalised, no trailing backslash except drive roots).</summary>
    public string Root { get; }
    public DateTime BuiltUtc { get; }
    public int Count { get; }
    /// <summary>Folders that couldn't be opened while building (access denied, vanished).</summary>
    public long InaccessibleFolders { get; }
    /// <summary>Folders deliberately not indexed (protected areas, AppData, exclusions).</summary>
    public long SkippedFolders { get; }

    internal readonly int[] Parent;
    internal readonly int[] NameStart;
    internal readonly byte[] NameLength;
    internal readonly char[] Names;
    internal readonly long[] Size;
    internal readonly long[] ModifiedTicks;
    internal readonly int[] Attributes;
    internal readonly ushort[] ExtensionId;
    /// <summary>Extension table: lower-case, without the dot. Slot 0 is "" (no extension).</summary>
    internal readonly string[] Extensions;
    /// <summary>Entry numbers sorted by name (ordinal, ignoring case), so results come out sorted for free.</summary>
    internal readonly int[] NameOrder;

    public ReadOnlySpan<char> Name(int i) => Names.AsSpan(NameStart[i], NameLength[i]);
    public bool IsDirectory(int i) => (Attributes[i] & (int)FileAttributes.Directory) != 0;
    public long SizeOf(int i) => Size[i];
    public DateTime ModifiedUtc(int i) => new(ModifiedTicks[i], DateTimeKind.Utc);
    public FileAttributes AttributesOf(int i) => (FileAttributes)Attributes[i];
    public int ParentOf(int i) => Parent[i];

    /// <summary>Extension (lower-case, no dot) of entry <paramref name="i"/>.</summary>
    public string ExtensionOf(int i)
    {
        var id = ExtensionId[i];
        if (id != OverflowExtension) return Extensions[id];
        var name = Name(i);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..].ToString().ToLowerInvariant();
    }

    /// <summary>Full path of entry <paramref name="i"/>, rebuilt from its parents (allocates one string).</summary>
    public string FullPath(int i)
    {
        // Measure first, then fill in one pass from the end.
        int length = Name(i).Length, depth = 0;
        for (var p = Parent[i]; p != RootParent; p = Parent[p]) { length += NameLength[p] + 1; depth++; }
        var root = Root;
        bool rootHasSlash = root.EndsWith('\\');
        length += root.Length + (rootHasSlash ? 0 : 1);
        return string.Create(length, (this, i, root), static (span, s) =>
        {
            var (seg, entry, r) = s;
            int pos = span.Length;
            for (var e = entry; e != RootParent; e = seg.Parent[e])
            {
                var n = seg.Name(e);
                pos -= n.Length;
                n.CopyTo(span[pos..]);
                pos--;
                span[pos] = '\\';
            }
            // With a drive root ("C:\") the first separator lands on the root's own backslash, which is copied over it.
            r.AsSpan().CopyTo(span);
        });
    }

    /// <summary>Folder that holds entry <paramref name="i"/>.</summary>
    public string FolderOf(int i) => Parent[i] == RootParent ? Root : FullPath(Parent[i]);

    /// <summary>Exact bytes held by this segment's arrays (what the budget is measured against).</summary>
    public long ArrayBytes =>
        (long)Parent.Length * 4 + NameStart.Length * 4L + NameLength.Length + Names.Length * 2L + Size.Length * 8L
        + ModifiedTicks.Length * 8L + Attributes.Length * 4L + ExtensionId.Length * 2L + NameOrder.Length * 4L
        + Extensions.Sum(e => 24L + e.Length * 2L);
}
