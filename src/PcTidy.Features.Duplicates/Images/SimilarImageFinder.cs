using System.Collections.Concurrent;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Images;

/// <summary>
/// How close two fingerprints must be (bits out of 64). pHash decides; dHash is a loose second opinion, because
/// resizing moves dHash bits more than pHash bits. Deep is more sensitive: it finds looser matches.
/// </summary>
public sealed record SimilarityThresholds(int MaxPHashDistance, int MaxDHashDistance)
{
    public static readonly SimilarityThresholds Quick = new(8, 22);
    public static readonly SimilarityThresholds Deep = new(12, 26);
    public static SimilarityThresholds For(ScanDepth depth) => depth == ScanDepth.Deep ? Deep : Quick;
}

/// <summary>A cluster of look-alike pictures with each one's fingerprint.</summary>
public sealed record RawSimilarGroup(IReadOnlyList<(FileRec File, ImageHashes Hashes)> Members);

/// <summary>
/// Finds look-alike pictures: fingerprints every picture (cached), finds the pairs within the thresholds through an
/// index (<see cref="LookAlikeIndex"/>, no all-pairs loop), then forms star-shaped groups around the best picture
/// (never chains). Byte-identical copies are collapsed first: they belong to exact duplicates, not here.
/// </summary>
public sealed class SimilarImageFinder
{
    /// <summary>Bigger files are skipped: they're not everyday pictures, and decoding them would be slow.</summary>
    private const long MaxFileBytes = 200L * 1024 * 1024;

    private readonly HashCache _cache;
    private readonly int _parallelism;

    public SimilarImageFinder(HashCache cache, int parallelism = 4)
    {
        _cache = cache;
        _parallelism = Math.Max(1, parallelism);
    }

    public long Hashed;
    public long CacheHits;
    public long Undecodable;

    public static bool IsCandidate(in FileRec f)
        => f.Size >= 512 && f.Size <= MaxFileBytes && ImageFingerprint.Extensions.Contains(Path.GetExtension(f.Name));

    /// <param name="contentKey">Same key = byte-identical (from exact duplicate hashing); such copies count once.</param>
    public IReadOnlyList<RawSimilarGroup> Find(IReadOnlyList<FileRec> pictures, SimilarityThresholds thresholds,
        Func<FileRec, string> contentKey, Action<double>? progress, CancellationToken ct)
    {
        var prints = new ConcurrentBag<(FileRec File, ImageHashes Hashes)>();
        long done = 0;
        Parallel.ForEach(pictures, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _parallelism }, f =>
        {
            if (Fingerprint(f) is { } h) prints.Add((f, h));
            var n = Interlocked.Increment(ref done);
            if (n % 32 == 0) progress?.Invoke(FingerprintShare * n / Math.Max(1, pictures.Count));
        });
        ct.ThrowIfCancellationRequested();

        // One representative per identical content: exact copies are reported as exact duplicates instead.
        var reps = prints.GroupBy(p => contentKey(p.File), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var neighbours = LookAlikeIndex.Neighbours(reps.Select(r => r.Hashes).ToList(), thresholds, _parallelism,
            frac => progress?.Invoke(FingerprintShare + (1 - FingerprintShare) * frac), ct);
        return StarGroups(reps, neighbours, ct);
    }

    /// <summary>Share of the progress bar for fingerprinting; the rest is for comparing.</summary>
    private const double FingerprintShare = 0.75;

    /// <summary>The most pictures one look-alike group can hold (the closest ones are kept; the rest may form their own group).</summary>
    public const int MaxGroupSize = 50;

    /// <summary>
    /// Groups are stars, never chains: the best picture still free (most pixels, then biggest file) becomes a centre, and
    /// only pictures within the thresholds OF THAT CENTRE join it (closest first, at most <see cref="MaxGroupSize"/>).
    /// So A≈B and B≈C never put A and C together unless both look like the same centre.
    /// </summary>
    private static IReadOnlyList<RawSimilarGroup> StarGroups(List<(FileRec File, ImageHashes Hashes)> reps, List<int>[] neighbours, CancellationToken ct)
    {
        var order = Enumerable.Range(0, reps.Count)
            .Where(i => neighbours[i].Count > 0)
            .OrderByDescending(i => (long)reps[i].Hashes.Width * reps[i].Hashes.Height)
            .ThenByDescending(i => reps[i].File.Size)
            .ThenBy(i => reps[i].File.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var taken = new bool[reps.Count];
        var groups = new List<RawSimilarGroup>();
        foreach (var centre in order)
        {
            ct.ThrowIfCancellationRequested();
            if (taken[centre]) continue;
            var c = reps[centre].Hashes;
            var members = neighbours[centre].Where(j => !taken[j])
                .OrderBy(j => ImageFingerprint.Distance(c.PHash, reps[j].Hashes.PHash))
                .ThenBy(j => reps[j].File.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxGroupSize - 1)
                .ToList();
            if (members.Count == 0) continue;
            taken[centre] = true;
            foreach (var j in members) taken[j] = true;
            groups.Add(new RawSimilarGroup(members.Prepend(centre).Select(i => reps[i]).ToList()));
        }
        return groups;
    }

    private ImageHashes? Fingerprint(FileRec f)
    {
        if (_cache.TryGetImage(f.Path, f.Size, f.WriteUtc, out var cached, out var notAnImage))
        {
            Interlocked.Increment(ref CacheHits);
            return notAnImage ? null : cached;
        }
        var h = ImageFingerprint.TryCompute(f.Path);
        if (h is { } v)
        {
            _cache.PutImage(f.Path, f.Size, f.WriteUtc, v);
            Interlocked.Increment(ref Hashed);
        }
        else
        {
            _cache.PutNotAnImage(f.Path, f.Size, f.WriteUtc);
            Interlocked.Increment(ref Undecodable);
        }
        return h;
    }
}
