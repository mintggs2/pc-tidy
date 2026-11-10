namespace PcTidy.Features.Search.Results;

/// <summary>One row of the results list. Built on demand when the list shows it (results can be millions long).</summary>
public sealed record SearchHit(int Ordinal, string Name, string Folder, bool IsFolder, long Size, DateTime ModifiedUtc)
{
    public string FullPath => Path.Combine(Folder, Name);
}

public enum SearchSort { Name, Folder, Size, Modified }

/// <summary>
/// The answer to one search: a count plus random access by position, so a virtualized list asks only for the rows
/// it shows. Implementations are immutable and safe to read from any thread.
/// </summary>
public abstract class SearchResults
{
    protected SearchResults(string backend, TimeSpan elapsed, IReadOnlyList<string> notes, bool truncated)
    {
        Backend = backend;
        Elapsed = elapsed;
        Notes = notes;
        Truncated = truncated;
    }

    /// <summary>"Everything" or "PC Tidy index".</summary>
    public string Backend { get; }
    public TimeSpan Elapsed { get; }
    /// <summary>Plain-English notes (unknown filter, Everything not answering...).</summary>
    public IReadOnlyList<string> Notes { get; }
    /// <summary>More matches exist than were kept.</summary>
    public bool Truncated { get; }

    public abstract int Count { get; }
    public abstract SearchHit this[int ordinal] { get; }

    /// <summary>A re-ordered copy (Name is the natural order). May take a moment on very long lists: call off the UI thread.</summary>
    public abstract SearchResults SortedBy(SearchSort sort, bool descending);

    public static SearchResults Empty(string backend, IReadOnlyList<string>? notes = null)
        => new ListResults(backend, Array.Empty<SearchHit>(), TimeSpan.Zero, notes ?? Array.Empty<string>(), false);
}

/// <summary>Results held as a plain list (Everything's answers, small sets).</summary>
public sealed class ListResults : SearchResults
{
    private readonly IReadOnlyList<SearchHit> _hits;

    public ListResults(string backend, IReadOnlyList<SearchHit> hits, TimeSpan elapsed, IReadOnlyList<string> notes, bool truncated)
        : base(backend, elapsed, notes, truncated) => _hits = hits;

    public override int Count => _hits.Count;
    public override SearchHit this[int ordinal] => _hits[ordinal] with { Ordinal = ordinal };

    public override SearchResults SortedBy(SearchSort sort, bool descending)
    {
        IEnumerable<SearchHit> ordered = sort switch
        {
            SearchSort.Folder => _hits.OrderBy(h => h.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase),
            SearchSort.Size => _hits.OrderBy(h => h.Size),
            SearchSort.Modified => _hits.OrderBy(h => h.ModifiedUtc),
            _ => _hits.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = ordered.ToList();
        if (descending) list.Reverse();
        return new ListResults(Backend, list, Elapsed, Notes, Truncated);
    }
}
