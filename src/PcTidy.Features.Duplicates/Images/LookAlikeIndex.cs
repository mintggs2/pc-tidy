using System.Numerics;
using PcTidy.Features.Duplicates.Hashing;

namespace PcTidy.Features.Duplicates.Images;

/// <summary>
/// Finds every pair of pictures within the thresholds without comparing all pairs (multi-index hashing): the 64-bit pHash
/// is cut into four 16-bit parts. If two hashes differ in at most d bits, at least one part differs in at most d / 4 bits,
/// so each picture only needs to be checked against pictures whose part matches one of its own parts within that radius.
/// Exact: it finds the same pairs as comparing everything with everything, just far fewer comparisons.
/// </summary>
internal static class LookAlikeIndex
{
    private const int Parts = 4;

    /// <summary>For each picture, the others within both thresholds (symmetric lists).</summary>
    public static List<int>[] Neighbours(IReadOnlyList<ImageHashes> hashes, SimilarityThresholds t, int parallelism,
        Action<double>? progress, CancellationToken ct)
    {
        int n = hashes.Count;
        var result = new List<int>[n];
        for (int i = 0; i < n; i++) result[i] = new List<int>();
        if (n < 2) return result;

        // part index → 16-bit value → pictures with that value.
        var buckets = new Dictionary<ushort, List<int>>[Parts];
        for (int p = 0; p < Parts; p++)
        {
            buckets[p] = new Dictionary<ushort, List<int>>();
            for (int i = 0; i < n; i++)
            {
                var key = Part(hashes[i].PHash, p);
                if (!buckets[p].TryGetValue(key, out var list)) buckets[p][key] = list = new List<int>();
                list.Add(i);
            }
        }
        var masks = MasksUpTo(Math.Max(0, t.MaxPHashDistance) / Parts);

        var pairs = new List<(int A, int B)>[n];
        long done = 0;
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, parallelism) },
            () => new HashSet<int>(),
            (i, _, seen) =>
            {
                seen.Clear();
                var a = hashes[i];
                List<(int, int)>? found = null;
                for (int p = 0; p < Parts; p++)
                {
                    var key = Part(a.PHash, p);
                    foreach (var mask in masks)
                    {
                        if (!buckets[p].TryGetValue((ushort)(key ^ mask), out var list)) continue;
                        foreach (var j in list)
                        {
                            if (j <= i || !seen.Add(j)) continue;
                            var b = hashes[j];
                            if (ImageFingerprint.Distance(a.PHash, b.PHash) <= t.MaxPHashDistance
                                && ImageFingerprint.Distance(a.DHash, b.DHash) <= t.MaxDHashDistance)
                                (found ??= new List<(int, int)>()).Add((i, j));
                        }
                    }
                }
                if (found is not null) pairs[i] = found;
                var count = Interlocked.Increment(ref done);
                if (count % 256 == 0) progress?.Invoke((double)count / n);
                return seen;
            },
            _ => { });
        ct.ThrowIfCancellationRequested();

        foreach (var list in pairs)
        {
            if (list is null) continue;
            foreach (var (a, b) in list) { result[a].Add(b); result[b].Add(a); }
        }
        progress?.Invoke(1);
        return result;
    }

    private static ushort Part(ulong hash, int part) => (ushort)(hash >> (16 * part));

    /// <summary>Every 16-bit mask with at most <paramref name="bits"/> bits set (697 for 3 bits).</summary>
    internal static IReadOnlyList<ushort> MasksUpTo(int bits)
    {
        var masks = new List<ushort>();
        for (int m = 0; m <= ushort.MaxValue; m++)
            if (BitOperations.PopCount((uint)m) <= bits) masks.Add((ushort)m);
        return masks;
    }
}
