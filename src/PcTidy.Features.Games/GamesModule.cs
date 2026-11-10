using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.UI;

namespace PcTidy.Features.Games;

/// <summary>
/// "Games" in the left rail (Speed up group, v2): library manager, clips &amp; screenshots, per-game optimizer and
/// per-game cleanup, on its own tabbed page. Every change goes through the shell's confirm (IFlowHost).
/// </summary>
public sealed class GamesModule : IFeatureModule
{
    public string Id => "games";
    public string Title => "Games";
    public string Summary => "Your game library by size and last played, old and duplicate clips, proven per-game settings with their sources, and game logs and caches.";
    public NavGroup Group => NavGroup.SpeedUp;
    public string Icon => "Games24";
    public int Order => 60;
    public Release Release => Release.V2;
    public IReadOnlyList<IFeatureScanner> Scanners { get; } = new IFeatureScanner[] { new GamesScanner() };

    public object? CreatePage(IServiceProvider services) => new GamesPage(services);
}

/// <summary>Is there anything for the Games section on this PC? (Any launcher, game folder or capture folder.)</summary>
public static class GamesAvailability
{
    public static bool HasLaunchers(IKnownLocations k)
    {
        if (k.SteamRoot is { } steam && Exists(steam)) return true;
        if (k.EpicManifestsDir is { } epic && Exists(epic)) return true;
        if (k.GameInstallDirs.Any(Exists)) return true;
        // An Xbox app game always ships Content\MicrosoftGame.config; an empty or leftover XboxGames folder doesn't count.
        return k.FixedDrives.Any(d => XboxGameFolders(Path.Combine(d, "XboxGames"))
            .Any(g => File.Exists(Path.Combine(g, "Content", "MicrosoftGame.config"))));
    }

    private static IEnumerable<string> XboxGameFolders(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool Exists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
