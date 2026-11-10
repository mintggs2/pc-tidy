using PcTidy.Core.Model;

namespace PcTidy.Features.Photos.Photos;

/// <summary>A photo that may belong to a burst: it has a real "date taken" and was measured.</summary>
public sealed record BurstCandidate(string Path, DateTime Taken, ImageMetrics Metrics, long Size);

/// <summary>Shots taken moments apart that look almost the same. <see cref="Keep"/> is the sharpest.</summary>
public sealed record Burst(IReadOnlyList<BurstCandidate> Shots, BurstCandidate Keep)
{
    public IEnumerable<BurstCandidate> Extras => Shots.Where(s => !ReferenceEquals(s, Keep));
}

/// <summary>
/// Finds bursts: photos taken within <see cref="Window"/> of each other whose perceptual hashes are close. Two shots
/// join when both hold; a burst is everything linked that way. Only real "date taken" values count (modified dates of
/// copies say nothing about when a photo was taken).
/// </summary>
public static class BurstFinder
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>Max differing hash bits: normal sensitivity (Quick) and higher sensitivity (Deep), Plan §2.</summary>
    public static int MaxDistance(ScanDepth depth) => depth == ScanDepth.Deep ? 14 : 10;

    public static IReadOnlyList<Burst> Find(IEnumerable<BurstCandidate> photos, int maxDistance)
    {
        var list = photos.OrderBy(p => p.Taken).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }

        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count && list[j].Taken - list[i].Taken <= Window; j++)
                if (ImageMath.Distance(list[i].Metrics.Hash, list[j].Metrics.Hash) <= maxDistance)
                    parent[Root(j)] = Root(i);

        return Enumerable.Range(0, list.Count)
            .GroupBy(Root)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var shots = g.Select(i => list[i]).ToList();
                return new Burst(shots, PickKeeper(shots));
            })
            .OrderBy(b => b.Shots[0].Taken)
            .ToList();
    }

    /// <summary>
    /// Sharpest first; then the bigger picture, the bigger file, the shorter name ("IMG_1.jpg" over "IMG_1 (1).jpg")
    /// and the name itself, so the choice is always the same.
    /// </summary>
    private static BurstCandidate PickKeeper(IEnumerable<BurstCandidate> shots)
        => shots.OrderByDescending(s => s.Metrics.Sharpness)
            .ThenByDescending(s => (long)s.Metrics.Width * s.Metrics.Height)
            .ThenByDescending(s => s.Size)
            .ThenBy(s => Path.GetFileName(s.Path).Length)
            .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .First();
}
