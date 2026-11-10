using PcTidy.Core;
using PcTidy.Safety.Detection;

namespace PcTidy.Safety;

/// <summary>
/// Read-only view of installed launcher games (Steam app manifests, Epic manifests) for the Games, Apps and
/// Move-to-drive features. Never edits launcher files. Works the same on the real PC and in the sandbox.
/// </summary>
public sealed class GameLibraryInfo
{
    private readonly IKnownLocations _locations;
    private Lazy<IReadOnlyList<SteamApp>> _steam;
    private Lazy<IReadOnlyList<EpicApp>> _epic;

    public GameLibraryInfo(IKnownLocations locations)
    {
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        (_steam, _epic) = CreateLoaders();
    }

    /// <summary>Every Steam game across all libraries. Empty when Steam isn't installed.</summary>
    public IReadOnlyList<SteamApp> SteamApps => _steam.Value;

    /// <summary>Every Epic Games item with an install folder. Empty when the Epic launcher isn't installed.</summary>
    public IReadOnlyList<EpicApp> EpicApps => _epic.Value;

    /// <summary>Forgets the cached lists so the next read sees new installs.</summary>
    public void Refresh() => (_steam, _epic) = CreateLoaders();

    private (Lazy<IReadOnlyList<SteamApp>>, Lazy<IReadOnlyList<EpicApp>>) CreateLoaders() =>
    (
        new(() => DetectionUtil.SafeList(() => _locations.SteamLibraries.SelectMany(SteamConfig.ReadApps)
            .GroupBy(a => a.AppId).Select(g => g.First()))),
        new(() => DetectionUtil.SafeList(() => EpicManifests.Read(_locations.EpicManifestsDir)))
    );
}
