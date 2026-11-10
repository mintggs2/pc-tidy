using System.Collections.Concurrent;
using System.Globalization;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>One file's data on disk. Several paths = hard links to the same data.</summary>
public sealed record PhysicalFile(IReadOnlyList<FileRec> Paths, uint LinkCount)
{
    public FileRec First => Paths[0];
    /// <summary>The data has more names than the ones the scan saw (a hard link somewhere else).</summary>
    public bool HasLinksOutsideScan => LinkCount > Paths.Count;
}

/// <summary>Byte-identical files found by size → first bytes → full content (XXH3-64).</summary>
public sealed record RawDuplicateGroup(long Size, string FullHash, IReadOnlyList<PhysicalFile> Files);

/// <summary>
/// Huge files (over <see cref="DuplicateFinder.HugeFileBytes"/>) with the same size, first bytes and sampled hash
/// (<see cref="SampledHash"/>). Only PROBABLY the same: never removed until a full hash proves it.
/// </summary>
public sealed record RawProbableGroup(long Size, string SampleHash, IReadOnlyList<PhysicalFile> Files);

public sealed class FinderStats
{
    public long SizeCandidates;
    public long PartialHashed;
    public long FullHashed;
    public long CacheHits;
    public long Unreadable;
    public long HardLinkedPaths;
}

/// <summary>
/// Exact duplicate detection (Plan §8.3, Research §3): group by size, drop hard links (same file id: they free
/// nothing), hash the first 64 KB, then the whole file for groups that still match. Multi-threaded per drive,
/// cancellable, with a <see cref="HashCache"/>. Only reads; the caller has already removed online-only files.
/// </summary>
public sealed class DuplicateFinder
{
    public const int DefaultPartialBytes = 64 * 1024;
    /// <summary>Files bigger than this (2 GB) are compared by samples first; the full hash waits for the user's "Check fully".</summary>
    public const long DefaultHugeFileBytes = 2L * 1024 * 1024 * 1024;

    private readonly HashCache _cache;
    private readonly int _partialBytes;
    private readonly Func<string, int> _parallelismForDrive;

    /// <param name="parallelismForDrive">Readers per drive root (SSD: several, HDD: one or two so the head doesn't thrash).</param>
    /// <param name="hugeFileBytes">Files bigger than this get the sampled check instead of a full hash (unless one is cached).</param>
    public DuplicateFinder(HashCache cache, Func<string, int>? parallelismForDrive = null, long hugeFileBytes = DefaultHugeFileBytes)
    {
        _cache = cache;
        _partialBytes = cache.PartialBytes;
        _parallelismForDrive = parallelismForDrive ?? (_ => Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        HugeFileBytes = Math.Max(hugeFileBytes, _partialBytes);
    }

    public long HugeFileBytes { get; }

    public FinderStats Stats { get; } = new();

    /// <summary>Huge files that only matched by samples in the last <see cref="Find"/> (never counted as exact).</summary>
    public List<RawProbableGroup> ProbableGroups { get; } = new();

    /// <summary>Full hash of each file the last <see cref="Find"/> hashed (by path), for folder signatures.</summary>
    public ConcurrentDictionary<string, string> FullHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RawDuplicateGroup> Find(IEnumerable<FileRec> files, Action<string, double?>? progress, CancellationToken ct)
    {
        // 1. Same size (empty files are junk, not duplicates).
        var bySize = files.Where(f => f.Size > 0)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();
        Stats.SizeCandidates = bySize.Sum(g => (long)g.Count());

        // 2. Same file id = hard links of one file: one "physical" file with several names.
        progress?.Invoke("Checking for hard links…", null);
        var physical = new List<(long Size, PhysicalFile File)>();
        var ids = new ConcurrentDictionary<string, FileId?>(StringComparer.OrdinalIgnoreCase);
        RunPerDrive(bySize.SelectMany(g => g).ToList(), f => f.Path, f => ids[f.Path] = FileIdentity.TryGet(f.Path), ct);
        foreach (var group in bySize)
        {
            var collapsed = group
                .GroupBy(f => ids.TryGetValue(f.Path, out var id) && id is { } v ? $"{v.Volume:X}:{v.IdHigh:X}:{v.IdLow:X}" : "path:" + f.Path,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var paths = g.ToList();
                    var links = ids.TryGetValue(paths[0].Path, out var id) && id is { } v ? v.LinkCount : 1u;
                    if (paths.Count > 1) Interlocked.Add(ref Stats.HardLinkedPaths, paths.Count - 1);
                    return new PhysicalFile(paths, Math.Max(links, (uint)paths.Count));
                }).ToList();
            foreach (var p in collapsed) physical.Add((group.Key, p));
        }

        // Several names of ONE file are not duplicates; only sizes with two or more physical files go on.
        var candidates = physical.GroupBy(p => p.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToList();

        // 3. First bytes.
        progress?.Invoke("Comparing the start of each file…", 0);
        var partial = new ConcurrentDictionary<PhysicalFile, ulong>();
        long done = 0;
        RunPerDrive(candidates, c => c.File.First.Path, c =>
        {
            if (TryPartial(c.File.First, out var h)) partial[c.File] = h;
            var n = Interlocked.Increment(ref done);
            if (n % 256 == 0) progress?.Invoke("Comparing the start of each file…", (double)n / Math.Max(1, candidates.Count));
        }, ct);

        var byPartial = candidates.Where(c => partial.ContainsKey(c.File))
            .GroupBy(c => (c.Size, partial[c.File]))
            .Where(g => g.Count() > 1)
            .ToList();

        // 4. Whole content, only where the start matched. Files no bigger than the partial window are already fully hashed.
        //    Huge files get the sampled check instead; only those the user already checked fully (cached) count as exact.
        var matchedStart = byPartial.SelectMany(g => g).ToList();
        var needFull = matchedStart.Where(c => c.Size <= HugeFileBytes).ToList();
        needFull.AddRange(SampleHuge(matchedStart.Where(c => c.Size > HugeFileBytes).ToList(), progress, ct));
        progress?.Invoke("Comparing whole files…", 0);
        var full = new ConcurrentDictionary<PhysicalFile, ulong>();
        done = 0;
        RunPerDrive(needFull, c => c.File.First.Path, c =>
        {
            if (c.Size <= _partialBytes) full[c.File] = partial[c.File];
            else if (TryFull(c.File.First, out var h)) full[c.File] = h;
            var n = Interlocked.Increment(ref done);
            if (n % 64 == 0) progress?.Invoke("Comparing whole files…", (double)n / Math.Max(1, needFull.Count));
        }, ct);

        foreach (var (file, hash) in full)
            foreach (var p in file.Paths) FullHashes[p.Path] = Hex(hash);

        return needFull.Where(c => full.ContainsKey(c.File))
            .GroupBy(c => (c.Size, full[c.File]))
            .Where(g => g.Count() > 1)
            .Select(g => new RawDuplicateGroup(g.Key.Size, Hex(g.Key.Item2), g.Select(x => x.File).ToList()))
            .OrderByDescending(g => g.Size * (g.Files.Count - 1))
            .ToList();
    }

    /// <summary>
    /// Huge files: same size and first bytes, then the sampled hash (start, middle, end). Sample groups whose every
    /// member was already hashed fully (the user pressed "Check fully" before; the cache remembers) go back as
    /// candidates for the exact list; the rest become <see cref="ProbableGroups"/>, which are never removed as they are.
    /// </summary>
    private IEnumerable<(long Size, PhysicalFile File)> SampleHuge(IReadOnlyList<(long Size, PhysicalFile File)> huge, Action<string, double?>? progress, CancellationToken ct)
    {
        ProbableGroups.Clear();
        if (huge.Count == 0) return Array.Empty<(long, PhysicalFile)>();
        progress?.Invoke("Sampling very large files…", null);
        var sampled = new ConcurrentDictionary<PhysicalFile, string>();
        RunPerDrive(huge, c => c.File.First.Path, c =>
        {
            try { sampled[c.File] = SampledHash.Compute(Win32Path.Raw(c.File.First.Path), c.Size); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Interlocked.Increment(ref Stats.Unreadable);
            }
        }, ct);

        var fullyKnown = new List<(long, PhysicalFile)>();
        foreach (var g in huge.Where(c => sampled.ContainsKey(c.File)).GroupBy(c => (c.Size, sampled[c.File])).Where(g => g.Count() > 1))
        {
            var members = g.ToList();
            if (members.All(c => _cache.TryGetFull(c.File.First.Path, c.Size, c.File.First.WriteUtc, out _))) fullyKnown.AddRange(members);
            else ProbableGroups.Add(new RawProbableGroup(g.Key.Size, g.Key.Item2, members.Select(c => c.File).ToList()));
        }
        return fullyKnown;
    }

    public static string Hex(ulong hash) => hash.ToString("X16", CultureInfo.InvariantCulture);

    public static ulong ParseHex(string hex) => ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private bool TryPartial(FileRec f, out ulong hash)
    {
        if (_cache.TryGetPartial(f.Path, f.Size, f.WriteUtc, out hash)) { Interlocked.Increment(ref Stats.CacheHits); return true; }
        try
        {
            hash = ParseHex(FileHash.Partial(Win32Path.Raw(f.Path), _partialBytes));
            _cache.PutPartial(f.Path, f.Size, f.WriteUtc, hash);
            Interlocked.Increment(ref Stats.PartialHashed);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Interlocked.Increment(ref Stats.Unreadable);
            return false;
        }
    }

    private bool TryFull(FileRec f, out ulong hash)
    {
        if (_cache.TryGetFull(f.Path, f.Size, f.WriteUtc, out hash)) { Interlocked.Increment(ref Stats.CacheHits); return true; }
        try
        {
            hash = ParseHex(FileHash.Full(Win32Path.Raw(f.Path)));
            _cache.PutFull(f.Path, f.Size, f.WriteUtc, hash);
            Interlocked.Increment(ref Stats.FullHashed);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Interlocked.Increment(ref Stats.Unreadable);
            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> for every item: drives side by side, a few readers per drive.</summary>
    private void RunPerDrive<T>(IReadOnlyList<T> items, Func<T, string> pathOf, Action<T> work, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var perDrive = items.GroupBy(i => Path.GetPathRoot(pathOf(i)) ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = perDrive.Select(g => Task.Run(() =>
        {
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, _parallelismForDrive(g.Key)) };
            Parallel.ForEach(g, options, item => work(item));
        }, ct)).ToArray();
        try { Task.WaitAll(tasks, ct); }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException or TaskCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
    }
}
