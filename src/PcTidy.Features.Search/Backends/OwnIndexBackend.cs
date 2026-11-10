using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.Backends;

/// <summary>Searches PC Tidy's own in-memory index (always available, possibly still filling up).</summary>
public sealed class OwnIndexBackend(Func<IndexSnapshot> snapshot) : ISearchBackend
{
    public string Name => QueryEngine.BackendName;
    public bool IsAvailable => true;

    public Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct)
    {
        var current = snapshot();
        return Task.Run<SearchResults>(() => QueryEngine.Search(current, query, maxResults, ct), ct);
    }
}
