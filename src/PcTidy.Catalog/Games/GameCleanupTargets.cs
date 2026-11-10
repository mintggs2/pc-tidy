namespace PcTidy.Catalog.Games;

/// <summary>
/// Per-game cleanup targets (Plan census v2 "per-game cleanup targets"; Community tweaks T9 Unity, T10 per title).
/// Only logs, crash reports, download leftovers and shader caches. Never saves, settings, replays or anything an
/// anti-cheat checks. Most paths are "(unverified)" in T10, so a target is only offered when its folder exists,
/// and every item is "Check" (never ticked for you).
/// </summary>
public static class GameCleanupTargets
{
    private const string T10 = "https://www.pcgamingwiki.com/wiki/";
    private const string UnityLogs = "https://docs.unity.com/en-us/engine/current/manual/get-started/project-configuration/log-files";
    private const string EpicDx12Stutter = "https://www.pcgamingwiki.com/wiki/Fortnite";

    private static readonly string[] Fortnite = { "FortniteClient-Win64-Shipping", "FortniteLauncher", "EpicGamesLauncher" };
    private static readonly string[] Valorant = { "VALORANT-Win64-Shipping", "VALORANT", "RiotClientServices" };
    private static readonly string[] League = { "League of Legends", "LeagueClient", "RiotClientServices" };
    private static readonly string[] Minecraft = { "Minecraft", "MinecraftLauncher", "javaw" };
    private static readonly string[] Roblox = { "RobloxPlayerBeta", "RobloxStudioBeta" };

    public static readonly IReadOnlyList<GameCleanupTarget> All = new GameCleanupTarget[]
    {
        new("fortnite.logs", "Fortnite", CleanupKind.Logs, CleanupBase.LocalAppData, @"FortniteGame\Saved\Logs", Fortnite, Evidence.Unverified, T10 + "Fortnite"),
        new("fortnite.crashes", "Fortnite", CleanupKind.CrashReports, CleanupBase.LocalAppData, @"FortniteGame\Saved\Crashes", Fortnite, Evidence.Unverified, T10 + "Fortnite"),
        new("fortnite.d3d11cache", "Fortnite", CleanupKind.ShaderCache, CleanupBase.LocalAppData, @"FortniteGame\Saved\D3D11Cache", Fortnite, Evidence.Unverified, EpicDx12Stutter),
        new("fortnite.d3d12cache", "Fortnite", CleanupKind.ShaderCache, CleanupBase.LocalAppData, @"FortniteGame\Saved\D3D12Cache", Fortnite, Evidence.Unverified, EpicDx12Stutter),

        new("valorant.logs", "VALORANT", CleanupKind.Logs, CleanupBase.LocalAppData, @"VALORANT\Saved\Logs", Valorant, Evidence.Unverified, T10 + "Valorant"),
        new("valorant.crashes", "VALORANT", CleanupKind.CrashReports, CleanupBase.LocalAppData, @"VALORANT\Saved\Crashes", Valorant, Evidence.Unverified, T10 + "Valorant"),
        new("riotclient.logs", "Riot Client", CleanupKind.Logs, CleanupBase.LocalAppData, @"Riot Games\Riot Client\Logs", Valorant, Evidence.Unverified, T10 + "Valorant"),

        // League lives wherever Riot installed it (a drive root by default); its install folder is usually protected,
        // in which case the safety rules show it as "can't be picked" with the reason.
        new("league.logs", "League of Legends", CleanupKind.Logs, CleanupBase.EachFixedDriveRoot, @"Riot Games\League of Legends\Logs", League, Evidence.Unverified, T10 + "League_of_Legends"),

        new("minecraft.logs", "Minecraft", CleanupKind.Logs, CleanupBase.RoamingAppData, @".minecraft\logs", Minecraft, Evidence.Unverified, T10 + "Minecraft:_Java_Edition"),
        new("minecraft.crashes", "Minecraft", CleanupKind.CrashReports, CleanupBase.RoamingAppData, @".minecraft\crash-reports", Minecraft, Evidence.Unverified, T10 + "Minecraft:_Java_Edition"),
        new("minecraft.webcache", "Minecraft Launcher", CleanupKind.WebCache, CleanupBase.RoamingAppData, @".minecraft\webcache2", Minecraft, Evidence.Unverified, T10 + "Minecraft:_Java_Edition"),

        new("roblox.logs", "Roblox", CleanupKind.Logs, CleanupBase.LocalAppData, @"Roblox\logs", Roblox, Evidence.Unverified, T10 + "Roblox"),
        new("roblox.downloads", "Roblox", CleanupKind.DownloadCache, CleanupBase.LocalAppData, @"Roblox\Downloads", Roblox, Evidence.Unverified, T10 + "Roblox"),
    };

    /// <summary>Unity players write Player.log (+ Player-prev.log) to LocalLow\&lt;Company&gt;\&lt;Product&gt; (Unity docs, verified).</summary>
    public static readonly IReadOnlyList<string> UnityLogNames = new[] { "Player.log", "Player-prev.log" };

    public const string UnityLogSource = UnityLogs;

    /// <summary>Steam's per-game shader pre-cache (steamapps\shadercache\&lt;appid&gt;): shown with its size, managed by Steam itself.</summary>
    public const string SteamShaderCacheSource = "https://help.steampowered.com/";

    /// <summary>
    /// Shader caches are never ticked for you (Community tweaks §9.7: a full shader compile took about an hour in the worst
    /// measured game). Shown on every shader-cache row and on the group.
    /// </summary>
    public const string ShaderRebuildWarning = "Games rebuild this on first launch; that can take up to about an hour, and the game may stutter until it's done.";

    /// <summary>Plain-English reason per kind. Shader caches carry the stutter warning (Plan §7 #12, T9 PSO note).</summary>
    public static string ReasonFor(CleanupKind kind, string game) => kind switch
    {
        CleanupKind.Logs => $"Log files {game} writes every time it runs. Only useful when you're reporting a bug. Your saves and settings aren't touched.",
        CleanupKind.CrashReports => $"Reports from past {game} crashes. Only useful if you're troubleshooting a crash right now. Saves and settings aren't touched.",
        CleanupKind.ShaderCache => $"Pre-built graphics for {game}. {ShaderRebuildWarning} Only clear it if it's huge or after a graphics glitch.",
        CleanupKind.DownloadCache => $"Installer leftovers {game} downloaded for updates. It downloads them again if it needs them.",
        CleanupKind.WebCache => $"Web pages and images the {game} keeps for its news screen. Downloaded again when needed.",
        _ => $"Leftover files from {game}.",
    };
}
