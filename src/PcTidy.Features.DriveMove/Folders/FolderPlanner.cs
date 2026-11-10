using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.Folders;

/// <summary>What one part of the section found: groups for its tab, things for the Drive balance view, notes.</summary>
internal sealed record PartResult(IReadOnlyList<ScanGroup> Groups, IReadOnlyList<Movable> Movables, IReadOnlyList<string> Notes)
{
    public static readonly PartResult Empty = new(Array.Empty<ScanGroup>(), Array.Empty<Movable>(), Array.Empty<string>());
}

/// <summary>
/// "Any folder → another drive, leaving a junction" (Plan §8, Research.md §7). Lists big folders inside your user folders
/// and profile, plus any folder you picked, and plans <see cref="ActionKind.MoveFolderWithJunction"/> to
/// &lt;drive&gt;\Moved by PC Tidy\&lt;name&gt; on the drive with the most room. Refused folders are shown greyed out with the reason.
/// </summary>
internal static class FolderPlanner
{
    /// <summary>Windows' own profile folders that aren't worth moving or that Windows expects in place.</summary>
    private static readonly HashSet<string> ProfileSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Contacts", "Favorites", "Links", "Searches", "Saved Games", "3D Objects", "OneDrive", "MicrosoftEdgeBackups",
        "Application Data", "Cookies", "Local Settings", "NetHood", "PrintHood", "Recent", "SendTo", "Start Menu", "Templates",
        "My Documents", "IntelGraphicsProfiles",
    };

    public static async Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!c.Drives.MovingAvailable) return PartResult.Empty with { Notes = new[] { c.Drives.UnavailableReason! } };
        var rules = new JunctionRules(c.Locations, c.Safety);
        var picked = (c.Scan.Roots ?? Array.Empty<string>()).Select(PathText.TryNormalize).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = Candidates(c).Where(x => !picked.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();

        var movable = new List<ScanItem>();
        var refused = new List<ScanItem>();
        var movables = new List<Movable>();
        var all = picked.Select(p => (Path: p, Picked: true)).Concat(candidates.Select(p => (Path: p, Picked: false))).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, isPicked) = all[i];
            progress?.Report(new ScanProgress($"Measuring {Path.GetFileName(folder)}…", 0.05 + 0.9 * i / Math.Max(1, all.Count)));
            var row = await PlanOneAsync(c, rules, folder, isPicked, ct).ConfigureAwait(false);
            if (row is null) continue;
            (row.Value.Item.DisabledReason is null ? movable : refused).Add(row.Value.Item);
            if (row.Value.Movable is { } m) movables.Add(m);
        }

        var groups = new List<ScanGroup>();
        if (movable.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupFolders,
                Title = "Big folders you can move",
                Description = "Each folder is copied to the other drive, checked file by file, and a link is left at the old place so apps still find it. The old copy waits in quarantine. Undo puts it back.",
                Badge = Badge.Check,
                Items = movable.OrderByDescending(x => x.SizeBytes).ToList(),
            });
        if (refused.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupFoldersRefused,
                Title = "Shown so you know",
                Description = "These folders stay where they are. Each one says why.",
                Badge = Badge.Check,
                Items = refused.OrderByDescending(x => x.SizeBytes).ToList(),
            });
        var notes = new List<string>();
        if (movable.Count == 0 && refused.Count == 0)
            notes.Add($"No folder of {Sizes.Format(c.Thresholds.MinFolderBytes)} or more was found in your user folders. Pick any folder with \"Choose a folder\".");
        return new PartResult(groups, movables, notes);
    }

    /// <summary>Folders worth a look: the children of each user folder (not the cloud-backed ones) and of the profile.</summary>
    internal static IEnumerable<string> Candidates(DriveMoveContext c)
    {
        var k = c.Locations;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userFolders = new[] { k.Downloads, k.Videos, k.Music, k.Pictures, k.Documents, k.Desktop }
            .Select(PathText.TryNormalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var folder in userFolders)
        {
            if (c.Safety.IsInCloudRoot(folder)) continue; // OneDrive-backed: never junctioned
            foreach (var child in FolderMeasure.SubFolders(folder))
                if (seen.Add(child)) yield return child;
        }
        if (PathText.TryNormalize(k.UserProfile) is not { } profile) yield break;
        foreach (var child in FolderMeasure.SubFolders(profile))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || ProfileSkip.Contains(name) || name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) continue;
            if (userFolders.Any(u => u.Equals(child, StringComparison.OrdinalIgnoreCase))) continue;
            if (seen.Add(child)) yield return child;
        }
    }

    private static async Task<(ScanItem Item, Movable? Movable)?> PlanOneAsync(DriveMoveContext c, JunctionRules rules, string folder, bool picked, CancellationToken ct)
    {
        var name = Path.GetFileName(folder);
        var refusal = rules.Refusal(folder);
        // Refused folders are only measured when the user picked them (no point walking Windows or a game library).
        if (refusal is not null)
            return picked ? (Refused(folder, refusal, FolderTotals.Missing), null) : null;

        var totals = FolderMeasure.Measure(c.Walker, folder, ct, c.Thresholds.MaxInUseFiles);
        if (!totals.Exists) return picked ? (Refused(folder, JunctionRules.NotThere, totals), null) : null;
        if (!picked && totals.Bytes < c.Thresholds.MinFolderBytes) return null;

        var from = c.Drives.DriveOf(folder);
        var to = c.Drives.TargetsFor(from, totals.Bytes, c.Thresholds).FirstOrDefault();
        if (to is null)
        {
            var roomiest = c.Drives.RoomiestOther(from);
            var why = roomiest is null ? JunctionRules.TooBigForDrives
                : $"{roomiest.Name} has {Sizes.Format(roomiest.FreeBytes)} free; this folder needs {Sizes.Format(c.Thresholds.Needed(totals.Bytes))} (its size plus 10% to spare).";
            return (Refused(folder, why, totals), null);
        }
        if (totals.Placeholders > 0)
            return (Refused(folder, "Some files in it are online-only in your cloud folder. Moving them would download them, so the folder stays put.", totals), null);

        var destination = PathUtil.NextFreeName(Path.Combine(to.Root, DriveMoveIds.MovedFolderName, name));
        var warning = rules.Warning(folder);
        var action = Plans.MoveWithJunction(folder, destination, totals, to, warning?.CloseFirst);
        var verdict = c.Safety.Check(action);
        string? disabled = verdict.Allowed ? null : verdict.Reason ?? "PC Tidy's safety rules don't allow moving this folder.";
        if (disabled is null && await InUse.FirstBusyAsync(c.Scan.InUse, totals.SampleFiles, ct).ConfigureAwait(false) is { } busy)
            disabled = InUse.Describe(busy);

        var reason = $"Copied to {destination}, checked file by file, then a link is left at the old place so apps still find it. The old copy waits in quarantine. Undo moves it back.";
        if (warning is not null) reason = warning.Text + " " + reason;
        if (totals.Links > 0) reason += $" It holds {totals.Links:N0} link{(totals.Links == 1 ? "" : "s")} of its own, which are moved as links.";
        var item = new ScanItem
        {
            Title = $"{name} → {to.Name}",
            Detail = folder,
            Reason = disabled is null ? reason : disabled,
            Badge = Badge.Check,
            DefaultSelected = false,
            // Rows that can't be ticked carry no size, so group totals only count what can really move.
            SizeBytes = disabled is null ? totals.Bytes : 0,
            FileCount = (int)Math.Min(int.MaxValue, totals.Files),
            LastModifiedUtc = totals.LastWriteUtc,
            RequiresClosedApps = warning?.CloseFirst is { Count: > 0 } close ? close : null,
            CurrentState = $"{Sizes.Format(totals.Bytes)} on {from?.Name ?? "this PC"}",
            Details = new Dictionary<string, string>
            {
                ["From"] = folder,
                ["To"] = destination,
                ["Files"] = totals.Files.ToString("N0"),
                [$"Free on {to.Name}"] = Sizes.Format(to.FreeBytes),
            },
            Actions = new[] { action },
            DisabledReason = disabled,
        };
        var movable = disabled is null ? new Movable(MovableKind.Folder, name, folder, totals.Bytes, null, new[] { action }, to) : null;
        return (item, movable);
    }

    /// <summary>A folder that stays put, with why. No size: group totals only count what can really move.</summary>
    private static ScanItem Refused(string folder, string reason, FolderTotals totals) => new()
    {
        Title = Path.GetFileName(folder) is { Length: > 0 } n ? n : folder,
        Detail = folder,
        Reason = reason,
        Badge = Badge.Check,
        CurrentState = totals.Exists ? Sizes.Format(totals.Bytes) : null,
        DisabledReason = reason,
    };
}
