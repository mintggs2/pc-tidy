using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.Backends;

/// <summary>Something that can answer a search (Everything, or PC Tidy's own index). Read-only.</summary>
public interface ISearchBackend
{
    /// <summary>"Everything" / "PC Tidy index" (shown under the search box).</summary>
    string Name { get; }

    /// <summary>Cheap and never blocks (safe on the UI thread, e.g. for the status line). False = skipped without a word.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// An up-to-date answer, which may take a moment (looking for a running program, checking its files). Called off
    /// the UI thread, right before a search.
    /// </summary>
    bool CheckAvailable() => IsAvailable;

    Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct);
}
