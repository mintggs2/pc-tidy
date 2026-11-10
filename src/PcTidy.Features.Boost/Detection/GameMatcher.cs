using PcTidy.Catalog.Games;
using PcTidy.Features.Boost.Games;

namespace PcTidy.Features.Boost.Detection;

/// <summary>A game the user enabled is running.</summary>
/// <param name="Processes">The game's own processes (never closed, never opened again).</param>
/// <param name="How">Plain-English reason it was recognised (for the status line).</param>
public sealed record GameMatch(BoostGame Game, IReadOnlyList<ObservedProcess> Processes, string How);

/// <summary>
/// Decides whether one of the enabled games is running, from a process snapshot and Steam's RunningAppID.
/// Pure logic (no system access), so it's tested with fake process lists and fake launcher manifests.
/// </summary>
public static class GameMatcher
{
    /// <summary>
    /// First enabled game (in the given order) with a matching process:
    /// an exe inside its install folder (Steam steamapps\common\&lt;game&gt;, Epic InstallLocation) that isn't a crash
    /// reporter, installer or anti-cheat; for Epic, a process named in its manifest whose path couldn't be read;
    /// for Steam, RunningAppID equal to its app id while the Steam client runs.
    /// </summary>
    public static GameMatch? Match(IReadOnlyList<BoostGame> enabledGames, IReadOnlyList<ObservedProcess> processes, int? steamRunningAppId)
    {
        var steamClientRunning = processes.Any(p => p.Name.Equals("steam", StringComparison.OrdinalIgnoreCase));
        foreach (var game in enabledGames)
        {
            var own = processes.Where(p => IsGameProcess(game, p)).ToList();
            if (own.Count > 0) return new GameMatch(game, own, $"{Path.GetFileName(own[0].ImagePath) ?? own[0].Name + ".exe"} is running");
            if (steamClientRunning && steamRunningAppId is > 0 && game.SteamAppId is { } id
                && int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var appId)
                && appId == steamRunningAppId)
                return new GameMatch(game, Array.Empty<ObservedProcess>(), "Steam says it's running");
        }
        return null;
    }

    /// <summary>Is this process part of the game (so it must never be closed)?</summary>
    public static bool IsGameProcess(BoostGame game, ObservedProcess p)
    {
        if (!p.Inspected || BoostDenyList.NeverClose(p.Name) || IsHelper(p.Name)) return false;
        if (p.ImagePath is { } path)
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsUnder(path, game.InstallDir);
        // The path couldn't be read (some games run protected): fall back to the names the launcher lists.
        return game.ProcessNames.Any(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Crash reporters, installers, redistributables and anti-cheat bootstrappers are never "the game".</summary>
    public static bool IsHelper(string processName) => Wildcard.MatchesAny(BoostDenyList.Normalize(processName), HelperExecutables.Patterns);

    internal static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try { return PathUtil.IsUnder(path, folder) && !PathUtil.Normalize(path).Equals(PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
