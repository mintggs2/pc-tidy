using System.Collections.Concurrent;
using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Builds a <see cref="SpaceIndex"/> for a drive or folder. Read-only: lists folders through <see cref="IFileWalker"/>,
/// never opens a file, never follows junctions/symlinks (they become 0-byte link nodes), never touches the content of
/// OneDrive online-only files (counted at 0 bytes on disk).
/// The top <see cref="SpaceScanOptions.SplitDepth"/> levels are listed first; the folders found there are then walked
/// in parallel, one branch per worker, each into its own <see cref="NodeSegment"/>.
/// </summary>
public sealed class SpaceIndexScanner
{
    private const int ProgressEvery = 4096;
    /// <summary>Classic MAX_PATH. Longer paths still scan fine but are flagged (many apps can't open them).</summary>
    public const int MaxClassicPath = 260;
    private const SpaceNodeFlags Inherited = SpaceNodeFlags.Protected | SpaceNodeFlags.AskFirst | SpaceNodeFlags.InGameLibrary;
    /// <summary>Made-up child name used to ask the guard "is what's inside this folder protected too?". Never created.</summary>
    internal const string ProbeChildName = "pctidy-probe-7d1e";

    private readonly IFileWalker _walker;
    private readonly ISafetyGuard _safety;
    private readonly IReparseTagReader _tags;
    private readonly IDriveSpaceProbe _drives;

    public SpaceIndexScanner(IFileWalker walker, ISafetyGuard safety, IReparseTagReader? tags = null, IDriveSpaceProbe? drives = null)
    {
        _walker = walker;
        _safety = safety;
        _tags = tags ?? NativeDisk.Instance;
        _drives = drives ?? new DriveSpaceProbe();
    }

    public Task<SpaceIndex> ScanAsync(string root, SpaceScanOptions? options, IProgress<SpaceScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(root, options ?? new SpaceScanOptions(), progress, ct), ct);

    public SpaceIndex Scan(string root, SpaceScanOptions options, IProgress<SpaceScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rootPath = PathUtil.Normalize(root);
        var run = new Run(this, options, progress, ct, options.ClusterSize ?? NativeDisk.ClusterSize(rootPath));

        // 1. Root + the top levels, listed one folder at a time.
        var skeleton = new NodeSegment(256);
        var rootFlags = SpaceNodeFlags.Directory | run.Classify(rootPath, SpaceNodeFlags.None, SpaceNodeFlags.None);
        skeleton.Add(NodeSegment.ExternalParent, rootPath, rootFlags, 0, 0, 0, 0, 0);
        var frontier = new List<WalkJob> { new(rootPath, 0, rootFlags) };
        for (int level = 0; level < Math.Max(0, options.SplitDepth) && frontier.Count > 0; level++)
        {
            var next = new List<WalkJob>();
            foreach (var job in frontier)
                run.Walk(skeleton, job, recurse: false, next);
            frontier = next;
        }

        // 2. Each remaining folder is a whole branch for one worker.
        var branches = new (NodeSegment Segment, int Anchor)[frontier.Count];
        try
        {
            var partitioner = Partitioner.Create(Enumerable.Range(0, frontier.Count), EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism), CancellationToken = ct }, i =>
            {
                var job = frontier[i];
                var segment = new NodeSegment();
                var anchored = job with { LocalIndex = NodeSegment.ExternalParent };
                var pending = new List<WalkJob> { anchored };
                // Cloud folders found inside the branch are walked afterwards in the same segment.
                for (int k = 0; k < pending.Count; k++) run.Walk(segment, pending[k], recurse: true, pending);
                branches[i] = (segment, job.LocalIndex);
            });
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();

        // 3. Merge, aggregate, add "Unscanned / system".
        var drive = options.IncludeUnscannedNode ? _drives.ForScanRoot(rootPath) : null;
        var index = SpaceIndexAssembler.Assemble(skeleton, branches, drive?.UsedBytes, totals => new SpaceScanSummary
        {
            RootPath = rootPath,
            Files = totals.Files,
            Folders = totals.Folders,
            Links = totals.Links,
            LongPaths = totals.LongPaths,
            Inaccessible = run.Inaccessible,
            ScannedBytes = totals.Bytes,
            ScannedBytesOnDisk = totals.BytesOnDisk,
            DriveTotalBytes = drive?.TotalBytes,
            DriveUsedBytes = drive?.UsedBytes,
            ClusterSize = run.ClusterSize,
            Elapsed = sw.Elapsed,
            Notes = run.Notes.ToArray(),
        });
        run.ReportNow(null);
        return index;
    }

    /// <summary>A folder still to be listed. <see cref="LocalIndex"/> is its node in the segment being filled.</summary>
    private readonly record struct WalkJob(string Path, int LocalIndex, SpaceNodeFlags Flags);

    /// <summary>State shared by all workers of one scan.</summary>
    private sealed class Run
    {
        private readonly SpaceIndexScanner _owner;
        private readonly SpaceScanOptions _options;
        private readonly IProgress<SpaceScanProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly PathSet _exclusions;
        private readonly PathSet _gameRoots;
        private readonly WalkOptions _recursive = new() { Recurse = true, IncludeHidden = true, IncludeSystem = true };
        private readonly WalkOptions _oneLevel = new() { Recurse = false, IncludeHidden = true, IncludeSystem = true };
        private long _files, _folders, _bytes, _inaccessible, _sinceReport;

        public Run(SpaceIndexScanner owner, SpaceScanOptions options, IProgress<SpaceScanProgress>? progress, CancellationToken ct, int clusterSize)
        {
            _owner = owner; _options = options; _progress = progress; _ct = ct;
            ClusterSize = clusterSize;
            _exclusions = new PathSet(options.Exclusions);
            _gameRoots = new PathSet(options.GameRoots);
        }

        public int ClusterSize { get; }
        public long Inaccessible => Interlocked.Read(ref _inaccessible);
        public ConcurrentQueue<string> Notes { get; } = new();

        /// <summary>Lists <paramref name="job"/> into <paramref name="seg"/>. Folders that should be walked separately
        /// (one-level mode: every walkable sub-folder; recursive mode: cloud folders the walker won't enter) go to <paramref name="later"/>.</summary>
        public void Walk(NodeSegment seg, WalkJob job, bool recurse, List<WalkJob> later)
        {
            // Folders seen in this walk, by full path without trailing '\', so each entry finds its parent.
            var folders = new Dictionary<string, (int Index, SpaceNodeFlags Flags)>(StringComparer.OrdinalIgnoreCase)
            {
                [job.Path.TrimEnd('\\')] = (job.LocalIndex, job.Flags),
            };
            string lastParentPath = job.Path.TrimEnd('\\');
            (int Index, SpaceNodeFlags Flags) lastParent = (job.LocalIndex, job.Flags);

            bool OnEntry(FileEntry e)
            {
                var parentSpan = e.FullPath.AsSpan(0, Math.Max(0, e.FullPath.Length - e.Name.Length - 1));
                if (!parentSpan.Equals(lastParentPath, StringComparison.OrdinalIgnoreCase))
                {
                    var key = new string(parentSpan);
                    if (!folders.TryGetValue(key, out lastParent)) return false; // not under this walk (shouldn't happen)
                    lastParentPath = key;
                }
                var (index, walk, flags) = AddEntry(seg, lastParent.Index, lastParent.Flags, e);
                if (walk == Descend.Walker && recurse)
                {
                    folders[e.FullPath.TrimEnd('\\')] = (index, flags);
                    return true;
                }
                if (walk != Descend.No) later.Add(new WalkJob(e.FullPath, index, flags));
                return false;
            }

            try
            {
                var stats = _owner._walker.Walk(job.Path, recurse ? _recursive : _oneLevel, OnEntry, _ct);
                Interlocked.Add(ref _inaccessible, stats.Inaccessible);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A broken branch must not sink the whole scan: note it, keep what we have.
                Interlocked.Increment(ref _inaccessible);
                Notes.Enqueue($"Couldn't finish reading {job.Path}: {ex.Message}");
            }
            ReportNow(job.Path);
        }

        private enum Descend { No, Walker, Separately }

        private (int Index, Descend Walk, SpaceNodeFlags Flags) AddEntry(NodeSegment seg, int parent, SpaceNodeFlags parentFlags, in FileEntry e)
        {
            var flags = parentFlags & Inherited;
            if (e.IsHidden) flags |= SpaceNodeFlags.Hidden;
            if (e.IsSystem) flags |= SpaceNodeFlags.System;
            if (e.FullPath.Length > MaxClassicPath) flags |= SpaceNodeFlags.LongPath;
            int touched = SpaceTime.ToMinutes(e.LastTouchedUtc);
            int accessed = SpaceTime.ToMinutes(e.LastAccessUtc);

            if (e.IsDirectory)
            {
                Interlocked.Increment(ref _folders);
                flags |= SpaceNodeFlags.Directory;
                var walk = Descend.Walker;
                if (e.IsReparsePoint)
                {
                    var tag = _owner._tags.GetTag(e.FullPath);
                    if (tag is null || NativeDisk.IsNameSurrogate(tag.Value))
                    {
                        flags |= SpaceNodeFlags.Link;
                        return (seg.Add(parent, e.Name, flags, 0, 0, 0, touched, accessed), Descend.No, flags);
                    }
                    walk = Descend.Separately; // e.g. a OneDrive folder: real content the walker won't enter by itself
                }
                // A folder with the System attribute is never touched (plan §6): no need to ask the guard.
                flags = e.IsSystem ? flags | SpaceNodeFlags.Protected : Classify(e.FullPath, flags, parentFlags);
                if ((flags & SpaceNodeFlags.InGameLibrary) == 0 && !_gameRoots.IsEmpty && _gameRoots.ContainsOrIsUnder(e.FullPath))
                    flags |= SpaceNodeFlags.InGameLibrary;
                if (ShouldSkipContents(e.FullPath, flags))
                {
                    flags |= SpaceNodeFlags.NotWalked;
                    walk = Descend.No;
                }
                // Quick mode lists a CheckInside folder only to find your folders in it; its own files aren't measured.
                else if (_options.SkipProtectedContents && (flags & SpaceNodeFlags.CheckInside) != 0)
                    flags |= SpaceNodeFlags.NotWalked;
                return (seg.Add(parent, e.Name, flags, 0, 0, 0, touched, accessed), walk, flags);
            }

            // Quick mode: files in a protected folder could never be suggested, so they aren't even recorded.
            if (_options.SkipProtectedContents && (parentFlags & SpaceNodeFlags.Protected) != 0) return (-1, Descend.No, flags);

            long size = e.Size, onDisk;
            if (e.IsCloudPlaceholder)
            {
                flags |= SpaceNodeFlags.CloudPlaceholder;
                onDisk = 0;
            }
            else if (e.IsReparsePoint && IsLinkFile(e.FullPath))
            {
                flags |= SpaceNodeFlags.Link;
                size = 0; onDisk = 0;
            }
            else
            {
                onDisk = RoundToCluster(size);
                if ((e.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0) flags |= SpaceNodeFlags.EstimatedOnDisk;
            }
            // Anything with the System attribute is never touched (plan §6).
            if (e.IsSystem) flags |= SpaceNodeFlags.Protected;

            var ext = seg.Extensions.GetOrAdd(ExtensionTable.ExtensionOf(e.Name));
            int index = seg.Add(parent, e.Name, flags, ext, size, onDisk, touched, accessed);
            Interlocked.Add(ref _bytes, size);
            if (Interlocked.Increment(ref _files) % ProgressEvery == 0) ReportNow(null);
            return (index, Descend.No, flags);
        }

        private bool IsLinkFile(string path)
        {
            var tag = _owner._tags.GetTag(path);
            return tag is null || NativeDisk.IsNameSurrogate(tag.Value);
        }

        private bool ShouldSkipContents(string path, SpaceNodeFlags flags)
        {
            // A protected folder that may hold a folder of yours (CheckInside, like Users) is still walked.
            if (_options.SkipProtectedContents && (flags & (SpaceNodeFlags.Protected | SpaceNodeFlags.CheckInside)) == SpaceNodeFlags.Protected) return true;
            if (!_exclusions.IsEmpty && _exclusions.ContainsOrIsUnder(path)) return true;
            return _options.SkipCloudRoots && SafeIsInCloudRoot(path, flags);
        }

        /// <summary>
        /// Adds Protected / SelfLocked / AskFirst from the SafetyGuard, once per folder (plus one probe for the few
        /// it blocks). Protected is inherited, so the contents of a protected folder are normally never asked about.
        /// <para>
        /// A blocked folder whose made-up child is fine is only blocked as a whole (a drive root, Downloads, a folder
        /// holding a protected one): SelfLocked, and its contents keep their own verdicts. When the made-up child is
        /// blocked too, the folder is Protected, but its direct sub-folders are still asked once (CheckInside):
        /// "Users" blocks everyone else's folders yet your own profile inside it is yours to tidy. The assembler then
        /// turns a Protected folder with unprotected folders inside into SelfLocked.
        /// </para>
        /// </summary>
        /// <param name="flags">Flags inherited from the parent.</param>
        /// <param name="parentFlags">The parent's own flags (to see CheckInside).</param>
        public SpaceNodeFlags Classify(string path, SpaceNodeFlags flags, SpaceNodeFlags parentFlags)
        {
            bool recheck = (parentFlags & SpaceNodeFlags.CheckInside) != 0;
            if (recheck) flags &= ~SpaceNodeFlags.Protected;
            else if ((flags & SpaceNodeFlags.Protected) != 0) return flags;
            try
            {
                switch (_owner._safety.ClassifyPath(path).Protection)
                {
                    case PathProtection.Never:
                        var inside = _owner._safety.ClassifyPath(Path.Combine(path, ProbeChildName)).Protection;
                        if (inside != PathProtection.Never)
                            return flags | SpaceNodeFlags.SelfLocked | (inside == PathProtection.AskFirst ? SpaceNodeFlags.AskFirst : 0);
                        // One level of re-checking only: deeper exceptions would cost a guard call per protected folder.
                        return (flags | SpaceNodeFlags.Protected | (recheck ? 0 : SpaceNodeFlags.CheckInside)) & ~SpaceNodeFlags.AskFirst;
                    case PathProtection.AskFirst: return flags | SpaceNodeFlags.AskFirst;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // If we can't tell, treat it as protected: the map still shows it, actions stay off.
                return flags | SpaceNodeFlags.Protected;
            }
            return flags;
        }

        /// <summary>
        /// Cloud folders are always ask-first (or the cloud root itself, blocked as a whole), so a folder the guard
        /// called ordinary can't be in one: only the rest are asked, which saves a guard call for nearly every folder.
        /// </summary>
        private bool SafeIsInCloudRoot(string path, SpaceNodeFlags flags)
        {
            if ((flags & (SpaceNodeFlags.AskFirst | SpaceNodeFlags.SelfLocked)) == 0) return false;
            try { return _owner._safety.IsInCloudRoot(path); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return true; }
        }

        private long RoundToCluster(long size)
            => size <= 0 ? 0 : (size + ClusterSize - 1) / ClusterSize * ClusterSize;

        public void ReportNow(string? currentFolder)
        {
            if (_progress is null) return;
            if (currentFolder is not null && Interlocked.Increment(ref _sinceReport) % 64 != 0) return;
            _progress.Report(new SpaceScanProgress(Interlocked.Read(ref _files), Interlocked.Read(ref _folders), Interlocked.Read(ref _bytes), currentFolder));
        }
    }
}
