using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>
/// §7 #15: Discord (+ PTB / Canary) media caches only. Local Storage and IndexedDB hold the login and are never listed.
/// </summary>
internal sealed class DiscordTarget : ICleanupTarget
{
    private static readonly (string Folder, string Name, string Process)[] Variants =
    {
        ("discord", "Discord", RunningApps.Discord),
        ("discordptb", "Discord PTB", RunningApps.DiscordPtb),
        ("discordcanary", "Discord Canary", RunningApps.DiscordCanary),
    };
    private static readonly string[] CacheFolders = { Path.Combine("Cache", "Cache_Data"), "Code Cache", "GPUCache" };

    public CleanupTargetInfo Info => CleanupTargets.Discord;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var installed = Variants
            .Select(v => (v.Name, v.Process, Root: Path.Combine(s.Locations.RoamingAppData, v.Folder)))
            .Where(v => Directory.Exists(v.Root))
            .ToList();
        if (installed.Count == 0) return null;

        return installed.Select(v => ItemFactory.Quarantine(s, Info, new ItemSpec
        {
            Title = $"{v.Name} cache",
            Detail = v.Root,
            Apps = new[] { v.Process },
            OwnerApp = v.Name,
        }, ChromiumCacheTarget.CachePicks(s, v.Root, CacheFolders, ct))).Present();
    }
}

/// <summary>
/// §7 #16: Steam's web cache, crash dumps and logs 30+ days old. Roots come from the carve-out table.
/// steamapps (games), userdata (saves) and config are never listed.
/// </summary>
internal sealed class SteamTarget : ICleanupTarget
{
    private const int LogMinAgeDays = 30;

    public CleanupTargetInfo Info => CleanupTargets.Steam;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var picks = new List<Pick>();
        var anyPresent = false;
        foreach (var root in CarveOutRoots.Of(CarveOuts.SteamCache, s.Locations))
        {
            var tree = s.Tree(root, ct);
            if (tree is null) continue;
            anyPresent = true;
            var isLogs = Path.GetFileName(root).Equals("logs", StringComparison.OrdinalIgnoreCase);
            picks.AddRange(TreePlanner.Plan(tree, e => !isLogs || s.IsOlderThan(e, LogMinAgeDays), MixedFolders.Descend));
        }
        if (!anyPresent) return null;
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = Info.Title,
                Detail = s.Locations.SteamRoot ?? Path.Combine(s.Locations.LocalAppData, "Steam"),
                Apps = new[] { RunningApps.Steam },
                OwnerApp = "Steam",
            }, picks),
        }.Present();
    }
}

/// <summary>
/// §7 #17: Spotify. Size only: Spotify keeps offline downloads next to its cache, so PC Tidy points to
/// Spotify's own "Clear cache" button instead of removing anything itself.
/// </summary>
internal sealed class SpotifyTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.Spotify;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var k = s.Locations;
        var roots = new List<string>
        {
            Path.Combine(k.LocalAppData, "Spotify", "Data"),
            Path.Combine(k.LocalAppData, "Spotify", "Storage"),
        };
        // Microsoft Store version: the package folder name carries the publisher id, so find it instead of hard-coding it.
        roots.AddRange(s.ChildFolders(Path.Combine(k.LocalAppData, "Packages"), ct)
            .Where(d => d.Name.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d.FullPath, "LocalCache", "Spotify", "Data")));

        var measured = roots.Select(r => (Root: r, M: s.Measure(r, ct))).Where(x => x.M.Exists).ToList();
        if (measured.Count == 0) return null;
        var bytes = measured.Sum(x => x.M.Bytes);
        var files = measured.Sum(x => x.M.Files);
        return new[]
        {
            ItemFactory.Info(s, Info, new ItemSpec
            {
                Title = Info.Title,
                Detail = measured[0].Root,
                Apps = new[] { RunningApps.Spotify },
                OwnerApp = "Spotify",
                Reason = CleanupTargets.SpotifyReason + " In Spotify: Settings > Storage > Clear cache.",
            }, bytes, files, Sizes.Format(bytes), "Use Spotify's own Clear cache button (Settings > Storage) so your offline songs are kept."),
        };
    }
}

/// <summary>§7 #18: Telegram media cache (tdata\user_data). The rest of tdata is your login and is never listed.</summary>
internal sealed class TelegramTarget : ICleanupTarget
{
    public CleanupTargetInfo Info => CleanupTargets.Telegram;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var tdata = Path.Combine(s.Locations.RoamingAppData, "Telegram Desktop", "tdata");
        if (!Directory.Exists(tdata)) return null;

        // Quick: the main account's cache. Deep: also extra accounts' caches (user_data#2, #3...).
        var folders = s.ChildFolders(tdata, ct)
            .Where(d => d.Name.Equals("user_data", StringComparison.OrdinalIgnoreCase)
                        || (s.IsDeep && d.Name.StartsWith("user_data#", StringComparison.OrdinalIgnoreCase)));
        var picks = new List<Pick>();
        foreach (var folder in folders)
        {
            var tree = s.Tree(folder.FullPath, ct);
            if (tree is null) continue;
            var extraAccount = !folder.Name.Equals("user_data", StringComparison.OrdinalIgnoreCase);
            picks.AddRange(TreePlanner.Plan(tree, _ => true, MixedFolders.Descend).Select(p => p with { DeepOnly = extraAccount }));
        }
        return new[]
        {
            ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = Info.Title,
                Detail = Path.Combine(tdata, "user_data"),
                Apps = new[] { RunningApps.Telegram },
                OwnerApp = "Telegram",
                DeepLabel = "other Telegram accounts' media caches",
            }, picks),
        }.Present();
    }
}
