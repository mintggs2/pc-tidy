using PcTidy.Core.Model;

namespace PcTidy.Safety.Rules;

/// <summary>Kinds of "ask first" places. A path can be in several at once (Desktop inside OneDrive).</summary>
[Flags]
public enum AskFirstCategory
{
    None = 0,
    /// <summary>Inside a cloud-synced folder (OneDrive...). Only ever unlocked per folder by the user.</summary>
    CloudFolder = 1,
    /// <summary>Inside Desktop, Documents, Pictures, Videos or Music: organize-only by default.</summary>
    UserLibrary = 2,
    /// <summary>Inside a code project or an app/game-looking folder.</summary>
    ProjectOrApp = 4,
}

public enum RegistryScope
{
    /// <summary>Only this exact key.</summary>
    ExactKey,
    /// <summary>Only keys strictly below this key (the key itself is not included).</summary>
    SubkeysOnly,
}

/// <summary>One registry location a feature may change.</summary>
/// <param name="KeyPath">Hive-less key path, e.g. "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced".</param>
/// <param name="ValueName">Exact value name, or null for any value in the matching keys.</param>
/// <param name="AllowKeyDelete">True: matching keys may be deleted (RegistryDeleteKey).</param>
public sealed record RegistryRule(RegistryHive Hive, string KeyPath, RegistryScope Scope = RegistryScope.ExactKey,
    string? ValueName = null, bool AllowKeyDelete = false)
{
    /// <param name="keyPath">Already-normalised key path (see SafetyGuard).</param>
    /// <param name="valueName">Value name, or null for key-level operations.</param>
    public bool Allows(RegistryHive hive, string keyPath, string? valueName, bool deletesKey)
    {
        if (hive != Hive) return false;
        var rule = KeyPath.Trim('\\');
        var keyMatches = Scope == RegistryScope.ExactKey
            ? keyPath.Equals(rule, StringComparison.OrdinalIgnoreCase)
            : keyPath.Length > rule.Length + 1 && keyPath.StartsWith(rule + "\\", StringComparison.OrdinalIgnoreCase);
        if (!keyMatches) return false;
        if (deletesKey) return AllowKeyDelete;
        return ValueName is null || (valueName is not null && valueName.Equals(ValueName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The tables behind the safety rules. <see cref="Default"/> is the v1 policy; later features (tweaks, developer
/// cleanup) extend it with <c>with</c> expressions so their exact registry keys or folder kinds are listed in one place.
/// </summary>
public sealed record SafetyPolicy
{
    /// <summary>Which ask-first places each feature may touch without a per-folder permission. Unlisted feature = none.</summary>
    public IReadOnlyDictionary<string, AskFirstCategory> FeatureAskFirst { get; init; } = new Dictionary<string, AskFirstCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["cleanup"] = AskFirstCategory.None,
        // Space map acts only on what the user picks by hand; Organize's whole job is the user libraries.
        ["spacemap"] = AskFirstCategory.UserLibrary,
        ["organize"] = AskFirstCategory.UserLibrary,
        ["optimizeall"] = AskFirstCategory.UserLibrary,
        // Copies, clips and screenshots live in the user libraries; each item is still shown and confirmed.
        ["duplicates"] = AskFirstCategory.UserLibrary,
        ["games"] = AskFirstCategory.UserLibrary,
        ["drivemove"] = AskFirstCategory.UserLibrary,
        ["photos"] = AskFirstCategory.UserLibrary,
        ["desktoptidy"] = AskFirstCategory.UserLibrary,
        // Search's "suggest a folder structure" moves whole folders inside the libraries (organize only; every row unticked).
        ["search"] = AskFirstCategory.UserLibrary,
    };

    /// <summary>
    /// Features that may work in the shared Public profile folder (C:\Users\Public), and only as an ask-first place
    /// the user allows per folder. Every other account's folder is never touched by anything.
    /// </summary>
    public IReadOnlySet<string> PublicFolderFeatures { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "organize" };

    /// <summary>Features allowed to use cleanup carve-outs (Plan §6: "only for the Clean feature").</summary>
    public IReadOnlySet<string> CarveOutFeatures { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cleanup", "optimizeall" };

    /// <summary>Registry places that may be written. v1: only the test area. Tweak features add their exact keys here.</summary>
    public IReadOnlyList<RegistryRule> Registry { get; init; } = new[]
    {
        new RegistryRule(RegistryHive.CurrentUser, @"Software\PCTidyTest", RegistryScope.SubkeysOnly, AllowKeyDelete: true),
    };

    /// <summary>
    /// Services that are never changed (Research/Tweaks.md "harmful, never offered"). A trailing * matches a prefix.
    /// The list is shared with the service handler (<see cref="PcTidy.Catalog.Services.ProtectedServices"/>), so the two
    /// walls refuse the same services (SVC-1).
    /// </summary>
    public IReadOnlyList<string> NeverTouchServices { get; init; } = PcTidy.Catalog.Services.ProtectedServices.Names;

    /// <summary>Store packages whose removal breaks Windows (Research.md: Edge/WebView2, Store, Xbox identity, App Installer...).</summary>
    public IReadOnlyList<string> CriticalStorePackages { get; init; } = new[]
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.MicrosoftEdge*",
        "Microsoft.WebView2*", "Microsoft.Win32WebViewHost", "Microsoft.XboxIdentityProvider", "Microsoft.GamingServices",
        "Microsoft.Xbox.TCUI", "Microsoft.VCLibs*", "Microsoft.NET.*", "Microsoft.UI.Xaml*", "Microsoft.WindowsAppRuntime*",
        "Microsoft.SecHealthUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Microsoft.Windows.*", "MicrosoftWindows.*", "Windows.*", "Microsoft.Services.Store.Engagement", "Microsoft.HEIFImageExtension",
        "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions", "Microsoft.HEVCVideoExtension", "Microsoft.WindowsTerminal",
    };

    /// <summary>
    /// Exact package names that match a broad pattern above but are ordinary apps the plan offers (Photos, Dev Home)
    /// or lets the user remove after a warning (Widgets, Phone Link's cross-device helper). Exact names only.
    /// </summary>
    public IReadOnlySet<string> RemovableStoreExceptions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.Photos", "Microsoft.Windows.DevHome", "MicrosoftWindows.Client.WebExperience", "MicrosoftWindows.CrossDevice",
    };

    /// <summary>Schemes OpenExternal may open (pages and launcher screens; never local programs).</summary>
    public IReadOnlySet<string> ExternalSchemes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "ms-settings", "ms-windows-store", "windowsdefender", "steam", "com.epicgames.launcher", "goggalaxy", "uplay",
        "battlenet", "origin2", "xbox", "ms-gamebar", "ms-gamingoverlay",
    };

    public static readonly SafetyPolicy Default = new();

    public AskFirstCategory AllowedFor(string featureId)
        => FeatureAskFirst.TryGetValue(featureId, out var c) ? c : AskFirstCategory.None;

    /// <summary>Case-insensitive match against a list where a trailing * means "starts with".</summary>
    public static bool MatchesAny(string name, IEnumerable<string> patterns)
        => patterns.Any(p => p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(p, StringComparison.OrdinalIgnoreCase));
}
