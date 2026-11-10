using System.Diagnostics;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;

namespace PcTidy.Features.Games.Library;

public enum LibrarySort { Size, LastPlayed }

/// <summary>
/// Library tab: every installed game by size or last played, "not played in N months" suggestions, and "move to
/// another drive" suggestions. Actions only ever open the launcher's own uninstall or move screen (OpenExternal);
/// PC Tidy never copies, moves or deletes a game folder.
/// </summary>
public sealed class LibraryScanner : IFeatureScanner
{
    /// <summary>Games on the system drive bigger than this get a "move to another drive" suggestion (when one exists).</summary>
    private const long MoveSuggestionBytes = 10L << 30;

    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Game library";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    /// <summary>Games found by the last scan (the page re-sorts them without scanning again).</summary>
    public IReadOnlyList<InstalledGame> LastGames { get; private set; } = Array.Empty<InstalledGame>();

    private GamesContext? _last;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var g = GamesContext.From(ctx);
            progress?.Report(new ScanProgress("Reading your game launchers…", 0.1));
            var games = GameLibraryDetector.Detect(g, ct);
            LastGames = games;
            _last = g;
            progress?.Report(new ScanProgress($"{games.Count} games found", 1, games.Count, games.Sum(x => x.SizeBytes)));
            return Build(g, games, LibrarySort.Size) with { Duration = sw.Elapsed, Depth = ctx.Depth };
        }, ct);

    /// <summary>Same results, re-sorted (no new scan).</summary>
    public ScanResult? Resort(LibrarySort sort) => _last is null ? null : Build(_last, LastGames, sort);

    internal static ScanResult Build(GamesContext g, IReadOnlyList<InstalledGame> games, LibrarySort sort)
    {
        var notes = new List<string>();
        if (games.Count == 0)
            notes.Add(g.Locations.SteamRoot is null && g.Locations.EpicManifestsDir is null
                ? "No game launchers were found on this PC, so there's nothing to show here."
                : "Your game launchers are installed, but no installed games were found.");

        var ordered = Order(games, sort).ToList();
        var groups = new List<ScanGroup>();

        var unplayed = ordered.Select(x => (Game: x, Why: UnplayedReason(g, x))).Where(x => x.Why is not null).ToList();
        if (unplayed.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "games-library-unplayed",
                Title = $"Not played in {g.UnplayedMonths}+ months",
                Description = "Uninstall them through their own launcher to free space. Saves synced to the cloud stay, and you can reinstall any time.",
                Badge = Badge.Check,
                Items = unplayed.Select(x => UninstallItem(g, x.Game, x.Why!)).ToList(),
            });

        var systemDrive = DriveOf(g, g.Locations.SystemDrive);
        var otherDrives = g.Locations.FixedDrives.Where(d => !string.Equals(d, systemDrive, StringComparison.OrdinalIgnoreCase)).ToList();
        if (otherDrives.Count > 0)
        {
            var movable = ordered.Where(x => string.Equals(DriveOf(g, x.InstallDir), systemDrive, StringComparison.OrdinalIgnoreCase) && x.SizeBytes >= MoveSuggestionBytes).ToList();
            if (movable.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = "games-library-move",
                    Title = "Big games on your system drive",
                    Description = "Moving them with the launcher's own Move frees space on your Windows drive. PC Tidy opens the right screen and tells you what to click.",
                    Badge = Badge.Check,
                    Items = movable.Select(x => MoveItem(g, x)).ToList(),
                });
        }

        if (ordered.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "games-library-all",
                Title = sort == LibrarySort.Size ? "All your games, biggest first" : "All your games, least recently played first",
                Description = "Shown so you know where the space goes. Nothing here changes anything.",
                Badge = Badge.Safe,
                Items = ordered.Select(x => InfoItem(g, x)).ToList(),
            });

        return new ScanResult { FeatureId = GamesContext.FeatureId, Title = "Game library", Groups = groups, Notes = notes };
    }

    private static IEnumerable<InstalledGame> Order(IEnumerable<InstalledGame> games, LibrarySort sort) => sort == LibrarySort.Size
        ? games.OrderByDescending(x => x.SizeBytes).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        // Never-played games sort first, then the oldest "last played".
        : games.OrderBy(x => x.LastPlayedUtc ?? DateTime.MinValue).ThenByDescending(x => x.SizeBytes);

    /// <summary>Why the game is suggested for uninstall, or null. Only launchers that record play time can say.</summary>
    internal static string? UnplayedReason(GamesContext g, InstalledGame game)
    {
        if (!game.FullyInstalled) return null;
        var cutoff = g.NowUtc.AddDays(-30.44 * g.UnplayedMonths);
        if (game.LastPlayedUtc is { } played)
            return played < cutoff ? $"Last played {Ages.Since(played, g.NowUtc)} ({Ages.Date(played)})." : null;
        if (game.InstalledUtc is { } installed && installed < cutoff)
            return $"Never played since it was installed or updated {Ages.Since(installed, g.NowUtc)}.";
        return null;
    }

    private static ScanItem UninstallItem(GamesContext g, InstalledGame game, string why)
    {
        var info = Launchers.All[game.Launcher];
        var actions = new List<PlanItem>();
        if (info.UninstallLink is { } link)
            actions.Add(Plans.OpenExternal(Launchers.Link(link, game.Id), $"Open {info.Name} to uninstall \"{game.Name}\""));
        var (allowed, refusal) = Plans.Vet(g.Safety, actions);
        return new ScanItem
        {
            Id = $"games.library.uninstall.{game.Key}",
            Title = game.Name,
            Detail = $"{Sizes.Format(game.SizeBytes)} · {info.Name} · {game.InstallDir}",
            // What it frees comes first: the list shows one line of the reason, so the size must not be the part that gets cut.
            Reason = $"Uninstalling it through {info.Name} frees {Sizes.Format(game.SizeBytes)}. {why} {info.UninstallSteps}",
            Badge = Badge.Check,
            // PC Tidy itself frees nothing here (the launcher does), so the row adds nothing to "selected" totals.
            OwnerApp = info.Name,
            CurrentState = game.LastPlayedUtc is { } p ? "Played " + Ages.Since(p, g.NowUtc) : "Never played",
            Details = Facts(g, game, ("How", info.UninstallSteps)),
            Actions = allowed,
            DisabledReason = actions.Count > 0 ? refusal : null,
        };
    }

    private static ScanItem MoveItem(GamesContext g, InstalledGame game)
    {
        var info = Launchers.All[game.Launcher];
        var actions = new List<PlanItem>();
        if (info.MoveLink is { } link)
            actions.Add(Plans.OpenExternal(Launchers.Link(link, game.Id), $"Open {info.Name} to move \"{game.Name}\" to another drive"));
        var (allowed, refusal) = Plans.Vet(g.Safety, actions);
        return new ScanItem
        {
            Id = $"games.library.move.{game.Key}",
            Title = $"Move {game.Name}",
            Detail = $"{Sizes.Format(game.SizeBytes)} on {DriveOf(g, game.InstallDir)} · {info.Name}",
            Reason = $"Frees {Sizes.Format(game.SizeBytes)} on your Windows drive. {info.MoveSteps}",
            Badge = Badge.Check,
            // The launcher moves it; nothing is freed by PC Tidy, so no bytes are counted.
            OwnerApp = info.Name,
            Details = Facts(g, game, ("How", info.MoveSteps)),
            Actions = allowed,
            DisabledReason = actions.Count > 0 ? refusal : null,
        };
    }

    private static ScanItem InfoItem(GamesContext g, InstalledGame game)
    {
        var info = Launchers.All[game.Launcher];
        var played = game.LastPlayedUtc is { } p ? "played " + Ages.Since(p, g.NowUtc)
            : game.Launcher == Launcher.Steam ? "never played" : "play time not recorded by " + info.Name;
        return new ScanItem
        {
            Id = $"games.library.info.{game.Key}",
            Title = game.Name,
            Detail = $"{info.Name} · {game.InstallDir}",
            Reason = $"{Sizes.Format(game.SizeBytes)}, {played}.{(game.FullyInstalled ? "" : " The launcher is still installing or updating it.")}",
            Badge = Badge.Safe,
            SizeBytes = game.SizeBytes,
            OwnerApp = info.Name,
            CurrentState = played,
            Details = Facts(g, game, ("Uninstall", info.UninstallSteps), ("Move", info.MoveSteps)),
        };
    }

    private static IReadOnlyDictionary<string, string> Facts(GamesContext g, InstalledGame game, params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string>
        {
            ["Launcher"] = Launchers.All[game.Launcher].Name,
            ["Folder"] = game.InstallDir,
            ["Last played"] = game.LastPlayedUtc is { } p ? $"{Ages.Date(p)} ({Ages.Since(p, g.NowUtc)})"
                : game.Launcher == Launcher.Steam ? "Never" : "Not recorded by this launcher",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    /// <summary>The fixed drive holding a path (works for the sandbox's fake drives too).</summary>
    internal static string? DriveOf(GamesContext g, string path)
        => g.Locations.FixedDrives.Where(d => PathUtil.IsUnder(path, d)).OrderByDescending(d => d.Length).FirstOrDefault();
}
