using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;

namespace PcTidy.Features.DriveMove.Drives;

public enum MovableKind { UserFolder, Folder, SteamGame, EpicGame, XboxGame, AppCache }

/// <summary>Something big that could live on another drive, with the plan items that move it (empty = steps only).</summary>
/// <param name="LastUsedUtc">Games: last played (or installed when never played). Null = unknown.</param>
/// <param name="TargetDrive">Drive the actions move it to (null when the launcher or the user picks).</param>
public sealed record Movable(
    MovableKind Kind,
    string Name,
    string Path,
    long Bytes,
    DateTime? LastUsedUtc,
    IReadOnlyList<PlanItem> Actions,
    DriveFacts? TargetDrive = null);

/// <summary>One "do this" card on the Drive balance tab.</summary>
public sealed record Suggestion(string Title, string Reason, DriveFacts From, DriveFacts? To, long Bytes, IReadOnlyList<Movable> Items)
{
    /// <summary>The plan items of every thing in it, each once (ten stale Steam games share one "open Steam" item).</summary>
    public IReadOnlyList<PlanItem> Actions => Items.SelectMany(i => i.Actions)
        .GroupBy(a => (a.Kind, a.SourcePath, a.DestinationPath, Target: a.Args?.GetValueOrDefault(DriveMoveArgs.Target)))
        .Select(g => g.First()).ToList();
}

public sealed record BalanceAdvice(
    bool MovingAvailable,
    string? UnavailableReason,
    IReadOnlyList<DriveFacts> Drives,
    IReadOnlyList<DriveFacts> FullDrives,
    IReadOnlyList<Suggestion> Suggestions,
    IReadOnlyList<Movable> Biggest);

/// <summary>
/// Plan §8 "Drive balance view": which drives are full, the biggest things on them that could move, and plain suggestions
/// ("Move Videos (40 GB) to D:", "4 Steam games not played in 6 months (217 GB) → move with Steam"). Pure: no I/O.
/// A suggestion is made only when the target drive still has room for it (size + 10%) after the suggestions before it.
/// </summary>
public static class BalanceAdvisor
{
    public static BalanceAdvice Advise(DriveList drives, IReadOnlyList<Movable> movables, DriveMoveThresholds t, DateTime nowUtc)
    {
        if (!drives.MovingAvailable)
            return new BalanceAdvice(false, drives.UnavailableReason, drives.All, Array.Empty<DriveFacts>(), Array.Empty<Suggestion>(), Array.Empty<Movable>());

        var full = drives.All.Where(d => d.IsFull(t)).OrderBy(d => d.FreeFraction).ToList();
        // Free space left on each drive once the suggestions so far were all done.
        var room = drives.Receivers.ToDictionary(d => d.Root, d => d.FreeBytes, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Suggestion>();
        foreach (var drive in full)
        {
            var here = movables.Where(m => OnDrive(drives, m, drive)).ToList();
            if (StaleSteamGames(drives, drive, here, t, nowUtc, room) is { } games) candidates.Add(games);
            candidates.AddRange(BigThings(drives, drive, here, t, room));
        }

        // Biggest movable things on the full drives (or on the system drive when nothing is full), for the "what's big" list.
        var focus = full.Count > 0 ? full : drives.All.Where(d => d.IsSystem).ToList();
        var biggest = movables.Where(m => focus.Any(f => OnDrive(drives, m, f)))
            .OrderByDescending(m => m.Bytes).Take(t.MaxBiggest).ToList();

        return new BalanceAdvice(true, null, drives.All, full,
            candidates.OrderByDescending(s => s.Bytes).Take(t.MaxSuggestions).ToList(), biggest);
    }

    private static bool OnDrive(DriveList drives, Movable m, DriveFacts drive)
        => drives.DriveOf(m.Path)?.Root.Equals(drive.Root, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The other drive with the most room left that fits <paramref name="bytes"/> plus headroom, or null.</summary>
    private static DriveFacts? Fit(DriveList drives, DriveFacts from, long bytes, DriveMoveThresholds t, Dictionary<string, long> room, DriveFacts? preferred = null)
    {
        bool Fits(DriveFacts d) => !d.Root.Equals(from.Root, StringComparison.OrdinalIgnoreCase)
                                   && room.TryGetValue(d.Root, out var left) && left >= t.Needed(bytes);
        if (preferred is not null) return Fits(preferred) ? preferred : null; // its plan items already name this drive
        return drives.Receivers.Where(Fits).OrderByDescending(d => room[d.Root]).FirstOrDefault();
    }

    /// <summary>Steam games not played for 6 months on a full drive, as one suggestion (Steam moves them one by one).</summary>
    private static Suggestion? StaleSteamGames(DriveList drives, DriveFacts drive, List<Movable> here, DriveMoveThresholds t, DateTime now, Dictionary<string, long> room)
    {
        var cutoff = now.AddDays(-t.StaleGameDays);
        var stale = here.Where(m => m.Kind == MovableKind.SteamGame && m.LastUsedUtc is { } used && used < cutoff && m.Bytes > 0)
            .OrderByDescending(m => m.Bytes).ToList();
        if (stale.Count == 0) return null;
        // Only the games the roomiest drive can take, biggest first.
        var to = Fit(drives, drive, stale[^1].Bytes, t, room);
        if (to is null) return null;
        var fitting = new List<Movable>();
        long total = 0;
        foreach (var g in stale)
        {
            if (room[to.Root] - t.Needed(total + g.Bytes) < 0) continue;
            fitting.Add(g);
            total += g.Bytes;
        }
        if (fitting.Count == 0 || total < t.MinSuggestionBytes) return null;
        room[to.Root] -= total;
        var games = fitting.Count == 1 ? "1 Steam game" : $"{fitting.Count} Steam games";
        return new Suggestion(
            $"{games} not played in 6 months ({Sizes.Format(total)}) → move with Steam to {to.Name}",
            $"{string.Join(", ", fitting.Take(4).Select(s => s.Name))}{(fitting.Count > 4 ? " and more" : "")}. Steam moves them itself " +
            "(Settings → Storage → pick the game → Move), so updates and saves keep working. Ticking this opens Steam's settings for you.",
            drive, to, total, fitting);
    }

    /// <summary>User folders, big folders and caches PC Tidy can move itself, biggest first, each to a drive with room.</summary>
    private static IEnumerable<Suggestion> BigThings(DriveList drives, DriveFacts drive, List<Movable> here, DriveMoveThresholds t, Dictionary<string, long> room)
    {
        // Never two suggestions for nested things (Videos and Videos\Big): ticking both would fight over the same files.
        var taken = new List<string>();
        foreach (var m in here.Where(m => m.Kind is MovableKind.UserFolder or MovableKind.Folder or MovableKind.AppCache
                                          && m.Bytes >= t.MinSuggestionBytes && m.Actions.Count > 0)
                              .OrderByDescending(m => m.Bytes))
        {
            if (taken.Any(p => PathUtil.IsUnder(m.Path, p) || PathUtil.IsUnder(p, m.Path))) continue;
            var to = Fit(drives, drive, m.Bytes, t, room, m.TargetDrive);
            if (to is null) continue;
            taken.Add(m.Path);
            room[to.Root] -= m.Bytes;
            var reason = m.Kind switch
            {
                MovableKind.UserFolder => $"Windows' own Location setting moves it with everything inside, so apps keep finding it. {drive.Name} has {Sizes.Format(drive.FreeBytes)} free, {to.Name} has {Sizes.Format(to.FreeBytes)}.",
                MovableKind.AppCache => $"Uses the tool's own setting for a new location. {to.Name} has {Sizes.Format(to.FreeBytes)} free.",
                _ => $"Copied, checked, then a link is left at the old place so apps still find it. Undo puts it back. {to.Name} has {Sizes.Format(to.FreeBytes)} free.",
            };
            yield return new Suggestion($"Move {m.Name} ({Sizes.Format(m.Bytes)}) to {to.Name}", reason, drive, to, m.Bytes, new[] { m });
        }
    }
}
