using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.Safety;
using PcTidy.Safety.Detection;

namespace PcTidy.Features.DriveMove.Games;

/// <summary>One installed game as this section sees it (read-only facts from the launcher's own files).</summary>
internal sealed record GameOnDrive(Launcher Launcher, string Id, string Name, string InstallDir, long Bytes, DateTime? LastPlayedUtc, DateTime? InstalledUtc)
{
    /// <summary>Last played, or installed when it was never played (Steam records both).</summary>
    public DateTime? LastUsedUtc => LastPlayedUtc ?? InstalledUtc;
}

/// <summary>
/// Games (Plan §8): Steam, Epic and Xbox app games listed per drive with size and last played. PC Tidy never copies a game
/// folder: the only actions open the launcher's own move screen (OpenExternal) with plain steps.
/// </summary>
internal static class GamePlanner
{
    /// <summary>Steam "apps" that aren't games (shared redistributables).</summary>
    private static readonly HashSet<string> SteamNonGames = new() { "228980" };

    public static Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!c.Drives.MovingAvailable) return Task.FromResult(PartResult.Empty with { Notes = new[] { c.Drives.UnavailableReason! } });
        progress?.Report(new ScanProgress("Reading your game launchers…", 0.1));
        var games = Detect(c, ct);
        if (games.Count == 0)
            return Task.FromResult(PartResult.Empty with { Notes = new[] { "No Steam, Epic or Xbox app games were found on this PC." } });

        var groups = new List<ScanGroup>();
        var launcherRows = new List<ScanItem>();
        foreach (var launcher in games.Select(g => g.Launcher).Distinct())
        {
            var info = Launchers.All[launcher];
            var count = games.Count(g => g.Launcher == launcher);
            var actions = info.MoveLink is { } link
                ? new[] { Plans.OpenExternal(link, $"Open {info.Name} to move games to another drive") }
                : Array.Empty<PlanItem>();
            launcherRows.Add(new ScanItem
            {
                Title = info.MoveLink is null ? $"How to move {info.Name} games" : $"Open {info.Name} to move games",
                Detail = $"{count} game{(count == 1 ? "" : "s")} installed",
                Reason = info.MoveSteps + " PC Tidy never copies game folders itself: the launcher keeps updates, saves and anti-cheat working.",
                Badge = Badge.Safe,
                Actions = actions,
            });
        }
        groups.Add(new ScanGroup
        {
            Id = DriveMoveIds.GroupGameLaunchers,
            Title = "Move games with their launcher",
            Description = "Ticking a row opens the launcher's own screen. Nothing on your PC changes until you click there.",
            Badge = Badge.Safe,
            Items = launcherRows,
        });

        var movables = new List<Movable>();
        foreach (var drive in c.Drives.All)
        {
            var here = games.Where(g => c.Drives.DriveOf(g.InstallDir)?.Root.Equals(drive.Root, StringComparison.OrdinalIgnoreCase) == true)
                .OrderByDescending(g => g.Bytes).ToList();
            if (here.Count == 0) continue;
            var cutoff = c.NowUtc.AddDays(-c.Thresholds.StaleGameDays);
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupGamesOnDrive + drive.Name.TrimEnd(':').ToLowerInvariant(),
                Title = $"Games on {drive.Name} ({Sizes.Format(here.Sum(g => g.Bytes))})",
                Description = $"{drive.Name} has {Sizes.Format(drive.FreeBytes)} free. Shown so you can pick which games to move in the launcher.",
                Badge = Badge.Safe,
                Items = here.Select(g => Row(g, cutoff, c.NowUtc)).ToList(),
            });
            foreach (var g in here)
            {
                var link = Launchers.All[g.Launcher].MoveLink;
                var actions = link is null ? Array.Empty<PlanItem>()
                    : new[] { Plans.OpenExternal(link, $"Open {Launchers.All[g.Launcher].Name} to move games to another drive") };
                movables.Add(new Movable(KindOf(g.Launcher), g.Name, g.InstallDir, g.Bytes, g.LastUsedUtc, actions));
            }
        }
        return Task.FromResult(new PartResult(groups, movables, Array.Empty<string>()));
    }

    private static ScanItem Row(GameOnDrive g, DateTime cutoff, DateTime now)
    {
        var played = g.LastPlayedUtc is { } p ? $"last played {Ago(p, now)}"
            : g.InstalledUtc is { } i ? $"not played since it was installed {Ago(i, now)}" : "last played: unknown";
        var stale = g.LastUsedUtc is { } used && used < cutoff;
        return new ScanItem
        {
            Title = g.Name,
            Detail = g.InstallDir,
            Reason = (stale ? "Not played in 6 months: a good one to move. " : "") + $"Move it with {Launchers.All[g.Launcher].Name}'s own Move (see the row above).",
            Badge = Badge.Safe,
            // Shown for information only (no actions), so it carries no size in the group totals' sense of "will move".
            CurrentState = $"{Sizes.Format(g.Bytes)} · {played}",
            LastModifiedUtc = g.LastPlayedUtc,
            OwnerApp = Launchers.All[g.Launcher].Name,
        };
    }

    private static MovableKind KindOf(Launcher l) => l switch
    {
        Launcher.Steam => MovableKind.SteamGame,
        Launcher.Epic => MovableKind.EpicGame,
        _ => MovableKind.XboxGame,
    };

    internal static string Ago(DateTime then, DateTime now)
    {
        var days = (now - then).TotalDays;
        if (days < 1) return "today";
        if (days < 2) return "yesterday";
        if (days < 45) return $"{(int)days} days ago";
        if (days < 365 * 1.5) return $"{(int)Math.Round(days / 30.4)} months ago";
        return $"{(int)Math.Round(days / 365.25)} years ago";
    }

    /// <summary>Steam (app manifests), Epic (.item manifests) and Xbox app games (&lt;drive&gt;\XboxGames), read-only.</summary>
    internal static IReadOnlyList<GameOnDrive> Detect(DriveMoveContext c, CancellationToken ct)
    {
        var list = new List<GameOnDrive>();
        var library = new GameLibraryInfo(c.Locations);
        foreach (var app in library.SteamApps)
        {
            if (SteamNonGames.Contains(app.AppId) || !app.IsFullyInstalled) continue;
            var acf = Vdf.TryLoad(Path.Combine(app.LibraryPath, "steamapps", $"appmanifest_{app.AppId}.acf"))?["AppState"];
            var updated = acf?.GetLong("LastUpdated");
            list.Add(new GameOnDrive(Launcher.Steam, app.AppId, app.Name, app.InstallPath, app.SizeOnDisk, app.LastPlayedUtc,
                updated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated.Value).UtcDateTime : null));
        }
        ct.ThrowIfCancellationRequested();
        foreach (var e in library.EpicApps.Where(e => !e.IsIncomplete))
            list.Add(new GameOnDrive(Launcher.Epic, e.AppName, e.DisplayName, e.InstallLocation, e.InstallSize, null, null));
        foreach (var drive in c.Drives.All)
        {
            foreach (var folder in FolderMeasure.SubFolders(Path.Combine(drive.Root, "XboxGames")))
            {
                ct.ThrowIfCancellationRequested();
                var content = Path.Combine(folder, "Content");
                if (!FolderMeasure.FileExists(Path.Combine(content, "MicrosoftGame.config"))) continue;
                var totals = FolderMeasure.Measure(c.Walker, folder, ct);
                list.Add(new GameOnDrive(Launcher.Xbox, Path.GetFileName(folder), Path.GetFileName(folder), folder, totals.Bytes, null, null));
            }
        }
        return list
            .Where(g => !string.IsNullOrWhiteSpace(g.InstallDir) && FolderMeasure.DirectoryExists(g.InstallDir))
            .GroupBy(g => g.InstallDir.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
