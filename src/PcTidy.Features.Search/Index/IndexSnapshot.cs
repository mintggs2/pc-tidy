namespace PcTidy.Features.Search.Index;

/// <summary>
/// The whole own index at one moment: one immutable segment per root. A refresh builds a new segment and swaps
/// in a new snapshot, so a query that's running keeps reading the old one safely.
/// </summary>
public sealed class IndexSnapshot
{
    public static readonly IndexSnapshot Empty = new(Array.Empty<IndexSegment>(), 0);

    public IndexSnapshot(IReadOnlyList<IndexSegment> segments, long version)
    {
        Segments = segments;
        Version = version;
    }

    public IReadOnlyList<IndexSegment> Segments { get; }
    /// <summary>Bumped on every swap; lets the page tell the user "the index was updated".</summary>
    public long Version { get; }

    public long EntryCount => Segments.Sum(s => (long)s.Count);
    public long ArrayBytes => Segments.Sum(s => s.ArrayBytes);
    public double BytesPerEntry => EntryCount == 0 ? 0 : (double)ArrayBytes / EntryCount;
    public DateTime? OldestBuildUtc => Segments.Count == 0 ? null : Segments.Min(s => s.BuiltUtc);

    public IndexSegment? SegmentFor(string root)
        => Segments.FirstOrDefault(s => string.Equals(s.Root, root, StringComparison.OrdinalIgnoreCase));

    /// <summary>A new snapshot with <paramref name="segment"/> replacing the one for the same root (or added).</summary>
    public IndexSnapshot With(IndexSegment segment)
    {
        var list = Segments.Where(s => !string.Equals(s.Root, segment.Root, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(segment);
        list.Sort((a, b) => string.Compare(a.Root, b.Root, StringComparison.OrdinalIgnoreCase));
        return new IndexSnapshot(list, Version + 1);
    }

    /// <summary>Keeps only the given roots (a root the user removed disappears from results straight away).</summary>
    public IndexSnapshot Only(IReadOnlyCollection<string> roots)
    {
        var keep = Segments.Where(s => roots.Contains(s.Root, StringComparer.OrdinalIgnoreCase)).ToList();
        return keep.Count == Segments.Count ? this : new IndexSnapshot(keep, Version + 1);
    }
}
