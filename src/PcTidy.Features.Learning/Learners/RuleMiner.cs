using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>A pattern in the user's own moves that is strong enough to suggest as an Organize rule.</summary>
/// <param name="Moves">Weighted moves of this kind to <paramref name="Destination"/> (one-off bulk runs count once).</param>
/// <param name="TotalMoves">Weighted moves of this kind to anywhere.</param>
/// <param name="CommonSource">The folder (almost) all of these moves came from, or null.</param>
/// <param name="MoveIds">Raw move rows behind it (empty for evidence that was already folded into totals).</param>
public sealed record RuleCandidate(
    string Key, MoveFeature Feature, string Value, string Destination,
    double Moves, double TotalMoves, int Days, DateTime FirstUtc, DateTime LastUtc,
    IReadOnlyList<string> Examples, string? CommonSource, IReadOnlySet<long> MoveIds)
{
    public double Confidence => TotalMoves <= 0 ? 0 : Moves / TotalMoves;
}

/// <summary>
/// Mines PC Tidy's own move log for rules: for every feature value (extension, name pattern, download site, source
/// folder, size bucket) the most common destination is suggested when it has ≥5 moves, ≥80% of all such moves and
/// ≥2 different days. A run that moved more than <see cref="LearningRules.BulkSessionMoves"/> files of one kind is a
/// one-off tidy and counts as one move. Pure.
/// </summary>
public static class RuleMiner
{
    private sealed class Bucket
    {
        public double Weight;
        public readonly HashSet<DateOnly> Days = new();
        public int ExtraDays;
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
        public readonly List<(DateTime Utc, string Name)> Examples = new();
        public readonly List<string> TotalsExamples = new();
        public readonly Dictionary<string, double> Sources = new(StringComparer.OrdinalIgnoreCase);
        public string? TotalsCommonSource;
        public readonly HashSet<long> Ids = new();
    }

    /// <summary>One feature value (case-insensitive) with its moves per destination folder.</summary>
    private sealed class ValueGroup
    {
        public required MoveFeature Feature { get; init; }
        /// <summary>The value as first seen (keeps the case of a name pattern for display).</summary>
        public required string Value { get; init; }
        public Dictionary<string, Bucket> ByDestination { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BucketMap
    {
        private readonly Dictionary<(MoveFeature, string), ValueGroup> _groups = new();
        public IEnumerable<ValueGroup> Groups => _groups.Values;

        public Bucket For(MoveFeature f, string value, string dest)
        {
            var key = (f, value.ToLowerInvariant());
            if (!_groups.TryGetValue(key, out var g)) _groups[key] = g = new ValueGroup { Feature = f, Value = value };
            if (!g.ByDestination.TryGetValue(dest, out var b)) g.ByDestination[dest] = b = new Bucket();
            return b;
        }
    }

    /// <summary>
    /// Folds raw moves into weighted totals (used when moves pass the 90-day retention). Mining the totals later gives
    /// the same weights, days and common folder as mining the raw moves did.
    /// </summary>
    public static IReadOnlyList<MoveTotals> Fold(IEnumerable<MoveEvent> moves)
    {
        var map = new BucketMap();
        AddRaw(map, moves);
        var result = new List<MoveTotals>();
        foreach (var g in map.Groups)
            foreach (var (dest, b) in g.ByDestination)
                result.Add(new MoveTotals(g.Feature, g.Value, dest, Math.Round(b.Weight, 4), b.Days.Count, b.First, b.Last,
                    string.Join("|", b.Examples.OrderByDescending(e => e.Utc).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3)),
                    CommonSource(g.Feature, b)));
        return result;
    }

    public static IReadOnlyList<RuleCandidate> Mine(IEnumerable<MoveEvent> moves, IEnumerable<MoveTotals>? totals = null)
    {
        var map = new BucketMap();
        AddRaw(map, moves);

        foreach (var t in totals ?? Enumerable.Empty<MoveTotals>())
        {
            var b = map.For(t.Feature, t.Value, t.Destination);
            b.Weight += t.Weight;
            b.ExtraDays += t.Days;
            if (t.FirstUtc < b.First) b.First = t.FirstUtc;
            if (t.LastUtc > b.Last) b.Last = t.LastUtc;
            b.TotalsExamples.AddRange(SplitExamples(t.Examples));
            b.TotalsCommonSource ??= t.CommonSource;
        }

        var candidates = new List<RuleCandidate>();
        foreach (var g in map.Groups)
        {
            var (feature, value) = (g.Feature, g.Value);
            double total = g.ByDestination.Values.Sum(b => b.Weight);
            var (dest, top) = g.ByDestination.OrderByDescending(kv => kv.Value.Weight).Select(kv => (kv.Key, kv.Value)).First();
            int days = top.Days.Count + top.ExtraDays;
            if (top.Weight + 1e-9 < LearningRules.MinRuleMoves) continue;
            if (top.Weight / total + 1e-9 < LearningRules.MinRuleConfidence) continue;
            if (days < LearningRules.MinRuleDays) continue;
            if (feature == MoveFeature.SourceFolder && value.Equals(dest, StringComparison.OrdinalIgnoreCase)) continue;

            var examples = top.Examples.OrderByDescending(e => e.Utc).Select(e => e.Name)
                .Concat(top.TotalsExamples).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            candidates.Add(new RuleCandidate(KeyOf(feature, value, dest), feature, value, dest,
                Math.Round(top.Weight, 2), Math.Round(total, 2), days, top.First, top.Last, examples,
                CommonSource(feature, top), top.Ids));
        }
        return Deduplicate(candidates);
    }

    private static void AddRaw(BucketMap map, IEnumerable<MoveEvent> moves)
    {
        foreach (var (feature, value, dest, sessionMoves) in Sessions(moves))
        {
            var b = map.For(feature, value, dest);
            double perMove = sessionMoves.Count > LearningRules.BulkSessionMoves ? 1.0 / sessionMoves.Count : 1.0;
            foreach (var m in sessionMoves)
            {
                b.Weight += perMove;
                b.Days.Add(DateOnly.FromDateTime(m.Utc));
                if (m.Utc < b.First) b.First = m.Utc;
                if (m.Utc > b.Last) b.Last = m.Utc;
                b.Examples.Add((m.Utc, Path.GetFileName(m.FinalPath)));
                var src = MoveFeatures.Normalize(m.SourceFolder);
                b.Sources[src] = b.Sources.GetValueOrDefault(src) + perMove;
                if (m.Id != 0) b.Ids.Add(m.Id);
            }
        }
    }

    public static string KeyOf(MoveFeature feature, string value, string destination)
        => $"rule:{feature}:{value}>{destination}".ToLowerInvariant();

    /// <summary>Moves grouped by feature value, destination and run: the unit that "a bulk session counts once" applies to.</summary>
    private static IEnumerable<(MoveFeature, string, string, List<MoveEvent>)> Sessions(IEnumerable<MoveEvent> moves)
    {
        var sessions = new Dictionary<(MoveFeature, string, string, string), (string Value, string Dest, List<MoveEvent> Moves)>();
        foreach (var m in moves)
        {
            var dest = MoveFeatures.Normalize(m.DestinationFolder);
            if (dest.Length == 0) continue;
            foreach (var (feature, value) in MoveFeatures.Of(m))
            {
                var key = (feature, value.ToLowerInvariant(), dest.ToLowerInvariant(), m.RunId);
                if (!sessions.TryGetValue(key, out var s)) sessions[key] = s = (value, dest, new List<MoveEvent>());
                s.Moves.Add(m);
            }
        }
        foreach (var ((feature, _, _, _), s) in sessions) yield return (feature, s.Value, s.Dest, s.Moves);
    }

    private static string? CommonSource(MoveFeature feature, Bucket b)
    {
        if (feature == MoveFeature.SourceFolder) return null;
        double raw = b.Sources.Values.Sum();
        if (raw <= 0) return b.TotalsCommonSource;
        var (folder, weight) = b.Sources.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();
        if (b.TotalsCommonSource is { } older && !older.Equals(folder, StringComparison.OrdinalIgnoreCase)) return null;
        return weight / raw + 1e-9 >= LearningRules.MinRuleConfidence ? folder : null;
    }

    /// <summary>
    /// One suggestion per set of moves: when two candidates send mostly the same moves to the same folder
    /// (".jpg files" and "files named IMG_*.jpg"), keep the simpler, better-supported one. Evidence that only exists as
    /// totals has no move ids: there a less simple candidate is a duplicate unless it explains more moves than the
    /// simpler ones already kept for that folder.
    /// </summary>
    private static IReadOnlyList<RuleCandidate> Deduplicate(List<RuleCandidate> candidates)
    {
        var kept = new List<RuleCandidate>();
        foreach (var c in candidates.OrderBy(c => c.Feature).ThenByDescending(c => c.Moves).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var sameFolder = kept.Where(k => k.Destination.Equals(c.Destination, StringComparison.OrdinalIgnoreCase)).ToList();
            bool duplicate = c.MoveIds.Count > 0
                ? sameFolder.Any(k => c.MoveIds.Count(k.MoveIds.Contains) * 2 >= c.MoveIds.Count)
                : sameFolder.Count > 0 && c.Moves <= sameFolder.Sum(k => k.Moves) + 1e-9;
            if (!duplicate) kept.Add(c);
        }
        return kept.OrderByDescending(c => c.Moves).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<string> SplitExamples(string? joined)
        => (joined ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
