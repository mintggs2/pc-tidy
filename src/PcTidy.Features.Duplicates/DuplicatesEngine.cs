using System.Collections.Concurrent;
using System.Diagnostics;
using PcTidy.Features.Duplicates.Folders;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Images;
using PcTidy.Features.Duplicates.Junk;
using PcTidy.Features.Duplicates.Keep;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.Features.Duplicates.Scanning;
using PcTidy.Features.Duplicates.Signatures;

namespace PcTidy.Features.Duplicates;

/// <summary>What a scan looks for. Defaults follow the scan depth.</summary>
public sealed record DuplicateScanOptions
{
    public bool FindExact { get; init; } = true;
    public bool FindFolders { get; init; } = true;
    public bool FindSimilarImages { get; init; } = true;
    public bool FindJunk { get; init; } = true;
    /// <summary>Smallest file compared for duplicates (bytes).</summary>
    public long MinFileSize { get; init; } = 1;
    /// <summary>Compare hidden files too. Null = only in Deep scans.</summary>
    public bool? IncludeHiddenFiles { get; init; }
    /// <summary>Null = <see cref="SimilarityThresholds.For"/> the depth.</summary>
    public SimilarityThresholds? Similarity { get; init; }
    /// <summary>"Now" for the junk age rules (tests pin it).</summary>
    public DateTime? NowUtc { get; init; }
    /// <summary>Files bigger than this only get the sampled check (probable duplicates) until the user asks for a full one.</summary>
    public long HugeFileBytes { get; init; } = DuplicateFinder.DefaultHugeFileBytes;
    /// <summary>Look for files whose content doesn't match their extension (reads only the first bytes).</summary>
    public bool FindWrongExtensions { get; init; } = true;
}

/// <summary>
/// Duplicates and junk (Plan §8.3), read-only from start to finish: walks the scan roots (never into protected,
/// project or sync-version folders, never through links, never opening online-only files), finds exact duplicates
/// (size → first bytes → full hash, hard links set aside), duplicate folders, look-alike pictures, empty folders,
/// zero-byte files and broken shortcuts, asks the safety guard about every path it would offer to remove, and
/// applies the keep rules. Nothing changes until the user confirms the resulting plan.
/// </summary>
public sealed class DuplicatesEngine
{
    private readonly HashCache _cache;

    public DuplicatesEngine(HashCache? cache = null) => _cache = cache ?? new HashCache();

    public HashCache Cache => _cache;

    public Task<DuplicatesReport> ScanAsync(ScanContext ctx, DuplicateScanOptions? options = null, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Scan(ctx, options ?? new DuplicateScanOptions(), progress, ct), ct);

    private DuplicatesReport Scan(ScanContext ctx, DuplicateScanOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var settings = DuplicatesSettings.From(ctx.Settings);
        var scope = new ScanScope(ctx, settings);
        var verdicts = new GuardCache(ctx.Safety);

        // 1. Walk (roots side by side).
        progress?.Report(new ScanProgress("Looking through your folders…", 0));
        long seen = 0;
        var inventories = WalkAll(scope, ctx.Walker, _ => progress?.Report(new ScanProgress($"Looking through your folders… {Interlocked.Add(ref seen, InventoryBuilder.ProgressStep):N0} files", 0.05)), ct);
        var files = inventories.SelectMany(i => i.Files).ToList();
        bool hidden = o.IncludeHiddenFiles ?? ctx.Depth == ScanDepth.Deep;

        // 2. Exact duplicates.
        var finder = new DuplicateFinder(_cache, DriveParallelism(ctx), o.HugeFileBytes);
        IReadOnlyList<RawDuplicateGroup> raw = Array.Empty<RawDuplicateGroup>();
        if (o.FindExact || o.FindFolders)
        {
            var candidates = files.Where(f => f.Size >= Math.Max(1, o.MinFileSize) && !f.IsSystem && (hidden || !f.IsHidden));
            raw = finder.Find(candidates, (msg, frac) => progress?.Report(new ScanProgress(msg, 0.3 + 0.4 * (frac ?? 0))), ct);
        }

        // 3. Duplicate folders (outermost only); file groups fully inside them are covered by the folder group.
        var folderGroups = new List<DuplicateGroup>();
        if (o.FindFolders)
        {
            progress?.Report(new ScanProgress("Looking for duplicate folders…", 0.72));
            foreach (var g in DuplicateFolderFinder.Find(inventories, finder.FullHashes, ct))
                if (BuildFolderGroup(g, scope, verdicts) is { } built) folderGroups.Add(built);
        }
        var folderMembers = new FolderSet(folderGroups.SelectMany(g => g.Copies.Select(c => c.Path)));
        var exactGroups = new List<DuplicateGroup>();
        if (o.FindExact)
        {
            foreach (var g in raw)
            {
                ct.ThrowIfCancellationRequested();
                if (folderMembers.Count > 0 && g.Files.SelectMany(f => f.Paths).All(p => folderMembers.ContainsAncestorOf(p.Path))) continue;
                if (BuildFileGroup(g, scope, verdicts) is { } built) exactGroups.Add(built);
            }
        }
        // Huge files that only matched by samples: shown, never ticked, never removed before "Check fully".
        var probableGroups = new List<DuplicateGroup>();
        if (o.FindExact)
            foreach (var g in finder.ProbableGroups)
                if (BuildFileGroup(new RawDuplicateGroup(g.Size, g.SampleHash, g.Files), scope, verdicts) is { } built)
                    probableGroups.Add(built with { Id = "probable-" + g.SampleHash + "-" + g.Size, IsProbable = true, AutoTicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) });

        // Files whose content isn't what the extension says (first bytes only).
        IReadOnlyList<WrongExtensionItem> wrongExtensions = Array.Empty<WrongExtensionItem>();
        if (o.FindWrongExtensions)
        {
            progress?.Report(new ScanProgress("Checking file types…", 0.725));
            wrongExtensions = WrongExtensionFinder.Find(files.Where(f => hidden || !f.IsHidden), scope.FriendlyPath, ctx.Safety, ct);
        }

        // 4. Look-alike pictures (never ticked).
        var similar = new List<SimilarImageGroup>();
        var imageFinder = new SimilarImageFinder(_cache, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        if (o.FindSimilarImages)
        {
            progress?.Report(new ScanProgress("Comparing pictures…", 0.74));
            var pictures = files.Where(f => SimilarImageFinder.IsCandidate(f) && !f.IsSystem && (hidden || !f.IsHidden)).ToList();
            var thresholds = o.Similarity ?? SimilarityThresholds.For(ctx.Depth);
            string ContentKey(FileRec f) => finder.FullHashes.TryGetValue(f.Path, out var h) ? $"{f.Size}:{h}" : "path:" + f.Path;
            foreach (var g in imageFinder.Find(pictures, thresholds, ContentKey, frac => progress?.Report(new ScanProgress("Comparing pictures…", 0.74 + 0.18 * frac)), ct))
                if (BuildSimilarGroup(g, thresholds, scope, verdicts) is { } built) similar.Add(built);
        }

        // 5. Junk.
        var empty = new List<JunkItem>();
        var zero = new List<JunkItem>();
        var shortcuts = new List<JunkItem>();
        var junk = new JunkFinder(o.NowUtc ?? DateTime.UtcNow);
        if (o.FindJunk)
        {
            progress?.Report(new ScanProgress("Looking for empty folders and broken shortcuts…", 0.93));
            foreach (var inv in inventories)
            {
                empty.AddRange(Actionable(junk.EmptyFolders(inv, ct), verdicts));
                zero.AddRange(Actionable(junk.ZeroByteFiles(inv), verdicts));
                shortcuts.AddRange(Actionable(junk.BrokenShortcuts(inv, ct), verdicts));
            }
        }

        var stats = new DuplicateScanStats
        {
            FilesSeen = inventories.Sum(i => i.FilesSeen),
            FoldersSeen = inventories.Sum(i => (long)i.Dirs.Count),
            BytesSeen = inventories.Sum(i => i.BytesSeen),
            SizeCandidates = finder.Stats.SizeCandidates,
            PartialHashed = finder.Stats.PartialHashed,
            FullHashed = finder.Stats.FullHashed,
            CacheHits = finder.Stats.CacheHits + imageFinder.CacheHits,
            ImagesHashed = imageFinder.Hashed,
            PlaceholdersSkipped = inventories.Sum(i => i.Placeholders),
            LinksSkipped = inventories.Sum(i => i.Links),
            ProtectedFoldersSkipped = inventories.Sum(i => i.ProtectedSkipped),
            ProjectFoldersSkipped = inventories.Sum(i => i.ProjectsSkipped),
            Unreadable = inventories.Sum(i => i.Unreadable) + finder.Stats.Unreadable,
            HardLinks = finder.Stats.HardLinkedPaths + exactGroups.Sum(g => g.Copies.Count(c => c.IsHardLink && c.HardLinkOf == "")),
            Elapsed = sw.Elapsed,
        };
        progress?.Report(new ScanProgress("Done", 1, stats.FilesSeen, stats.BytesSeen));
        return new DuplicatesReport
        {
            Depth = ctx.Depth,
            Roots = scope.Roots,
            ExactGroups = exactGroups.OrderByDescending(g => g.ReclaimableBytes).ThenBy(g => g.KeptPath, StringComparer.OrdinalIgnoreCase).ToList(),
            FolderGroups = folderGroups.OrderByDescending(g => g.ReclaimableBytes).ToList(),
            SimilarImages = similar,
            EmptyFolders = empty,
            ZeroByteFiles = zero,
            BrokenShortcuts = shortcuts,
            ProbableGroups = probableGroups.OrderByDescending(g => g.Size * (g.Copies.Count - 1)).ToList(),
            WrongExtensions = wrongExtensions,
            Notes = Notes(stats, junk),
            Stats = stats,
            FriendlyPath = scope.FriendlyPath,
        };
    }

    /// <summary>
    /// "Compare to master": files in <paramref name="otherFolder"/> whose content is already somewhere in
    /// <paramref name="masterFolder"/>. The master is never changed; matches are offered for quarantine, unticked.
    /// </summary>
    public Task<MasterComparison> CompareToMasterAsync(ScanContext ctx, string masterFolder, string otherFolder,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => CompareToMaster(ctx, masterFolder, otherFolder, progress, ct), ct);

    private MasterComparison CompareToMaster(ScanContext ctx, string masterFolder, string otherFolder, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var master = PathUtil.Normalize(masterFolder);
        var other = PathUtil.Normalize(otherFolder);
        if (PathUtil.IsUnder(master, other) || PathUtil.IsUnder(other, master))
            return new MasterComparison
            {
                MasterFolder = master, OtherFolder = other, Matches = Array.Empty<MasterMatch>(), OnlyInOther = 0,
                Notes = new[] { "Pick two separate folders: one of them is inside the other." },
            };
        var settings = DuplicatesSettings.From(ctx.Settings);
        // The master acts as a reference folder for this comparison: nothing in it is ever offered.
        settings.ReferenceFolders = settings.ReferenceFolders.Append(master).ToList();
        var scopeCtx = new ScanContext
        {
            Locations = ctx.Locations, Safety = ctx.Safety, InUse = ctx.InUse, Settings = ctx.Settings, Walker = ctx.Walker,
            Depth = ScanDepth.Deep, Roots = new[] { master, other }, System = ctx.System,
        };
        var scope = new ScanScope(scopeCtx, settings);
        var verdicts = new GuardCache(ctx.Safety);
        progress?.Report(new ScanProgress("Listing both folders…", 0));
        var inventories = WalkAll(scope, ctx.Walker, null, ct);
        var masterFiles = inventories.Where(i => PathUtil.IsUnder(i.Root, master)).SelectMany(i => i.Files).Where(f => !f.IsSystem).ToList();
        var otherFiles = inventories.Where(i => PathUtil.IsUnder(i.Root, other)).SelectMany(i => i.Files).Where(f => !f.IsSystem && f.Size > 0).ToList();
        var masterSizes = masterFiles.Select(f => f.Size).ToHashSet();
        var otherSizes = otherFiles.Select(f => f.Size).ToHashSet();
        var relevant = masterFiles.Where(f => otherSizes.Contains(f.Size)).Concat(otherFiles.Where(f => masterSizes.Contains(f.Size)));
        var finder = new DuplicateFinder(_cache, DriveParallelism(ctx));
        var groups = finder.Find(relevant.Where(f => f.Size > 0), (msg, frac) => progress?.Report(new ScanProgress(msg, 0.2 + 0.8 * (frac ?? 0))), ct);

        var matches = new List<MasterMatch>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var inMaster = g.Files.SelectMany(f => f.Paths).FirstOrDefault(p => PathUtil.IsUnder(p.Path, master));
            if (inMaster.Path is null) continue;
            foreach (var physical in g.Files)
                foreach (var p in physical.Paths.Where(p => PathUtil.IsUnder(p.Path, other)))
                {
                    var copy = MakeCopy(p.Path, p.Size, p.WriteUtc, scope, verdicts, isDirectory: false) with
                    {
                        HardLinkOf = physical.Paths.Any(x => PathUtil.IsUnder(x.Path, master)) ? inMaster.Path : physical.HasLinksOutsideScan ? "" : null,
                    };
                    matches.Add(new MasterMatch(copy, inMaster.Path, g.FullHash));
                    matched.Add(p.Path);
                }
        }
        return new MasterComparison
        {
            MasterFolder = master, OtherFolder = other,
            Matches = matches.OrderBy(m => m.Copy.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            OnlyInOther = otherFiles.Count(f => !matched.Contains(f.Path)),
            Notes = inventories.Sum(i => i.Placeholders) is > 0 and var ph
                ? new[] { $"{ph:N0} online-only file{(ph == 1 ? " was" : "s were")} skipped. PC Tidy never downloads files to compare them." }
                : Array.Empty<string>(),
        };
    }

    // ------------------------------------------------------------------ walking

    private static IReadOnlyList<RootInventory> WalkAll(ScanScope scope, IFileWalker walker, Action<long>? onProgress, CancellationToken ct)
    {
        var results = new RootInventory[scope.Roots.Count];
        Parallel.For(0, scope.Roots.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            i => results[i] = InventoryBuilder.Walk(scope.Roots[i], scope, walker, onProgress, ct));
        return results;
    }

    private static Func<string, int> DriveParallelism(ScanContext ctx)
    {
        var drives = ctx.System?.Drives ?? Array.Empty<DriveDetails>();
        return root =>
        {
            var d = drives.FirstOrDefault(x => PathUtil.IsUnder(root, x.Root) || string.Equals(Path.GetPathRoot(x.Root), root, StringComparison.OrdinalIgnoreCase));
            // A spinning disk reads fastest one file at a time; SSDs like a few at once.
            return d?.Media == DriveMedia.Hdd ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        };
    }

    // ------------------------------------------------------------------ building groups

    private DuplicateGroup? BuildFileGroup(RawDuplicateGroup g, ScanScope scope, GuardCache verdicts)
    {
        var copies = new List<DuplicateCopy>();
        foreach (var physical in g.Files)
        {
            // Of several names of the same data, the best-ranked one stands for the data; the rest are hard links.
            var names = KeepRules.Rank(physical.Paths.Select(p => MakeCopy(p.Path, p.Size, p.WriteUtc, scope, verdicts, false))).ToList();
            copies.Add(physical.HasLinksOutsideScan ? names[0] with { HardLinkOf = "" } : names[0]);
            copies.AddRange(names.Skip(1).Select(n => n with { HardLinkOf = names[0].Path }));
        }
        if (copies.All(c => c.InReferenceFolder || c.BlockedForGood)) return null;
        return KeepRules.Decide("dup-" + g.FullHash + "-" + g.Size, g.FullHash, g.Size, copies);
    }

    private static DuplicateGroup? BuildFolderGroup(RawFolderGroup g, ScanScope scope, GuardCache verdicts)
    {
        var copies = g.Folders.Select(f => MakeCopy(f.Path, f.Bytes, f.WriteUtc, scope, verdicts, true)).ToList();
        if (copies.All(c => c.InReferenceFolder || c.BlockedForGood)) return null;
        return KeepRules.Decide("folder-" + g.Signature, g.Signature, g.Folders[0].Bytes, copies, isFolderGroup: true, fileCount: g.Folders[0].Files);
    }

    private static SimilarImageGroup? BuildSimilarGroup(RawSimilarGroup g, SimilarityThresholds thresholds, ScanScope scope, GuardCache verdicts)
    {
        // Suggested keep: the most pixels, then the biggest file, then the usual keep rules.
        var ranked = g.Members
            .Select(m => (m.File, m.Hashes, Copy: MakeCopy(m.File.Path, m.File.Size, m.File.WriteUtc, scope, verdicts, false)))
            .OrderByDescending(m => m.Copy.InReferenceFolder)
            .ThenByDescending(m => (long)m.Hashes.Width * m.Hashes.Height)
            .ThenByDescending(m => m.File.Size)
            .ThenBy(m => m.Copy.Location).ThenBy(m => m.Copy.HasCopyName).ThenBy(m => m.File.WriteUtc).ThenBy(m => m.File.Path.Length)
            .ToList();
        // Every picture shown must look like the one kept (a reference-folder keeper may not be the group's centre).
        var k0 = ranked[0].Hashes;
        ranked = ranked.Where((m, i) => i == 0 || (ImageFingerprint.Distance(m.Hashes.PHash, k0.PHash) <= thresholds.MaxPHashDistance
                                                   && ImageFingerprint.Distance(m.Hashes.DHash, k0.DHash) <= thresholds.MaxDHashDistance)).ToList();
        if (ranked.Count < 2 || ranked.All(m => m.Copy.InReferenceFolder || m.Copy.BlockedForGood)) return null;
        var keep = ranked[0];
        var keepReason = keep.Copy.InReferenceFolder ? "It's in a reference folder you marked."
            : ranked.Count > 1 && (long)keep.Hashes.Width * keep.Hashes.Height > (long)ranked[1].Hashes.Width * ranked[1].Hashes.Height
                ? $"It's the sharpest version ({keep.Hashes.Width} × {keep.Hashes.Height})."
                : ranked.Count > 1 && keep.File.Size > ranked[1].File.Size ? "It's the best-quality version (the biggest file)."
                : KeepRules.Explain(keep.Copy, ranked.Count > 1 ? ranked[1].Copy : null);
        return new SimilarImageGroup
        {
            Id = "similar-" + DuplicateFinder.Hex(keep.Hashes.PHash) + "-" + keep.File.Path.GetHashCode().ToString("X8"),
            SuggestedKeep = keep.File.Path,
            KeepReason = keepReason,
            Images = ranked.Select(m => new SimilarImage
            {
                Path = m.File.Path, Size = m.File.Size, LastWriteUtc = m.File.WriteUtc, Width = m.Hashes.Width, Height = m.Hashes.Height,
                SimilarityPercent = (int)Math.Round(100.0 * (64 - ImageFingerprint.Distance(m.Hashes.PHash, keep.Hashes.PHash)) / 64),
                RemovalBlockedReason = m.Copy.RemovalBlockedReason, InReferenceFolder = m.Copy.InReferenceFolder,
            }).ToList(),
        };
    }

    private static DuplicateCopy MakeCopy(string path, long size, DateTime writeUtc, ScanScope scope, GuardCache verdicts, bool isDirectory)
    {
        var location = scope.Classify(path, out var label);
        var verdict = verdicts.Check(path, isDirectory, size, writeUtc);
        return new DuplicateCopy
        {
            Path = path, Size = size, LastWriteUtc = writeUtc, Location = location, LocationLabel = label,
            InReferenceFolder = scope.IsReference(path),
            HasCopyName = ScanScope.HasCopyName(path),
            RemovalBlockedReason = verdict.Allowed ? null : verdict.Reason ?? "PC Tidy's safety rules keep this file where it is.",
            BlockedForGood = !verdict.Allowed && verdict.Protection == PathProtection.Never,
        };
    }

    /// <summary>Drops junk the guard will never allow; keeps "ask first" ones, shown greyed out with the reason.</summary>
    private static IEnumerable<JunkItem> Actionable(IEnumerable<JunkItem> items, GuardCache verdicts)
    {
        foreach (var j in items)
        {
            var v = verdicts.Check(j.Path, j.IsDirectory, j.Size, j.LastWriteUtc);
            if (v.Allowed) yield return j;
            else if (v.Protection != PathProtection.Never) yield return j with { RemovalBlockedReason = v.Reason };
        }
    }

    private static IReadOnlyList<string> Notes(DuplicateScanStats s, JunkFinder junk)
    {
        var notes = new List<string>();
        static string Plural(long n, string one, string many) => n == 1 ? one : many;
        if (s.PlaceholdersSkipped > 0)
            notes.Add($"{s.PlaceholdersSkipped:N0} online-only cloud file{Plural(s.PlaceholdersSkipped, " was", "s were")} skipped. PC Tidy never downloads files to compare them.");
        if (s.ProjectFoldersSkipped > 0)
            notes.Add($"{s.ProjectFoldersSkipped:N0} code project or app folder{Plural(s.ProjectFoldersSkipped, " was", "s were")} left out: copies inside them are there on purpose.");
        if (s.HardLinks > 0)
            notes.Add($"{s.HardLinks:N0} file{Plural(s.HardLinks, " is a hard link", "s are hard links")} (one file with two names). Removing {Plural(s.HardLinks, "it", "them")} frees nothing, so {Plural(s.HardLinks, "it isn't", "they aren't")} counted.");
        if (junk.NetworkShortcuts > 0)
            notes.Add($"{junk.NetworkShortcuts:N0} shortcut{Plural(junk.NetworkShortcuts, " points", "s point")} to a network place. PC Tidy can't check {Plural(junk.NetworkShortcuts, "it", "those")}, so {Plural(junk.NetworkShortcuts, "it was", "they were")} skipped.");
        if (s.Unreadable > 0)
            notes.Add($"{s.Unreadable:N0} file{Plural(s.Unreadable, " or folder", "s or folders")} couldn't be read (in use or no permission) and {Plural(s.Unreadable, "was", "were")} skipped.");
        return notes;
    }

    /// <summary>
    /// "Is this file inside one of these folders?" in O(depth): the folders go in a hash set and each file's parent chain
    /// is looked up, instead of comparing every file with every folder.
    /// </summary>
    internal sealed class FolderSet(IEnumerable<string> folders)
    {
        private readonly HashSet<string> _folders = folders.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public int Count => _folders.Count;

        /// <summary>True when <paramref name="path"/> is one of the folders or anywhere below one.</summary>
        public bool ContainsAncestorOf(string path)
        {
            for (var p = Key(path); !string.IsNullOrEmpty(p); p = Key(Path.GetDirectoryName(p)))
                if (_folders.Contains(p)) return true;
            return false;
        }

        private static string Key(string? path) => path is null ? "" : path.TrimEnd('\\', '/');
    }

    /// <summary>Safety-guard answers per path for one scan (the guard is asked about every path offered for removal).</summary>
    private sealed class GuardCache(ISafetyGuard guard)
    {
        private readonly ConcurrentDictionary<string, SafetyVerdict> _verdicts = new(StringComparer.OrdinalIgnoreCase);

        public SafetyVerdict Check(string path, bool isDirectory, long size, DateTime writeUtc)
            => _verdicts.GetOrAdd(path, p =>
            {
                try
                {
                    return guard.Check(DuplicatePlanner.Quarantine(p, isDirectory, size, writeUtc, "Check " + Path.GetFileName(p)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return SafetyVerdict.Block("PC Tidy couldn't check this file safely, so it's left alone.");
                }
            });
    }
}
