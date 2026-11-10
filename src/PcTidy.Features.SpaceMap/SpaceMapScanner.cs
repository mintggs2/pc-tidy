using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap;

/// <summary>
/// Space map's part of "Optimize &amp; Organize everything": only the "Big and old" group, every item a
/// <see cref="Badge.Check"/> that is never ticked by default. Suggests a move to another fixed drive when the
/// file is on the system drive and another drive has room; otherwise quarantine.
/// Quick = the user's profile; Deep = every fixed drive. Protected folders and OneDrive aren't walked
/// (nothing in them could be suggested anyway).
/// </summary>
public sealed class SpaceMapScanner : IFeatureScanner
{
    public const string BigAndOldGroupId = "spacemap.big-and-old";

    private readonly Func<DateTime> _utcNow;
    private readonly IReparseTagReader? _tags;
    private readonly IDriveSpaceProbe? _drives;
    private readonly IPathFacts? _facts;

    public SpaceMapScanner() : this(null) { }

    /// <summary>Test seam: clock, reparse reader, drive space and file facts.</summary>
    public SpaceMapScanner(Func<DateTime>? utcNow, IReparseTagReader? tags = null, IDriveSpaceProbe? drives = null, IPathFacts? facts = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _tags = tags;
        _drives = drives;
        _facts = facts;
    }

    public string FeatureId => SpaceActions.FeatureId;
    public string Title => "Space map";
    public Release Release => Release.V1;
    public bool IncludedInOptimizeAll => true;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var drives = _drives ?? new DriveSpaceProbe(ctx.System);
        var scanner = new SpaceIndexScanner(ctx.Walker, ctx.Safety, _tags, drives);
        var actions = new SpaceActions(ctx.Locations, ctx.Safety, drives, ctx.Settings.QuarantineDays, _facts);
        var options = new SpaceScanOptions
        {
            SkipProtectedContents = true,
            SkipCloudRoots = true,
            IncludeUnscannedNode = false,
            Exclusions = ctx.Settings.ScanExclusions,
            GameRoots = ctx.Locations.GameInstallDirs,
        };

        var items = new List<ScanItem>();
        var notes = new List<string>();
        var now = _utcNow();
        long filesSeen = 0;
        foreach (var root in Roots(ctx))
        {
            ct.ThrowIfCancellationRequested();
            if (!(_facts ?? DiskPathFacts.Instance).DirectoryExists(root)) continue;
            var relay = progress is null ? null : new Progress<SpaceScanProgress>(p =>
                progress.Report(new ScanProgress($"Measuring {p.CurrentFolder ?? root}", null, filesSeen + p.Files, p.Bytes)));
            var index = await scanner.ScanAsync(root, options, relay, ct).ConfigureAwait(false);
            filesSeen += index.Summary.Files;
            notes.AddRange(index.Summary.Notes);
            foreach (var node in SpaceQueries.BigAndOld(index, SpaceIndex.RootNode, now, ctx.Safety))
                if (ToItem(index, node, actions, ctx.Settings.QuarantineDays) is { } item) items.Add(item);
        }

        var groups = new List<ScanGroup>();
        if (items.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = BigAndOldGroupId,
                Title = "Big and old files",
                Description = "Files over 500 MB you haven't changed in 6 months or more. Your call: nothing is ticked for you.",
                Badge = Badge.Check,
                Items = items,
            });
        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = sw.Elapsed,
            Groups = groups,
            Notes = notes,
        };
    }

    /// <summary>User-chosen roots, else Quick: the user profile; Deep: every fixed drive. Nested roots are dropped.</summary>
    private static IReadOnlyList<string> Roots(ScanContext ctx)
    {
        IEnumerable<string> roots = ctx.Roots is { Count: > 0 } r ? r
            : ctx.Depth == ScanDepth.Deep ? ctx.Locations.FixedDrives
            : new[] { ctx.Locations.UserProfile };
        var list = roots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Where(p => !list.Any(o => !o.Equals(p, StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(p, o))).ToList();
    }

    private static ScanItem? ToItem(SpaceIndex index, int node, SpaceActions actions, int quarantineDays)
    {
        var path = index.FullPath(node)!;
        long size = index.Size(node);
        var target = actions.IsOnSystemDrive(path) ? actions.MoveTargets(path, size).FirstOrDefault() : null;
        var result = target is not null ? actions.MoveToDrive(index, node, target) : actions.Quarantine(index, node);
        if (!result.Ok && target is not null) result = actions.Quarantine(index, node);
        if (!result.Ok) return null;

        var touched = index.LastTouchedUtc(node);
        var reason = result.Item!.Kind == ActionKind.MovePath
            ? $"Not changed since {touched.ToLocalTime():MMM yyyy}. Moving it to {target!.DriveRoot} frees {Sizes.Format(index.SizeOnDisk(node))} here and keeps the file."
            : $"Not changed since {touched.ToLocalTime():MMM yyyy}. Quarantine keeps it restorable for {quarantineDays} days, then frees {Sizes.Format(index.SizeOnDisk(node))}.";
        var details = new Dictionary<string, string>
        {
            ["Size on disk"] = Sizes.Format(index.SizeOnDisk(node)),
            ["Last changed"] = touched.ToLocalTime().ToString("d MMM yyyy"),
        };
        var accessed = index.LastAccessUtc(node);
        if (accessed > touched) details["Last opened (if Windows recorded it)"] = accessed.ToLocalTime().ToString("d MMM yyyy");

        return new ScanItem
        {
            Title = index.Name(node),
            Detail = Path.GetDirectoryName(path),
            Reason = reason,
            Badge = Badge.Check,
            DefaultSelected = false,
            SizeBytes = size,
            SizeOnDiskBytes = index.SizeOnDisk(node),
            FileCount = 1,
            LastModifiedUtc = touched,
            Details = details,
            Actions = new[] { result.Item },
        };
    }
}
