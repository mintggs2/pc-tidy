using PcTidy.Safety;

namespace PcTidy.Features.Boost.Games;

/// <summary>
/// One installed game Game boost can watch for. <see cref="Key"/> uses the same "launcher:id" form as the Games
/// library ("steam:730", "epic:fortnite"), so settings stay stable across scans.
/// </summary>
/// <param name="SteamAppId">Steam app id (matched against Steam's RunningAppID), null for other launchers.</param>
/// <param name="ProcessNames">Exe names (no ".exe") the launcher lists for it (Epic ProcessNames + LaunchExecutable).</param>
public sealed record BoostGame(string Key, string Name, string Launcher, string InstallDir, string? SteamAppId, IReadOnlyList<string> ProcessNames);

/// <summary>Where Game boost gets the list of installed games.</summary>
public interface IBoostGameSource
{
    IReadOnlyList<BoostGame> Games();
}

/// <summary>
/// Steam (steamapps\common\&lt;installdir&gt; from each appmanifest) and Epic (.item manifests: InstallLocation,
/// LaunchExecutable, ProcessNames), read-only through <see cref="GameLibraryInfo"/>. Missing launchers give nothing.
/// </summary>
public sealed class LauncherGameSource : IBoostGameSource
{
    /// <summary>Steam "apps" that aren't games (shared redistributables).</summary>
    private static readonly HashSet<string> SteamNonGames = new() { "228980" };

    private readonly GameLibraryInfo _library;

    public LauncherGameSource(IKnownLocations locations) => _library = new GameLibraryInfo(locations);

    public IReadOnlyList<BoostGame> Games()
    {
        _library.Refresh();
        var games = new List<BoostGame>();
        foreach (var app in _library.SteamApps)
        {
            if (SteamNonGames.Contains(app.AppId)) continue;
            games.Add(new BoostGame($"steam:{app.AppId}".ToLowerInvariant(), app.Name, "Steam", app.InstallPath, app.AppId, Array.Empty<string>()));
        }
        foreach (var e in _library.EpicApps)
        {
            var names = e.ProcessNames
                .Concat(e.LaunchExecutable is { } exe ? new[] { exe } : Array.Empty<string>())
                .Select(p => Path.GetFileNameWithoutExtension(p.Replace('/', '\\')))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            games.Add(new BoostGame($"epic:{e.AppName}".ToLowerInvariant(), e.DisplayName, "Epic Games", e.InstallLocation, null, names));
        }
        return games
            .GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
