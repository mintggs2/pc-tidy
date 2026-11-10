namespace PcTidy.Catalog.Speed;

/// <summary>
/// Names the Speed section never offers to switch off or close: Windows itself, security software and anti-cheat.
/// Patterns are case-insensitive; a trailing * means "starts with". The SafetyGuard and the Platform handlers keep
/// their own lists too (two walls), so these lists only decide what the page offers.
/// </summary>
public static class SpeedProtectedNames
{
    /// <summary>Processes that are never offered "Close": Windows core, shell hosts, security, anti-cheat, PC Tidy.</summary>
    public static readonly IReadOnlyList<string> NeverCloseProcesses = new[]
    {
        // Windows core and shell
        "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "svchost", "dwm", "explorer", "sihost", "fontdrvhost", "ctfmon", "conhost", "taskhostw", "spoolsv",
        "audiodg", "LogonUI", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SearchIndexer",
        "SearchApp", "TextInputHost", "dllhost", "WmiPrvSE", "wudfhost", "dasHost", "smartscreen", "SystemSettings",
        "ApplicationFrameHost", "LockApp", "userinit", "WUDFHost", "Widgets", "WidgetService", "PhoneExperienceHost",
        // Windows Update and installers
        "TrustedInstaller", "TiWorker", "MsiExec", "wuauclt", "MoUsoCoreWorker", "UsoClient",
        // Microsoft security
        "MsMpEng", "NisSrv", "MpCmdRun", "SecurityHealthService", "SecurityHealthSystray", "MpDefenderCoreService", "SgrmBroker",
        "MsSense", "SenseIR", "SenseCncProxy",
        // Third-party security
        "avp", "avpui", "ekrn", "egui", "bdagent", "bdservicehost", "vsserv", "mbam", "mbamtray", "MBAMService", "AvastSvc",
        "AvastUI", "avgsvc", "avgui", "Norton*", "NortonSecurity", "ns", "mcshield", "McAfee*", "mfe*", "ModuleCoreService",
        "SophosHealth", "Sophos*", "SentinelAgent", "SentinelUI", "CSFalconService", "CSFalconContainer", "WRSA", "fsaua",
        "F-Secure*", "PSANHost", "a2service", "AVGUI", "AvastBrowser*",
        // Anti-cheat
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "BEDaisy", "vgc", "vgtray", "vgk", "FACEIT*", "faceitclient",
        "ESEA*", "PnkBstr*", "xigncode*", "GameGuard*", "nProtect*", "mhyprot*", "ACE-*", "Ricochet*",
        // PC Tidy itself
        "PcTidy*", "PC Tidy*",
    };

    /// <summary>Auto-start services the Speed section never offers to set to Manual (on top of "never Microsoft, never drivers").</summary>
    public static readonly IReadOnlyList<string> NeverTouchServices = new[]
    {
        // Windows Update, installs, licensing
        "wuauserv", "UsoSvc", "BITS", "WaaSMedicSvc", "CryptSvc", "TrustedInstaller", "AppXSvc", "ClipSVC", "InstallService",
        "msiserver", "sppsvc", "wlidsvc", "TokenBroker",
        // Microsoft security
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "mpsdrv",
        "BFE", "SgrmBroker", "SamSs", "KeyIso", "VaultSvc", "EFS",
        // Xbox / Game Pass
        "Xbl*", "Xbox*", "GamingServices*",
        // Core plumbing
        "W32Time", "Winmgmt", "Schedule", "EventLog", "RpcSs", "RpcEptMapper", "DcomLaunch", "Dnscache", "NlaSvc", "nsi", "Dhcp",
        "BrokerInfrastructure", "SystemEventsBroker", "StateRepository", "CoreMessagingRegistrar", "Power", "ProfSvc", "LSM",
        "gpsvc", "UserManager", "Audiosrv", "AudioEndpointBuilder", "LanmanWorkstation", "TimeBrokerSvc", "PlugPlay", "VSS",
        "swprv", "Appinfo", "AppIDSvc",
        // Anti-cheat
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "vgc", "vgk", "FACEIT*", "ESEA*", "PnkBstr*", "Ricochet*",
        // Third-party security
        "avp*", "ekrn", "EHttpSrv", "bdservicehost", "VSSERV", "MBAMService", "AvastSvc", "avgsvc", "Norton*", "mfe*", "McAfee*",
        "Sophos*", "SentinelAgent", "CSFalconService", "WRSVC", "WRCoreService", "fsaua", "FSMA", "PSANHost", "a2service",
    };

    /// <summary>
    /// Publishers whose services and processes are security or anti-cheat software (matched as "contains" against the
    /// file's company name or signer). Switching them off would leave the PC unprotected or stop games from starting.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedPublishers = new[]
    {
        "ESET", "Bitdefender", "Kaspersky", "AO Kaspersky", "Malwarebytes", "Sophos", "CrowdStrike", "SentinelOne", "Sentinel Labs",
        "Trend Micro", "McAfee", "NortonLifeLock", "Gen Digital", "Symantec", "Avast", "AVG Technologies", "Webroot", "F-Secure",
        "WithSecure", "Panda Security", "Emsisoft", "G DATA", "Comodo", "Avira", "Check Point", "Fortinet", "Palo Alto Networks",
        "Cylance", "Carbon Black", "Trellix", "Huorong",
        "Easy Anti-Cheat", "EasyAntiCheat", "Kamu", "BattlEye", "Riot Games", "FACEIT", "ESEA", "Even Balance", "Wellbia", "INCA Internet",
    };

    /// <summary>Case-insensitive match against a list where a trailing * means "starts with".</summary>
    public static bool Matches(string? name, IEnumerable<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        foreach (var p in patterns)
        {
            if (p.EndsWith('*'))
            {
                if (n.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (n.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>True when a company or signer name belongs to security or anti-cheat software.</summary>
    public static bool IsProtectedPublisher(params string?[] names)
        => names.Any(n => !string.IsNullOrWhiteSpace(n)
            && ProtectedPublishers.Any(p => n!.Contains(p, StringComparison.OrdinalIgnoreCase)));
}
