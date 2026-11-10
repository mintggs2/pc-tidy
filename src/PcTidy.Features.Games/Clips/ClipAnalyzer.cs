using PcTidy.Core;

namespace PcTidy.Features.Games.Clips;

/// <summary>One clip or screenshot found in a capture folder.</summary>
public sealed record ClipFile(string Path, long Size, DateTime CreationUtc, DateTime LastWriteUtc, bool IsPlaceholder, ClipSource Source)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsVideo => ClipSources.VideoExtensions.Contains(System.IO.Path.GetExtension(Path));
    public DateTime LastTouchedUtc => LastWriteUtc > CreationUtc ? LastWriteUtc : CreationUtc;
}

/// <summary>A copy to remove and the copy that stays.</summary>
/// <param name="ContentHash">Exact copies: the full XXH3 both files had at scan time (<c>FileHash.Full</c>), so the engine can
/// re-check both right before moving the copy. Null for near-duplicates (they aren't the same bytes).</param>
public sealed record DuplicateMatch(ClipFile Copy, ClipFile Keeper, MediaInfo? Media = null, string? ContentHash = null);

/// <summary>
/// Finds exact duplicates (same size, then same XXH3 of the whole file) and near-duplicates (videos with the same
/// resolution, length within ±1 s, and matching shell thumbnails). Read-only. OneDrive online-only files are never
/// opened: they are skipped for both checks.
/// </summary>
internal static class ClipAnalyzer
{
    /// <summary>Thumbnail hashes this close (of 64 bits) count as "the same picture".</summary>
    public const int NearDuplicateMaxDistance = 10;

    public static IReadOnlyList<DuplicateMatch> ExactDuplicates(IEnumerable<ClipFile> files, CancellationToken ct)
    {
        var result = new List<DuplicateMatch>();
        foreach (var sameSize in files.Where(f => !f.IsPlaceholder && f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            foreach (var (_, samePrefix) in GroupByHash(sameSize, f => FileHash.Partial(f.Path), ct).Where(g => g.Files.Count > 1))
                foreach (var (fullHash, sameBytes) in GroupByHash(samePrefix, f => FileHash.Full(f.Path, ct), ct).Where(g => g.Files.Count > 1))
                {
                    // Keep the original: oldest created, then the shortest path.
                    var keeper = sameBytes.OrderBy(f => f.CreationUtc).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
                    result.AddRange(sameBytes.Where(f => !ReferenceEquals(f, keeper)).Select(f => new DuplicateMatch(f, keeper, ContentHash: fullHash)));
                }
        }
        return result;
    }

    public static IReadOnlyList<DuplicateMatch> NearDuplicates(IEnumerable<ClipFile> files, IMediaProbe probe, CancellationToken ct)
    {
        var probed = new List<(ClipFile File, MediaInfo Info)>();
        foreach (var f in files.Where(f => f.IsVideo && !f.IsPlaceholder))
        {
            ct.ThrowIfCancellationRequested();
            if (probe.Probe(f.Path) is { } info) probed.Add((f, info));
        }

        var result = new List<DuplicateMatch>();
        foreach (var sameSize in probed.GroupBy(p => (p.Info.Width, p.Info.Height)))
        {
            foreach (var cluster in DurationClusters(sameSize.OrderBy(p => p.Info.Duration).ToList()))
            {
                if (cluster.Count < 2) continue;
                // Keep the best copy: biggest file at the same length means the highest bitrate; then the oldest.
                var keeper = cluster.OrderByDescending(p => p.File.Size).ThenBy(p => p.File.CreationUtc).First();
                var keeperHash = Thumbnail(probe, keeper.File);
                if (keeperHash is null) continue;
                foreach (var other in cluster.Where(p => !ReferenceEquals(p.File, keeper.File)))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Math.Abs((other.Info.Duration - keeper.Info.Duration).TotalSeconds) > 1) continue;
                    if (Thumbnail(probe, other.File) is { } h && DHash.Distance(h, keeperHash.Value) <= NearDuplicateMaxDistance)
                        result.Add(new DuplicateMatch(other.File, keeper.File, other.Info));
                }
            }
        }
        return result;
    }

    /// <summary>Thumbnail from Windows' cache first; for files fully on this PC the shell may make one (no download ever).</summary>
    private static ulong? Thumbnail(IMediaProbe probe, ClipFile f)
        => probe.ThumbnailHash(f.Path, cacheOnly: true) ?? (f.IsPlaceholder ? null : probe.ThumbnailHash(f.Path, cacheOnly: false));

    /// <summary>Runs of clips (sorted by length) where each is within 1 s of the previous one.</summary>
    private static IEnumerable<List<(ClipFile File, MediaInfo Info)>> DurationClusters(List<(ClipFile File, MediaInfo Info)> sorted)
    {
        var current = new List<(ClipFile, MediaInfo)>();
        foreach (var p in sorted)
        {
            if (current.Count > 0 && (p.Info.Duration - current[^1].Item2.Duration).TotalSeconds > 1)
            {
                yield return current;
                current = new();
            }
            current.Add(p);
        }
        if (current.Count > 0) yield return current;
    }

    private static IEnumerable<(string Hash, List<ClipFile> Files)> GroupByHash(IEnumerable<ClipFile> files, Func<ClipFile, string> hash, CancellationToken ct)
    {
        var map = new Dictionary<string, List<ClipFile>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string h;
            try { h = hash(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // locked or vanished: not offered
            if (!map.TryGetValue(h, out var list)) map[h] = list = new List<ClipFile>();
            list.Add(f);
        }
        return map.Select(kv => (kv.Key, kv.Value));
    }
}
