using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>A folder the user uses often, with its fading score.</summary>
public sealed record FrequentFolder(string Folder, double Score, int Visits, DateTime FirstUtc, DateTime LastUtc)
{
    public string Id => "folder:" + Folder.ToLowerInvariant();
}

/// <summary>
/// "Folders you use most": every visit counts 1, fading with a 14-day half-life (frequent + recent = "frecency").
/// Pure. Old visits are folded into a single score (<see cref="FolderTotals"/>) that keeps fading the same way.
/// </summary>
public static class Frecency
{
    /// <summary>Folders below this score have faded away (≈ one visit three months ago).</summary>
    public const double MinScore = 0.02;

    public static double Weight(DateTime visitUtc, DateTime nowUtc, TimeSpan? halfLife = null)
    {
        var age = (nowUtc - visitUtc).TotalDays;
        if (age < 0) age = 0;
        return Math.Pow(2, -age / (halfLife ?? LearningRules.FolderHalfLife).TotalDays);
    }

    /// <summary>A folded score, decayed from when it was valid to now.</summary>
    public static double Decay(double score, DateTime scoreUtc, DateTime nowUtc) => score * Weight(scoreUtc, nowUtc);

    public static IReadOnlyList<FrequentFolder> Rank(IEnumerable<FolderVisit> visits, IEnumerable<FolderTotals>? totals, DateTime nowUtc, int max = 10)
    {
        var map = new Dictionary<string, (double Score, int Count, DateTime First, DateTime Last, string Display)>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder, double score, int count, DateTime first, DateTime last)
        {
            if (map.TryGetValue(folder, out var cur))
                map[folder] = (cur.Score + score, cur.Count + count, first < cur.First ? first : cur.First, last > cur.Last ? last : cur.Last, cur.Display);
            else map[folder] = (score, count, first, last, folder);
        }
        foreach (var v in visits) Add(v.Folder, Weight(v.Utc, nowUtc), 1, v.Utc, v.Utc);
        foreach (var t in totals ?? Enumerable.Empty<FolderTotals>()) Add(t.Folder, Decay(t.Score, t.ScoreUtc, nowUtc), t.Count, t.FirstUtc, t.LastUtc);

        return map.Values.Where(v => v.Score >= MinScore)
            .OrderByDescending(v => v.Score).ThenBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(v => new FrequentFolder(v.Display, Math.Round(v.Score, 4), v.Count, v.First, v.Last))
            .ToList();
    }

    /// <summary>Folds visits into an existing total (or a new one), valid at <paramref name="nowUtc"/>.</summary>
    public static FolderTotals Fold(string folder, IEnumerable<FolderVisit> visits, FolderTotals? existing, DateTime nowUtc)
    {
        var list = visits.ToList();
        double score = list.Sum(v => Weight(v.Utc, nowUtc)) + (existing is null ? 0 : Decay(existing.Score, existing.ScoreUtc, nowUtc));
        var first = list.Select(v => v.Utc).Append(existing?.FirstUtc ?? DateTime.MaxValue).Min();
        var last = list.Select(v => v.Utc).Append(existing?.LastUtc ?? DateTime.MinValue).Max();
        return new FolderTotals(folder, score, nowUtc, list.Count + (existing?.Count ?? 0), first, last);
    }
}
