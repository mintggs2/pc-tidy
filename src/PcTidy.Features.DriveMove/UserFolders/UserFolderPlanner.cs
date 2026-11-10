using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.UserFolders;

/// <summary>
/// Windows user folders (Plan §8): Downloads, Videos, Music, and Pictures / Documents / Desktop only when OneDrive isn't
/// backing them up. Planned as <see cref="ActionKind.KnownFolderRedirect"/>: Windows' own known-folder API (what the
/// folder's Properties → Location tab does) copies the folder and its files and keeps the old copy; PC Tidy's handler checks
/// the copy, then the row's quarantine steps (<see cref="OldCopy"/>), which depend on that step, clear the old copy.
/// Rows are Check and never pre-ticked. A folder that already lives elsewhere gets "Put it back in its usual place".
/// </summary>
internal static class UserFolderPlanner
{
    public static Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!c.Drives.MovingAvailable) return Task.FromResult(PartResult.Empty with { Notes = new[] { c.Drives.UnavailableReason! } });
        var all = KnownFolderTargets.All(c.Locations);
        var oneDrive = c.Pc.OneDriveFolders();
        var cloudRoots = c.Locations.CloudRoots.Concat(oneDrive).ToList();
        var items = new List<ScanItem>();
        var movables = new List<Movable>();
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var folder = all[i];
            progress?.Report(new ScanProgress($"Looking at your {folder.Name} folder…", 0.05 + 0.9 * i / all.Count));
            var (item, movable) = PlanOne(c, folder, all, cloudRoots, ct);
            items.Add(item);
            if (movable is not null) movables.Add(movable);
        }
        var group = new ScanGroup
        {
            Id = DriveMoveIds.GroupUserFolders,
            Title = "Your Windows user folders",
            Description = "Moved with Windows' own Location setting, so every app keeps finding them. Each goes into a new, empty folder, never a whole drive. The old copy goes to quarantine only after every file is checked in the new place. Undo puts it back.",
            Badge = Badge.Check,
            Items = items,
        };
        return Task.FromResult(new PartResult(new[] { group }, movables, Array.Empty<string>()));
    }

    private static (ScanItem Item, Movable? Movable) PlanOne(DriveMoveContext c, UserFolder folder, IReadOnlyList<UserFolder> all, IReadOnlyList<string> cloudRoots, CancellationToken ct)
    {
        var current = folder.CurrentPath;
        var backedUp = c.Safety.IsInCloudRoot(current)
                       || cloudRoots.Select(PathText.TryNormalize).OfType<string>().Any(r => PathUtil.IsUnder(current, r));
        if (backedUp)
            return (Info(folder, current,
                $"OneDrive is backing up your {folder.Name} folder, so PC Tidy won't move it: the two would fight. If you really want it on another drive, stop the backup first (OneDrive → Settings → Sync and back up → Manage back up).",
                "Backed up by OneDrive"), null);

        var totals = FolderMeasure.Measure(c.Walker, current, ct);
        var from = c.Drives.DriveOf(current);
        var atDefault = current.Equals(folder.DefaultPath, StringComparison.OrdinalIgnoreCase);

        // Already moved somewhere else: offer the way back.
        if (!atDefault)
            return (RestoreItem(c, folder, all, cloudRoots, totals, ct), null);

        if (!totals.Exists)
            return (Info(folder, current, $"Your {folder.Name} folder isn't there right now, so there's nothing to move.", "Not found"), null);

        string? lastProblem = null;
        foreach (var to in c.Drives.TargetsFor(from, totals.Bytes, c.Thresholds))
        {
            var target = KnownFolderTargets.Suggested(to, c.ProfileFolderName, folder.Name);
            var problem = KnownFolderTargets.Problem(target, folder, all, c.Drives, c.Safety, cloudRoots);
            if (problem is not null) { lastProblem = $"{to.Name}: {problem}"; continue; }
            var action = Plans.Redirect(folder.Id, folder.Name, current, target, totals.Bytes, restore: false);
            var verdict = c.Safety.Check(action);
            if (!verdict.Allowed) { lastProblem = verdict.Reason; continue; }
            var old = OldCopy.Plan(c, current, folder.Name, action, ct);
            var fromName = from?.Name ?? "this drive";
            var details = new Dictionary<string, string> { ["Now"] = current, ["New place"] = target, ["Files"] = totals.Files.ToString("N0") };
            if (old.LeftBehind > 0) details["Stays in the old place"] = $"{old.LeftBehind:N0} item{(old.LeftBehind == 1 ? "" : "s")}";
            var item = new ScanItem
            {
                Title = $"Move {folder.Name} to {to.Name}",
                Detail = $"{current} → {target}",
                Reason = $"Windows' own Location setting copies the folder with everything in it to {to.Name}, so apps keep finding it. "
                         + $"PC Tidy checks every file arrived; only then does the old copy on {fromName} go to quarantine. {OldCopy.SpaceNote(fromName)} "
                         + $"Undo puts it all back. {to.Name} has {Sizes.Format(to.FreeBytes)} free.",
                Badge = Badge.Check,
                SizeBytes = old.Bytes,
                FileCount = (int)Math.Min(int.MaxValue, totals.Files),
                CurrentState = $"{Sizes.Format(totals.Bytes)} on {from?.Name ?? "this PC"}",
                Details = details,
                Actions = old.Steps.Prepend(action).ToList(),
            };
            return (item, new Movable(MovableKind.UserFolder, folder.Name, current, old.Bytes, null, item.Actions, to));
        }

        var roomiest = c.Drives.RoomiestOther(from);
        var why = lastProblem
                  ?? (roomiest is null ? $"There's no other drive for your {folder.Name} folder."
                      : $"{roomiest.Name} has {Sizes.Format(roomiest.FreeBytes)} free; your {folder.Name} folder needs {Sizes.Format(c.Thresholds.Needed(totals.Bytes))} (its size plus 10% to spare).");
        return (Info(folder, current, why, $"{Sizes.Format(totals.Bytes)} on {from?.Name ?? "this PC"}", totals), null);
    }

    private static ScanItem RestoreItem(DriveMoveContext c, UserFolder folder, IReadOnlyList<UserFolder> all, IReadOnlyList<string> cloudRoots, FolderTotals totals, CancellationToken ct)
    {
        var target = folder.DefaultPath;
        var defaultDrive = c.Drives.DriveOf(target);
        var problem = KnownFolderTargets.Problem(target, folder, all, c.Drives, c.Safety, cloudRoots);
        if (problem is null && defaultDrive is not null && defaultDrive.FreeBytes < c.Thresholds.Needed(totals.Bytes))
            problem = $"{defaultDrive.Name} has {Sizes.Format(defaultDrive.FreeBytes)} free; your {folder.Name} folder needs {Sizes.Format(c.Thresholds.Needed(totals.Bytes))}.";
        var action = Plans.Redirect(folder.Id, folder.Name, folder.CurrentPath, target, totals.Bytes, restore: true);
        if (problem is null && c.Safety.Check(action) is { Allowed: false } verdict) problem = verdict.Reason;
        var old = problem is null ? OldCopy.Plan(c, folder.CurrentPath, folder.Name, action, ct) : OldCopy.None;
        var fromName = c.Drives.DriveOf(folder.CurrentPath)?.Name ?? "the other drive";
        var details = new Dictionary<string, string> { ["Now"] = folder.CurrentPath, ["Usual place"] = target };
        if (old.LeftBehind > 0) details["Stays in the old place"] = $"{old.LeftBehind:N0} item{(old.LeftBehind == 1 ? "" : "s")}";
        return new ScanItem
        {
            Title = $"Restore default location for {folder.Name}",
            Detail = $"{folder.CurrentPath} → {target}",
            Reason = problem ?? $"Your {folder.Name} folder is on {fromName} now. Windows' own Location setting copies it back to its usual place; "
                     + $"once PC Tidy has checked every file arrived, the old copy on {fromName} goes to quarantine. {OldCopy.SpaceNote(fromName)} Undo moves it again.",
            Badge = Badge.Check,
            SizeBytes = problem is null ? old.Bytes : 0,
            FileCount = (int)Math.Min(int.MaxValue, totals.Files),
            CurrentState = $"Moved · {Sizes.Format(totals.Bytes)}",
            Details = details,
            Actions = old.Steps.Prepend(action).ToList(),
            DisabledReason = problem,
        };
    }

    /// <summary>A row shown so you know. No size: group totals only count what can really move.</summary>
    private static ScanItem Info(UserFolder folder, string current, string reason, string state, FolderTotals? totals = null) => new()
    {
        Title = folder.Name,
        Detail = current,
        Reason = reason,
        Badge = Badge.Check,
        CurrentState = state,
        Details = totals is { Exists: true } t ? new Dictionary<string, string> { ["Size"] = Sizes.Format(t.Bytes), ["Files"] = t.Files.ToString("N0") } : null,
        DisabledReason = reason,
    };
}
