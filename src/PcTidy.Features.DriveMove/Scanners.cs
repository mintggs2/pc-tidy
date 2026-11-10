using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Caches;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.Features.DriveMove.Games;
using PcTidy.Features.DriveMove.UserFolders;

namespace PcTidy.Features.DriveMove;

/// <summary>One tab of the section as a read-only scanner (each tab of the page scans on its own).</summary>
public abstract class DriveMovePartScanner : IFeatureScanner
{
    public string FeatureId => DriveMoveIds.FeatureId;
    public abstract string Title { get; }
    public Release Release => Release.V2;
    /// <summary>Moving things between drives is always the user's own pick: never part of "Optimize &amp; Organize everything".</summary>
    public bool IncludedInOptimizeAll => false;

    internal abstract Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct);

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var c = DriveMoveContext.From(ctx);
        var part = await PlanAsync(c, progress, ct).ConfigureAwait(false);
        progress?.Report(new ScanProgress("Done", 1));
        return new ScanResult
        {
            FeatureId = FeatureId,
            Title = Title,
            Depth = ctx.Depth,
            Duration = sw.Elapsed,
            Groups = part.Groups.Where(g => g.Items.Count > 0).ToList(),
            Notes = part.Notes.Distinct().ToList(),
        };
    }
}

/// <summary>Big folders → another drive with a link left behind (MoveFolderWithJunction).</summary>
public sealed class FolderMoveScanner : DriveMovePartScanner
{
    public override string Title => "Folders";
    internal override Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
        => FolderPlanner.PlanAsync(c, progress, ct);
}

/// <summary>Windows user folders → another drive with Windows' own Location setting (KnownFolderRedirect).</summary>
public sealed class UserFolderScanner : DriveMovePartScanner
{
    public override string Title => "User folders";
    internal override Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
        => UserFolderPlanner.PlanAsync(c, progress, ct);
}

/// <summary>Games per drive, moved only with each launcher's own Move (OpenExternal).</summary>
public sealed class GameDriveScanner : DriveMovePartScanner
{
    public override string Title => "Games";
    internal override Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
        => GamePlanner.PlanAsync(c, progress, ct);
}

/// <summary>Developer, AI and media app caches, with only each tool's supported relocation.</summary>
public sealed class AppCacheScanner : DriveMovePartScanner
{
    public override string Title => "App caches";
    internal override Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
        => AppCachePlanner.PlanAsync(c, progress, ct);
}

/// <summary>
/// Drive balance: every fixed drive (used / free / media), which ones are full, the biggest things on them that could
/// move and concrete suggestions that carry the same plan items as the other tabs. Reads everything the other tabs read.
/// </summary>
public sealed class DriveBalanceScanner : DriveMovePartScanner
{
    public override string Title => "Drive balance";

    /// <summary>The last advice worked out (the page draws its drive strip from it).</summary>
    public BalanceAdvice? LastAdvice { get; private set; }

    internal override async Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var drivesGroup = DrivesGroup(c);
        if (!c.Drives.MovingAvailable)
        {
            LastAdvice = BalanceAdvisor.Advise(c.Drives, Array.Empty<Movable>(), c.Thresholds, c.NowUtc);
            return new PartResult(new[] { drivesGroup }, Array.Empty<Movable>(), new[] { c.Drives.UnavailableReason! });
        }

        var movables = new List<Movable>();
        var parts = new (string Message, Func<Task<PartResult>> Run)[]
        {
            ("Measuring your user folders…", () => UserFolderPlanner.PlanAsync(c, null, ct)),
            ("Looking for big folders…", () => FolderPlanner.PlanAsync(c, null, ct)),
            ("Reading your game launchers…", () => GamePlanner.PlanAsync(c, null, ct)),
            ("Looking for app caches…", () => AppCachePlanner.PlanAsync(c, null, ct)),
        };
        for (int i = 0; i < parts.Length; i++)
        {
            progress?.Report(new ScanProgress(parts[i].Message, 0.05 + 0.9 * i / parts.Length));
            movables.AddRange((await parts[i].Run().ConfigureAwait(false)).Movables);
        }
        var advice = BalanceAdvisor.Advise(c.Drives, movables, c.Thresholds, c.NowUtc);
        LastAdvice = advice;

        var groups = new List<ScanGroup> { drivesGroup };
        if (advice.Suggestions.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupSuggestions,
                Title = "Suggestions",
                Description = "Tick a suggestion to do it. Each one only moves things to a drive with room for them plus 10% to spare.",
                Badge = Badge.Check,
                Items = advice.Suggestions.Select(SuggestionRow).ToList(),
            });
        if (advice.Biggest.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupBiggest,
                Title = advice.FullDrives.Count > 0 ? $"Biggest things you could move off {string.Join(" and ", advice.FullDrives.Select(d => d.Name))}" : "Biggest things you could move",
                Description = "For a quick picture. Each one is moved from its own tab.",
                Badge = Badge.Check,
                Items = advice.Biggest.Select(m => new ScanItem
                {
                    Title = m.Name,
                    Detail = m.Path,
                    Reason = KindText(m.Kind),
                    Badge = Badge.Check,
                    CurrentState = Sizes.Format(m.Bytes),
                }).ToList(),
            });
        var notes = new List<string>();
        if (advice.FullDrives.Count == 0) notes.Add("Your drives have room to spare. Nothing needs moving right now, but you can still move things from the other tabs.");
        return new PartResult(groups, movables, notes);
    }

    private static ScanItem SuggestionRow(Suggestion s) => new()
    {
        Title = s.Title,
        Detail = s.Items.Count == 1 ? s.Items[0].Path : $"{s.Items.Count} items on {s.From.Name}",
        Reason = s.Reason,
        Badge = s.Actions.All(a => a.Kind == ActionKind.OpenExternal) ? Badge.Safe : Badge.Check,
        // Only what really moves counts toward the totals; a launcher link moves nothing by itself.
        SizeBytes = s.Actions.All(a => a.Kind == ActionKind.OpenExternal) ? 0 : s.Bytes,
        CurrentState = $"{s.From.Name}: {Sizes.Format(s.From.FreeBytes)} free",
        Actions = s.Actions,
    };

    private static string KindText(MovableKind k) => k switch
    {
        MovableKind.UserFolder => "A Windows user folder: moved from the User folders tab.",
        MovableKind.Folder => "A folder: moved from the Folders tab, with a link left at the old place.",
        MovableKind.AppCache => "An app's cache: moved from the App caches tab with the app's own setting.",
        _ => "A game: moved with its launcher (see the Games tab).",
    };

    internal static ScanGroup DrivesGroup(DriveMoveContext c) => new()
    {
        Id = DriveMoveIds.GroupDrives,
        Title = "Your drives",
        Description = "How full each fixed drive is. Only drives inside this PC are listed.",
        Badge = Badge.Safe,
        Items = c.Drives.All.Select(d => new ScanItem
        {
            Title = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name} {d.Label}",
            Detail = d.Root,
            Reason = !d.SizeKnown ? "PC Tidy couldn't read this drive's size."
                : d.IsFull(c.Thresholds) ? $"Getting full: {d.FreeFraction:P0} free. Moving big things off it keeps Windows and updates running smoothly."
                : $"{d.FreeFraction:P0} free.",
            Badge = Badge.Safe,
            CurrentState = d.SizeKnown ? $"{Sizes.Format(d.FreeBytes)} free of {Sizes.Format(d.TotalBytes)} · {d.MediaText}{(d.IsSystem ? " · Windows" : "")}" : d.MediaText,
        }).ToList(),
    };
}

/// <summary>
/// The section's scanner for the shell (module list, History, anything that wants "everything"): every tab's groups in
/// one result. Suggestions are left out here because they repeat items from the other tabs. Never in Optimize-all.
/// </summary>
public sealed class DriveMoveScanner : DriveMovePartScanner
{
    public override string Title => DriveMoveIds.Title;

    internal override async Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!c.Drives.MovingAvailable)
            return new PartResult(new[] { DriveBalanceScanner.DrivesGroup(c) }, Array.Empty<Movable>(), new[] { c.Drives.UnavailableReason! });
        var groups = new List<ScanGroup> { DriveBalanceScanner.DrivesGroup(c) };
        var notes = new List<string>();
        var parts = new (string Message, Func<Task<PartResult>> Run)[]
        {
            ("Looking at your user folders…", () => UserFolderPlanner.PlanAsync(c, null, ct)),
            ("Looking for big folders…", () => FolderPlanner.PlanAsync(c, null, ct)),
            ("Reading your game launchers…", () => GamePlanner.PlanAsync(c, null, ct)),
            ("Looking for app caches…", () => AppCachePlanner.PlanAsync(c, null, ct)),
        };
        for (int i = 0; i < parts.Length; i++)
        {
            progress?.Report(new ScanProgress(parts[i].Message, 0.05 + 0.9 * i / parts.Length));
            var part = await parts[i].Run().ConfigureAwait(false);
            groups.AddRange(part.Groups);
            notes.AddRange(part.Notes);
        }
        return new PartResult(groups, Array.Empty<Movable>(), notes);
    }
}
