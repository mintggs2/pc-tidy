using PcTidy.Core;
using PcTidy.Safety.Detection;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>
/// Steam and Epic games, read from the launchers' own manifests (Steam appmanifest_*.acf, Epic *.item).
/// Missing launchers simply give no games. Games are only ever removed through their launcher.
/// </summary>
public static class LauncherGamesReader
{
    public static IReadOnlyList<InstalledApp> Read(IKnownLocations locations, CancellationToken ct)
    {
        var games = new List<InstalledApp>();
        foreach (var library in locations.SteamLibraries)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<SteamApp> apps;
            try { apps = SteamConfig.ReadApps(library).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var app in apps)
            {
                // Steam's own runtime pieces (Steamworks redistributables, Proton...) aren't games a person installed.
                if (app.AppId == "228980") continue;
                games.Add(new InstalledApp
                {
                    Id = "steam:" + app.AppId,
                    Name = app.Name,
                    Source = AppSource.Steam,
                    Scope = AppScope.Machine,
                    Publisher = "Steam",
                    InstallLocation = app.InstallPath,
                    ReportedSizeBytes = app.SizeOnDisk > 0 ? app.SizeOnDisk : null,
                    LastUsedUtc = app.LastPlayedUtc,
                    LastUsedSource = app.LastPlayedUtc is null ? LastUsedSource.Unknown : LastUsedSource.Steam,
                    LauncherId = app.AppId,
                });
            }
        }

        IEnumerable<EpicApp> epic;
        try { epic = EpicManifests.Read(locations.EpicManifestsDir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { epic = Array.Empty<EpicApp>(); }
        foreach (var app in epic)
        {
            if (app.IsIncomplete) continue;
            games.Add(new InstalledApp
            {
                Id = "epic:" + app.AppName,
                Name = app.DisplayName,
                Source = AppSource.Epic,
                Scope = AppScope.Machine,
                Publisher = "Epic Games",
                InstallLocation = app.InstallLocation,
                ReportedSizeBytes = app.InstallSize > 0 ? app.InstallSize : null,
                LauncherId = app.AppName,
            });
        }
        return games;
    }
}
