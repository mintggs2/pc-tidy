using System.Diagnostics;
using PcTidy.Catalog.Games;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Common;
using PcTidy.Safety;

namespace PcTidy.Features.Games.Cleanup;

/// <summary>
/// Cleanup tab: per-game logs, crash reports, download leftovers and shader caches (catalog
/// <see cref="GameCleanupTargets"/>), Unity Player.log files, and Steam's shader pre-cache sizes. Only the contents of
/// each folder are offered (the folder stays), never saves or settings, always "Check", quarantined so it can be undone.
/// </summary>
public sealed class GameCleanupScanner : IFeatureScanner
{
    public string FeatureId => GamesContext.FeatureId;
    public string Title => "Game cleanup";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            return Scan(GamesContext.From(ctx), progress, ct) with { Duration = sw.Elapsed, Depth = ctx.Depth };
        }, ct);

    internal static ScanResult Scan(GamesContext g, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var caches = new List<ScanItem>();
        var logs = new List<ScanItem>();
        progress?.Report(new ScanProgress("Looking for game logs and caches…", 0.1));

        foreach (var target in GameCleanupTargets.All)
            foreach (var folder in Resolve(g.Locations, target))
            {
                ct.ThrowIfCancellationRequested();
                if (!FolderSize.DirectoryExists(folder)) continue;
                var item = TargetItem(g, target, folder, ct);
                if (item is null) continue;
                (target.Kind == CleanupKind.ShaderCache ? caches : logs).Add(item);
            }

        progress?.Report(new ScanProgress("Looking for Unity game logs…", 0.5));
        var unity = UnityLogItems(g, ct).ToList();
        progress?.Report(new ScanProgress("Measuring Steam's shader caches…", 0.8));
        var steamShaders = SteamShaderCacheItems(g, ct).ToList();

        var groups = new List<ScanGroup>();
        if (logs.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "games-cleanup-logs", Title = "Game logs and crash reports", Badge = Badge.Check,
                Description = "Files games write while they run or crash. Only useful when you're reporting a bug. Saves, settings and replays are never touched.",
                Items = logs.OrderByDescending(i => i.SizeBytes).ToList(),
            });
        if (unity.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "games-cleanup-unity", Title = "Unity game logs (Player.log)", Badge = Badge.Check,
                Description = "Unity games write a fresh Player.log every time they start. Old ones are safe to clear; the game makes a new one next time.",
                Items = unity.OrderByDescending(i => i.SizeBytes).ToList(),
            });
        if (caches.Count > 0 || steamShaders.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "games-cleanup-shaders", Title = "Game shader caches", Badge = Badge.Check,
                Description = "Pre-built graphics. " + GameCleanupTargets.ShaderRebuildWarning + " Nothing here is ticked for you: only clear one if it's huge or a game shows graphics glitches.",
                Items = caches.Concat(steamShaders).OrderByDescending(i => i.SizeBytes).ToList(),
            });

        var notes = new List<string>();
        if (groups.Count == 0) notes.Add("No game logs or caches worth clearing were found.");
        notes.Add("Your graphics driver's own shader caches (NVIDIA, AMD, Intel, DirectX) are in Clean → GPU shader caches.");
        progress?.Report(new ScanProgress("Done", 1));
        return new ScanResult { FeatureId = GamesContext.FeatureId, Title = "Game cleanup", Groups = groups, Notes = notes };
    }

    internal static IEnumerable<string> Resolve(IKnownLocations k, GameCleanupTarget t) => t.Base switch
    {
        CleanupBase.LocalAppData => new[] { Path.Combine(k.LocalAppData, t.RelativePath) },
        CleanupBase.RoamingAppData => new[] { Path.Combine(k.RoamingAppData, t.RelativePath) },
        CleanupBase.LocalLow => new[] { Path.Combine(LocalLow(k), t.RelativePath) },
        CleanupBase.EachFixedDriveRoot => k.FixedDrives.Select(d => Path.Combine(d, t.RelativePath)),
        _ => Array.Empty<string>(),
    };

    private static string LocalLow(IKnownLocations k) => Path.Combine(k.UserProfile, "AppData", "LocalLow");

    /// <summary>One row per target folder; its actions quarantine each entry inside (never the folder itself).</summary>
    private static ScanItem? TargetItem(GamesContext g, GameCleanupTarget t, string folder, CancellationToken ct)
    {
        var entries = FolderSize.Children(g.Scan.Walker, folder, ct).Where(e => !e.IsReparsePoint && !e.IsCloudPlaceholder).ToList();
        if (entries.Count == 0) return null;
        var actions = new List<PlanItem>();
        long size = 0;
        int files = 0;
        var newest = DateTime.MinValue;
        foreach (var e in entries)
        {
            long bytes = e.Size;
            int count = 1;
            var written = e.LastWriteUtc;
            if (e.IsDirectory)
            {
                var totals = FolderSize.Measure(g.Scan.Walker, e.FullPath, ct);
                (bytes, count) = (totals.Bytes, totals.Files);
                if (totals.NewestWriteUtc > newest) newest = totals.NewestWriteUtc;
            }
            if (written > newest) newest = written;
            size += bytes;
            files += count;
            actions.Add(Plans.Quarantine(e.FullPath, e.IsDirectory, bytes, e.LastWriteUtc, $"Move {t.Game} {KindNoun(t.Kind)} \"{e.Name}\" to quarantine", t.CloseFirst));
        }
        var (allowed, refusal) = Plans.Vet(g.Safety, actions);
        return new ScanItem
        {
            Id = "games.cleanup." + t.Id + "." + folder.ToLowerInvariant(),
            Title = $"{t.Game}: {KindTitle(t.Kind)}",
            Detail = folder,
            Reason = GameCleanupTargets.ReasonFor(t.Kind, t.Game),
            Badge = Badge.Check,
            // Never pre-ticked, shader caches above all: rebuilding one can take up to an hour on the next launch.
            DefaultSelected = false,
            SizeBytes = allowed.Count == actions.Count ? size : allowed.Sum(a => a.ExpectedSize),
            FileCount = files,
            OwnerApp = t.Game,
            LastModifiedUtc = newest == DateTime.MinValue ? null : newest,
            RequiresClosedApps = t.CloseFirst,
            Details = Sources(t.SourceUrl, t.Evidence),
            Actions = allowed,
            DisabledReason = refusal,
        };
    }

    /// <summary>LocalLow\&lt;Company&gt;\&lt;Product&gt;\Player.log and Player-prev.log (Unity docs: where players write their log).</summary>
    private static IEnumerable<ScanItem> UnityLogItems(GamesContext g, CancellationToken ct)
    {
        var root = LocalLow(g.Locations);
        foreach (var company in FolderSize.Children(g.Scan.Walker, root, ct).Where(e => e.IsDirectory && !e.IsReparsePoint))
            foreach (var product in FolderSize.Children(g.Scan.Walker, company.FullPath, ct).Where(e => e.IsDirectory && !e.IsReparsePoint))
            {
                var logs = FolderSize.Children(g.Scan.Walker, product.FullPath, ct)
                    .Where(e => !e.IsDirectory && !e.IsReparsePoint && !e.IsCloudPlaceholder
                                && GameCleanupTargets.UnityLogNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (logs.Count == 0) continue;
                var game = product.Name;
                var actions = logs.Select(e => Plans.Quarantine(e.FullPath, false, e.Size, e.LastWriteUtc, $"Move {game}'s {e.Name} to quarantine")).ToList();
                var (allowed, refusal) = Plans.Vet(g.Safety, actions);
                yield return new ScanItem
                {
                    Id = "games.cleanup.unity." + product.FullPath.ToLowerInvariant(),
                    Title = $"{game}: Unity log",
                    Detail = product.FullPath,
                    Reason = $"The log {game} wrote last time it ran ({company.Name}). Only useful when reporting a bug; the game writes a new one when it starts.",
                    Badge = Badge.Check,
                    SizeBytes = allowed.Sum(a => a.ExpectedSize),
                    FileCount = logs.Count,
                    OwnerApp = game,
                    LastModifiedUtc = logs.Max(e => e.LastWriteUtc),
                    Details = Sources(GameCleanupTargets.UnityLogSource, Evidence.Verified),
                    Actions = allowed,
                    DisabledReason = refusal,
                };
            }
    }

    /// <summary>steamapps\shadercache\&lt;appid&gt;: inside the Steam library, so shown with its size only (Steam manages it).</summary>
    private static IEnumerable<ScanItem> SteamShaderCacheItems(GamesContext g, CancellationToken ct)
    {
        if (g.Locations.SteamLibraries.Count == 0) yield break;
        var names = new GameLibraryInfo(g.Locations).SteamApps.GroupBy(a => a.AppId).ToDictionary(x => x.Key, x => x.First().Name);
        foreach (var lib in g.Locations.SteamLibraries)
        {
            var cacheRoot = Path.Combine(lib, "steamapps", "shadercache");
            foreach (var app in FolderSize.Children(g.Scan.Walker, cacheRoot, ct).Where(e => e.IsDirectory && !e.IsReparsePoint))
            {
                var totals = FolderSize.Measure(g.Scan.Walker, app.FullPath, ct);
                if (totals.Bytes <= 0) continue;
                var game = names.TryGetValue(app.Name, out var n) ? n : $"Steam app {app.Name}";
                yield return new ScanItem
                {
                    Id = "games.cleanup.steamshader." + app.FullPath.ToLowerInvariant(),
                    Title = $"{game}: Steam shader pre-cache",
                    Detail = app.FullPath,
                    Reason = $"{Sizes.Format(totals.Bytes)} of pre-built shaders Steam keeps for {game}. It lives inside your Steam library, which PC Tidy never changes; Steam manages it under Settings → Downloads → Shader Pre-Caching. {GameCleanupTargets.ShaderRebuildWarning}",
                    Badge = Badge.Check,
                    SizeBytes = 0, // information only: nothing is freed from here
                    CurrentState = Sizes.Format(totals.Bytes),
                    FileCount = totals.Files,
                    OwnerApp = "Steam",
                    Details = Sources(GameCleanupTargets.SteamShaderCacheSource, Evidence.Verified),
                };
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Sources(string url, Evidence evidence) => new Dictionary<string, string>
    {
        ["Source"] = url,
        ["Evidence"] = evidence == Evidence.Verified
            ? "Documented by the vendor or PCGamingWiki."
            : "Community-reported folder. PC Tidy only offers it because it exists on this PC, and never ticks it for you.",
    };

    private static string KindTitle(CleanupKind k) => k switch
    {
        CleanupKind.Logs => "logs",
        CleanupKind.CrashReports => "crash reports",
        CleanupKind.ShaderCache => "shader cache",
        CleanupKind.DownloadCache => "download leftovers",
        CleanupKind.WebCache => "web cache",
        _ => "leftovers",
    };

    private static string KindNoun(CleanupKind k) => k switch
    {
        CleanupKind.Logs => "log",
        CleanupKind.CrashReports => "crash report",
        CleanupKind.ShaderCache => "shader cache file",
        CleanupKind.DownloadCache => "download",
        CleanupKind.WebCache => "web cache file",
        _ => "file",
    };
}
