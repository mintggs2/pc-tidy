namespace PcTidy.Catalog.Games;

public enum Launcher { Steam, Epic, Gog, Ea, Ubisoft, BattleNet, Xbox }

/// <summary>
/// How each launcher uninstalls and moves its own games (Research.md §7 "Games"). PC Tidy never copies, moves or deletes
/// a game folder itself: it opens the launcher's own screen (OpenExternal) and explains the clicks.
/// A null link means "information only": PC Tidy just shows the steps.
/// </summary>
/// <param name="UninstallLink">Link for one game; "{id}" is replaced by the launcher's game id.</param>
public sealed record LauncherInfo(
    Launcher Launcher,
    string Name,
    string? UninstallLink,
    string UninstallSteps,
    string? MoveLink,
    string MoveSteps,
    bool LinkVerified);

public static class Launchers
{
    public static readonly IReadOnlyDictionary<Launcher, LauncherInfo> All = new Dictionary<Launcher, LauncherInfo>
    {
        [Launcher.Steam] = new(Launcher.Steam, "Steam",
            "steam://uninstall/{id}",
            "Steam asks you to confirm the uninstall. Your Steam Cloud saves stay; reinstall any time from your library.",
            "steam://open/settings",
            "In Steam's settings click Storage, pick the drive the game is on, tick the game, then click Move and pick the other drive. (Add the drive first with \"Add Drive\" if it isn't listed.)",
            LinkVerified: true),
        [Launcher.Epic] = new(Launcher.Epic, "Epic Games Launcher",
            "com.epicgames.launcher://store",
            "In the Epic Games Launcher open Library, click ⋯ on the game, then Uninstall.",
            "com.epicgames.launcher://store",
            "Epic's own steps: copy the game folder to the new drive, uninstall it in the launcher, then click Install, pick the folder that now holds the copy, and the launcher checks the files instead of downloading them again.",
            LinkVerified: false),
        [Launcher.Gog] = new(Launcher.Gog, "GOG GALAXY",
            "goggalaxy://openGameView/{id}",
            "GOG GALAXY opens the game. Click the settings icon next to Play → Manage installation → Uninstall.",
            "goggalaxy://openGameView/{id}",
            "GOG GALAXY: settings icon next to Play → Manage installation → Move to, then pick the new folder.",
            LinkVerified: false),
        [Launcher.Ea] = new(Launcher.Ea, "EA app",
            null,
            "Open the EA app → Library → ⋯ on the game → Uninstall.",
            null,
            "Open the EA app → Library → ⋯ on the game → Move (or uninstall and reinstall to the new install location in EA app settings).",
            LinkVerified: false),
        [Launcher.Ubisoft] = new(Launcher.Ubisoft, "Ubisoft Connect",
            null,
            "Open Ubisoft Connect → Games → the game → Properties → Uninstall.",
            null,
            "Move the game folder yourself, then in Ubisoft Connect choose the game → \"Locate installed game\" and pick the new folder.",
            LinkVerified: false),
        [Launcher.BattleNet] = new(Launcher.BattleNet, "Battle.net",
            null,
            "Open Battle.net, pick the game, click the cog next to Play → Uninstall.",
            null,
            "Battle.net: pick the game, cog next to Play → Game Settings or use \"Locate the game\" after moving its folder.",
            LinkVerified: false),
        [Launcher.Xbox] = new(Launcher.Xbox, "Xbox app",
            "ms-settings:appsfeatures",
            "Windows Settings opens Installed apps. Find the game, click ⋯ → Uninstall (or use the Xbox app → the game → ⋯ → Manage → Uninstall).",
            "ms-settings:appsfeatures",
            "Xbox app → the game → ⋯ → Manage → Files → Move (or Settings → Installed apps → ⋯ → Move).",
            LinkVerified: true),
    };

    public static string Link(string template, string id) => template.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
}
