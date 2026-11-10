namespace PcTidy.Features.Boost.Detection;

/// <summary>
/// Programs Game boost never touches. Anti-cheat and Windows/security processes are not even opened with limited
/// rights (they're skipped by name before any query); launchers and the running game are never closed. "*" matches
/// any characters. Names are process names without ".exe", compared case-insensitively.
/// </summary>
public static class BoostDenyList
{
    /// <summary>Anti-cheat services and their helpers (EAC, EA Javelin, BattlEye, Vanguard, FACEIT, ESEA, PunkBuster, Ricochet...).</summary>
    public static readonly IReadOnlyList<string> AntiCheat = new[]
    {
        "EasyAntiCheat*", "EAAntiCheat*", "start_protected_game", "BEService*", "BattlEye*", "BEDaisy*",
        "vgc", "vgk", "vgtray", "FACEIT*", "faceitservice*", "ESEA*", "PnkBstr*", "Ricochet*", "GameGuard*", "GameMon*",
        "xhunter*", "nProtect*", "mhyprot*", "ACE-*", "SGuard*",
    };

    /// <summary>Windows itself and security software. Never opened, never closed.</summary>
    public static readonly IReadOnlyList<string> WindowsAndSecurity = new[]
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "svchost", "dwm", "explorer", "sihost", "fontdrvhost", "ctfmon", "conhost", "taskhostw", "spoolsv",
        "audiodg", "LogonUI", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SearchIndexer",
        "TextInputHost", "dllhost", "WmiPrvSE", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "MpDefenderCoreService", "SgrmBroker", "smartscreen", "TrustedInstaller", "TiWorker", "MsiExec", "wuauclt",
        "UsoClient", "MoUsoCoreWorker", "GameBar*", "GameInputSvc", "gamingservices*", "PcTidy*",
    };

    /// <summary>Game launchers and their helpers: closing them can stop the game or its overlay. Never closed.</summary>
    public static readonly IReadOnlyList<string> Launchers = new[]
    {
        "steam", "steamwebhelper", "steamservice", "SteamService", "gameoverlayui*", "EpicGamesLauncher", "EpicWebHelper",
        "EpicOnlineServices*", "EOSOverlayRenderer*", "GalaxyClient*", "GOG Galaxy*", "EADesktop", "EABackgroundService",
        "EALauncher", "EAConnect*", "Origin*", "upc", "UbisoftConnect", "UplayWebCore", "UbisoftGameLauncher*", "Battle.net*",
        "XboxPcApp", "XboxApp*", "RiotClient*", "Riot Client*",
    };

    public static bool IsAntiCheat(string processName) => Matches(processName, AntiCheat);

    public static bool IsWindowsOrSecurity(string processName) => Matches(processName, WindowsAndSecurity);

    public static bool IsLauncher(string processName) => Matches(processName, Launchers);

    /// <summary>Never opened, not even with limited rights: anti-cheat, Windows and security.</summary>
    public static bool NeverInspect(string processName) => IsAntiCheat(processName) || IsWindowsOrSecurity(processName);

    /// <summary>Never closed: everything above plus launchers.</summary>
    public static bool NeverClose(string processName) => NeverInspect(processName) || IsLauncher(processName);

    /// <summary>True for "name" or "name.exe" matching any pattern ("*" = any characters).</summary>
    public static bool Matches(string processName, IReadOnlyList<string> patterns)
    {
        var name = Normalize(processName);
        return name.Length > 0 && Wildcard.MatchesAny(name, patterns);
    }

    /// <summary>"C:\x\Discord.exe" or "Discord.exe" → "Discord".</summary>
    public static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        var name = processName.Trim();
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
