using PcTidy.Core.Model;

namespace PcTidy.Catalog.Services;

/// <summary>
/// The ONE list of services PC Tidy never weakens, and their Windows default start types (SVC-1, ELV-1, UNDO-1).
/// The SafetyGuard (<c>SafetyPolicy.NeverTouchServices</c>, <c>ServiceDefaults</c>) and the service handler (its own
/// second wall, including undo) both read it, so they can't drift apart. A service is protected when it is in
/// <see cref="Names"/> or the service catalog calls it <see cref="ServiceVerdict.Protected"/>.
/// </summary>
public static class ProtectedServices
{
    /// <summary>Services that are never switched off or weakened. A trailing * matches a prefix.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        // Windows Update and app installs
        "wuauserv", "UsoSvc", "BITS", "WaaSMedicSvc", "CryptSvc", "TrustedInstaller", "AppXSvc", "ClipSVC", "InstallService", "msiserver",
        "DoSvc",
        // Security
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "mpsdrv", "BFE",
        "SgrmBroker", "SamSs", "KeyIso", "VaultSvc", "EFS", "MDCoreSvc", "webthreatdefsvc", "webthreatdefusersvc*", "Appinfo", "AppIDSvc",
        "Netlogon",
        // Xbox / Game Pass
        "Xbl*", "Xbox*", "GamingServices*",
        // Sign-in, licensing, time, core plumbing
        "wlidsvc", "TokenBroker", "sppsvc", "W32Time", "Winmgmt", "Schedule", "EventLog", "RpcSs", "RpcEptMapper", "DcomLaunch",
        "Dnscache", "NlaSvc", "nsi", "Dhcp", "BrokerInfrastructure", "SystemEventsBroker", "StateRepository", "CoreMessagingRegistrar",
        "Power", "PlugPlay", "ProfSvc", "LSM", "gpsvc", "UserManager", "Audiosrv", "AudioEndpointBuilder", "LanmanWorkstation", "TimeBrokerSvc",
        // Restore points
        "VSS", "swprv",
        // Typing in Start, IPv6 helper
        "TextInputManagementService", "iphlpsvc",
        // Anti-cheat
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "vgc", "vgk", "FACEIT*", "ESEA*", "PnkBstr*", "Ricochet*",
    };

    /// <summary>
    /// Windows 10/11 default start types for protected services (where 10 and 11 differ, both are listed). A protected
    /// service may only be set to one of these, or to Automatic (switching a service on can't weaken anything).
    /// </summary>
    private static readonly Dictionary<string, ServiceStart[]> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // Security
        ["WinDefend"] = new[] { ServiceStart.Automatic },
        ["WdNisSvc"] = new[] { ServiceStart.Manual },
        ["SecurityHealthService"] = new[] { ServiceStart.Manual },
        ["wscsvc"] = new[] { ServiceStart.AutomaticDelayed },
        ["mpssvc"] = new[] { ServiceStart.Automatic },
        ["BFE"] = new[] { ServiceStart.Automatic },
        // Updates and installs
        ["wuauserv"] = new[] { ServiceStart.Manual },
        ["UsoSvc"] = new[] { ServiceStart.AutomaticDelayed, ServiceStart.Manual },
        ["BITS"] = new[] { ServiceStart.Manual, ServiceStart.AutomaticDelayed },
        ["WaaSMedicSvc"] = new[] { ServiceStart.Manual },
        ["CryptSvc"] = new[] { ServiceStart.Automatic },
        ["TrustedInstaller"] = new[] { ServiceStart.Manual },
        ["AppXSvc"] = new[] { ServiceStart.Manual },
        ["ClipSVC"] = new[] { ServiceStart.Manual },
        ["InstallService"] = new[] { ServiceStart.Manual },
        // Restore points
        ["VSS"] = new[] { ServiceStart.Manual },
        ["swprv"] = new[] { ServiceStart.Manual },
        // Core plumbing (Automatic only)
        ["EventLog"] = new[] { ServiceStart.Automatic },
        ["RpcSs"] = new[] { ServiceStart.Automatic },
        ["DcomLaunch"] = new[] { ServiceStart.Automatic },
        ["Winmgmt"] = new[] { ServiceStart.Automatic },
        ["Schedule"] = new[] { ServiceStart.Automatic },
        ["Dnscache"] = new[] { ServiceStart.Automatic },
        // Recent Windows 11 builds show Manual (trigger start); unverified on a clean install, so both count as default.
        ["NlaSvc"] = new[] { ServiceStart.Automatic, ServiceStart.Manual },
        ["TextInputManagementService"] = new[] { ServiceStart.Automatic },
        ["iphlpsvc"] = new[] { ServiceStart.Automatic },
        // Sign-in / activation / time
        ["sppsvc"] = new[] { ServiceStart.AutomaticDelayed },
        ["wlidsvc"] = new[] { ServiceStart.Manual },
        ["TokenBroker"] = new[] { ServiceStart.Manual },
        ["W32Time"] = new[] { ServiceStart.Manual },
        // Xbox / gaming
        ["XblAuthManager"] = new[] { ServiceStart.Manual },
        ["XblGameSave"] = new[] { ServiceStart.Manual },
        ["XboxNetApiSvc"] = new[] { ServiceStart.Manual },
        ["XboxGipSvc"] = new[] { ServiceStart.Manual },
        ["GamingServices"] = new[] { ServiceStart.Automatic },
        // Anti-cheat services install as Manual (started by their games)
        ["EasyAntiCheat"] = new[] { ServiceStart.Manual },
        ["EasyAntiCheat_EOS"] = new[] { ServiceStart.Manual },
        ["BEService"] = new[] { ServiceStart.Manual },
        ["vgc"] = new[] { ServiceStart.Manual },
    };

    /// <summary>Services with a default in the table above.</summary>
    public static IReadOnlyCollection<string> KnownDefaults => Defaults.Keys;

    /// <summary>Case-insensitive match against a list where a trailing * means "starts with".</summary>
    public static bool MatchesAny(string name, IEnumerable<string> patterns)
        => patterns.Any(p => p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>In <see cref="Names"/>, or a service the catalog calls Protected.</summary>
    public static bool IsProtected(string name)
        => MatchesAny(name, Names) || ServiceCatalog.Find(name) is { Verdict: ServiceVerdict.Protected };

    /// <summary>True if setting protected service <paramref name="name"/> to <paramref name="start"/> is a restore to a Windows default.</summary>
    public static bool IsDefaultFor(string name, ServiceStart start)
    {
        if (start is ServiceStart.Disabled or ServiceStart.Boot or ServiceStart.System) return false;
        if (Defaults.TryGetValue(name, out var allowed)) return start == ServiceStart.Automatic || allowed.Contains(start);
        // Not in this table: the service catalog's Windows defaults (many Harmful/Protected services default to Manual).
        if (ServiceCatalog.Find(name) is { } entry && entry.WindowsDefaults.Contains(start)) return true;
        // Unknown protected service: only Automatic (switching it on can't weaken anything).
        return start == ServiceStart.Automatic;
    }

    /// <summary>
    /// True when <paramref name="start"/> would leave protected service <paramref name="name"/> weaker than Windows'
    /// default: Disabled, a driver start type, or a later start than every default (Manual when the default is
    /// Automatic). Used by undo, which must never switch protection off again whatever its saved data says.
    /// </summary>
    public static bool WeakerThanDefault(string name, ServiceStart start)
    {
        if (start is ServiceStart.Disabled or ServiceStart.Boot or ServiceStart.System) return true;
        if (IsDefaultFor(name, start)) return false;
        var defaults = Defaults.TryGetValue(name, out var d) ? d
            : ServiceCatalog.Find(name) is { WindowsDefaults.Count: > 0 } e ? e.WindowsDefaults.ToArray()
            : new[] { ServiceStart.Automatic };
        var weakest = defaults.Where(s => s is not (ServiceStart.Disabled or ServiceStart.Boot or ServiceStart.System)).Select(Strength).DefaultIfEmpty(Strength(ServiceStart.Automatic)).Min();
        return Strength(start) < weakest;
    }

    /// <summary>How readily a service runs: Automatic 3, Automatic (delayed) 2, Manual 1, anything else 0.</summary>
    private static int Strength(ServiceStart s) => s switch
    {
        ServiceStart.Automatic => 3,
        ServiceStart.AutomaticDelayed => 2,
        ServiceStart.Manual => 1,
        _ => 0,
    };
}
