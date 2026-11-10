using PcTidy.Catalog.Developer;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Projects;

/// <summary>A folder that holds a code project (found by its markers: .git, package.json, *.sln...).</summary>
internal sealed record DevProject(string Root, string Name, IReadOnlyList<string> Markers, DateTime FolderWriteUtc);

/// <summary>A rebuildable folder found inside a project, measured exactly (links inside are counted, never followed).</summary>
internal sealed record RebuildCandidate(
    string Path,
    RebuildableFolderRule Rule,
    RebuildConfidence Confidence,
    DateTime FolderWriteUtc,
    FolderMeasure.Totals Size,
    bool HasDependencyList);

/// <summary>What one look through a project found.</summary>
/// <param name="LastActivityUtc">Newest of git's own activity files and the project's files (outside build folders). Null = nothing dated.</param>
/// <param name="ActivitySource">Plain words for where that date came from ("git", "a file").</param>
/// <param name="Truncated">The project was too big to look through fully: treated as recently used (never cleaned).</param>
internal sealed record ProjectReport(
    DevProject Project,
    DateTime? LastActivityUtc,
    string ActivitySource,
    IReadOnlyList<RebuildCandidate> Candidates,
    int LinksSkipped,
    bool Truncated)
{
    public long CandidateBytes => Candidates.Sum(c => c.Size.Bytes);
}
