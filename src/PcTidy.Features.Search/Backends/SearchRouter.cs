using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.Backends;

/// <summary>
/// Picks who answers: Everything when it's installed, running and allowed in Settings; otherwise PC Tidy's own
/// index. If Everything fails mid-search (closed, busy), the same search is answered by the own index instead,
/// with a short note, never an error.
/// </summary>
public sealed class SearchRouter
{
    private readonly ISearchBackend? _everything;
    private readonly ISearchBackend _own;
    private readonly Func<bool> _useEverything;
    private readonly TimeSpan _timeout;

    /// <summary>How long a search waits for Everything before PC Tidy's own index answers instead.</summary>
    public static readonly TimeSpan DefaultEverythingTimeout = TimeSpan.FromSeconds(5);

    public SearchRouter(ISearchBackend own, ISearchBackend? everything = null, Func<bool>? useEverything = null, TimeSpan? everythingTimeout = null)
    {
        _own = own;
        _everything = everything;
        _useEverything = useEverything ?? (() => true);
        _timeout = everythingTimeout ?? DefaultEverythingTimeout;
    }

    /// <summary>The backend the next search will probably use (the last known answer; never blocks, so the UI may ask).</summary>
    public ISearchBackend Active => _everything is not null && _useEverything() && SafeAvailable(_everything, fresh: false) ? _everything : _own;

    /// <summary>
    /// Runs a search. The backend is picked off the UI thread (an up-to-date Everything check may take a moment), and
    /// an Everything query that is slow, busy with an earlier one, or failing is answered by the own index instead.
    /// </summary>
    public async Task<SearchResults> SearchAsync(string text, int maxResults, CancellationToken ct, TimeProvider? clock = null)
    {
        var query = QueryParser.Parse(text, clock);
        var useEverything = _everything is not null && _useEverything()
                            && await Task.Run(() => SafeAvailable(_everything, fresh: true), ct).ConfigureAwait(false);
        if (!useEverything) return await _own.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
        try
        {
            // WaitAsync stops waiting on a timeout or cancel; the Everything query itself can't be stopped and ends on its own.
            return await _everything!.SearchAsync(query, maxResults, ct).WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not TimeoutException) (_everything as EverythingBackend)?.Invalidate();
            var fallback = await _own.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
            return new NotedResults(fallback, ex is TimeoutException
                ? "Everything was taking too long, so PC Tidy's own index answered."
                : "Everything didn't answer, so PC Tidy's own index was used.");
        }
    }

    private static bool SafeAvailable(ISearchBackend b, bool fresh)
    {
        try { return fresh ? b.CheckAvailable() : b.IsAvailable; }
        catch (Exception) { return false; }
    }

    /// <summary>Same results with one extra note.</summary>
    private sealed class NotedResults(SearchResults inner, string note)
        : SearchResults(inner.Backend, inner.Elapsed, inner.Notes.Append(note).ToList(), inner.Truncated)
    {
        public override int Count => inner.Count;
        public override SearchHit this[int ordinal] => inner[ordinal];
        public override SearchResults SortedBy(SearchSort sort, bool descending) => new NotedResults(inner.SortedBy(sort, descending), note);
    }
}
