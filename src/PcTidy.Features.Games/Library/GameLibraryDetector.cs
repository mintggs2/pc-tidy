using System.Xml;
using PcTidy.Catalog.Games;
using PcTidy.Features.Games.Common;
using PcTidy.Safety;
using PcTidy.Safety.Detection;

namespace PcTidy.Features.Games.Library;

/// <summary>
/// Finds installed games across every launcher and drive, read-only: Steam (libraryfolders.vdf + appmanifest_*.acf),
/// Epic (.item manifests), GOG / EA / Ubisoft / Battle.net (their registry entries), Xbox / Store games
/// (PackageManager + &lt;drive&gt;\XboxGames). Launchers that aren't installed contribute nothing.
/// </summary>
internal static class GameLibraryDetector
{
    /// <summary>Steam "apps" that aren't games (shared redistributables).</summary>
    private static readonly HashSet<string> SteamNonGames = new() { "228980" };

    public static IReadOnlyList<InstalledGame> Detect(GamesContext g, CancellationToken ct)
    {
        var games = new List<InstalledGame>();
        var library = new GameLibraryInfo(g.Locations);
        games.AddRange(Steam(library));
        ct.ThrowIfCancellationRequested();
        games.AddRange(library.EpicApps.Select(e => new InstalledGame
        {
            Launcher = Launcher.Epic, Id = e.AppName, Name = e.DisplayName, InstallDir = e.InstallLocation,
            SizeBytes = e.InstallSize, FullyInstalled = !e.IsIncomplete,
            ProcessNames = e.ProcessNames.Concat(e.LaunchExecutable is { } exe ? new[] { exe } : Array.Empty<string>())
                .Select(p => Path.GetFileNameWithoutExtension(p.Replace('/', '\\'))).Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }));
        foreach (var r in Safe(() => g.Environment.RegistryGames()))
            games.Add(new InstalledGame { Launcher = r.Launcher, Id = r.Id ?? r.Name, Name = r.Name, InstallDir = r.InstallDir });
        games.AddRange(Xbox(g));
        ct.ThrowIfCancellationRequested();

        // One entry per folder: the first launcher that claims it wins (Steam/Epic manifests are the most precise).
        var unique = games
            .Where(x => !string.IsNullOrWhiteSpace(x.InstallDir) && FolderSize.DirectoryExists(x.InstallDir))
            .GroupBy(x => x.InstallDir.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        // Launchers that don't record a size: measure the folder (read-only listing).
        for (int i = 0; i < unique.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (unique[i].SizeBytes > 0) continue;
            var totals = FolderSize.Measure(g.Scan.Walker, unique[i].InstallDir, ct);
            unique[i] = unique[i] with { SizeBytes = totals.Bytes };
        }
        return unique;
    }

    private static IEnumerable<InstalledGame> Steam(GameLibraryInfo library)
    {
        foreach (var app in library.SteamApps)
        {
            if (SteamNonGames.Contains(app.AppId)) continue;
            var acf = Vdf.TryLoad(Path.Combine(app.LibraryPath, "steamapps", $"appmanifest_{app.AppId}.acf"))?["AppState"];
            var updated = acf?.GetLong("LastUpdated");
            yield return new InstalledGame
            {
                Launcher = Launcher.Steam, Id = app.AppId, Name = app.Name, InstallDir = app.InstallPath, LibraryPath = app.LibraryPath,
                SizeBytes = app.SizeOnDisk, LastPlayedUtc = app.LastPlayedUtc,
                InstalledUtc = updated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated.Value).UtcDateTime : null,
                FullyInstalled = app.IsFullyInstalled,
            };
        }
    }

    /// <summary>Xbox app games: PackageManager on the real PC, plus &lt;drive&gt;\XboxGames\&lt;game&gt;\Content\MicrosoftGame.config on any drive.</summary>
    private static IEnumerable<InstalledGame> Xbox(GamesContext g)
    {
        foreach (var s in Safe(() => g.Environment.StoreGames()))
            yield return new InstalledGame { Launcher = Launcher.Xbox, Id = s.PackageFamilyName, Name = s.Name, InstallDir = s.InstallDir };

        foreach (var drive in g.Locations.FixedDrives)
        {
            var root = Path.Combine(drive, "XboxGames");
            string[] folders;
            try { folders = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
            {
                var content = Path.Combine(folder, "Content");
                var config = Path.Combine(content, "MicrosoftGame.config");
                if (!FolderSize.FileExists(config)) continue;
                var (name, identity) = ReadGameConfig(config);
                yield return new InstalledGame
                {
                    Launcher = Launcher.Xbox, Id = identity ?? Path.GetFileName(folder),
                    Name = name ?? Path.GetFileName(folder), InstallDir = content,
                };
            }
        }
    }

    /// <summary>Display name and identity from MicrosoftGame.config (DTDs and external entities refused).</summary>
    internal static (string? Name, string? Identity) ReadGameConfig(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            string? name = null, identity = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "Identity") identity ??= reader.GetAttribute("Name");
                if (reader.LocalName == "ShellVisuals")
                {
                    var display = reader.GetAttribute("DefaultDisplayName");
                    // "ms-resource:" names need the package's resource table: fall back to the folder name.
                    if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name ??= display;
                }
            }
            return (name, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return (null, null); }
    }

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> read)
    {
        try { return read(); } catch (Exception) { return Array.Empty<T>(); }
    }
}
