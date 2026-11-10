using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Plan §8 "Never, in any mode": things a tweak may never turn off or remove. The scanner refuses to offer a tweak
/// that matches (second wall behind the catalog), and the catalog tests check every row against these lists.
/// The bad-tweak finder may only RESTORE these to their Windows default.
/// </summary>
public static class TweakSafetyRules
{
    /// <summary>Services a tweak never changes (Tweaks.md "Harmful, never offered" + anti-cheat). Trailing * = prefix.</summary>
    public static readonly IReadOnlyList<string> NeverTouchedServices = new[]
    {
        "wuauserv", "UsoSvc", "BITS", "WaaSMedicSvc", "CryptSvc", "TrustedInstaller", "AppXSvc", "ClipSVC", "InstallService", "DoSvc",
        "WinDefend", "WdNisSvc", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "SgrmBroker", "webthreatdefsvc",
        "Xbl*", "Xbox*", "GamingServices*",
        "wlidsvc", "TokenBroker", "sppsvc", "W32Time", "Winmgmt", "Schedule", "EventLog", "RpcSs", "DcomLaunch", "Dnscache", "NlaSvc",
        "VSS", "swprv", "TextInputManagementService", "iphlpsvc", "WpnService", "Themes",
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "vgc", "vgk", "FACEIT*", "ESEA*", "PnkBstr*",
    };

    /// <summary>Registry areas a tweak never writes: security, updates, mitigations, boot integrity. Prefix match on "HIVE\key".</summary>
    public static readonly IReadOnlyList<string> NeverWrittenKeys = new[]
    {
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender",
        @"HKLM\SOFTWARE\Microsoft\Windows Defender",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender Security Center",
        @"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall",
        @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
        @"HKLM\SOFTWARE\Microsoft\WindowsUpdate",
        @"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
        @"HKLM\SYSTEM\CurrentControlSet\Control\CI",
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore",
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend",
        @"HKLM\SYSTEM\CurrentControlSet\Services\wuauserv",
    };

    /// <summary>Exact values a tweak never writes (they live in keys that also hold harmless values).</summary>
    public static readonly IReadOnlyList<string> NeverWrittenValues = new[]
    {
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\FeatureSettingsOverride",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\FeatureSettingsOverrideMask",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\LargeSystemCache",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\DisableExceptionChainValidation",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DisableWindowsUpdateAccess",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DoNotConnectToWindowsUpdateInternetLocations",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\WUServer",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableSmartScreen",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SmartScreenEnabled",
        @"HKLM\SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell\ExecutionPolicy",
        @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\DisableDeleteNotification",
    };

    /// <summary>Store packages a tweak never removes (Store, winget, frameworks, Edge/WebView2, Xbox and Game Bar, security, shell hosts).</summary>
    public static readonly IReadOnlyList<string> NeverRemovedPackages = new[]
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.MicrosoftEdge*", "Microsoft.WebView2*",
        "Microsoft.Win32WebViewHost", "Microsoft.Xbox*", "Microsoft.GamingApp", "Microsoft.GamingServices", "Microsoft.XboxGamingOverlay",
        "Microsoft.VCLibs*", "Microsoft.NET.*", "Microsoft.UI.Xaml*", "Microsoft.WindowsAppRuntime*", "Microsoft.SecHealthUI",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp", "Microsoft.Windows.*", "MicrosoftWindows.*", "Windows.*",
    };

    public static bool MatchesAny(string name, IEnumerable<string> patterns)
        => patterns.Any(p => p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the change is allowed for a tweak; otherwise why not.</summary>
    public static string? WhyForbidden(TweakChange change) => change switch
    {
        RegistryValueChange r => KeyProblem(r.Hive, r.KeyPath, r.ValueName),
        RegistryTokenChange t => KeyProblem(t.Hive, t.KeyPath, t.ValueName),
        ServiceStartChange s when MatchesAny(s.ServiceName, NeverTouchedServices) => $"'{s.ServiceName}' is a security, update, Xbox or core service.",
        ServiceStartChange { Target: ServiceStart.Disabled or ServiceStart.Boot or ServiceStart.System } => "Tweaks never set a service to Disabled.",
        AppxRemoveChange a when MatchesAny(a.PackageName, NeverRemovedPackages) => $"'{a.PackageName}' is part of Windows.",
        _ => null,
    };

    private static string? KeyProblem(RegistryHive hive, string keyPath, string valueName)
    {
        var full = $@"{RegistryNames.Short(hive)}\{keyPath.Trim('\\')}";
        if (NeverWrittenKeys.Any(k => full.Equals(k, StringComparison.OrdinalIgnoreCase) || full.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)))
            return "It's in a security or update area of the registry.";
        var value = full + "\\" + valueName;
        return NeverWrittenValues.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase)) ? "It's a security or stability setting." : null;
    }
}
