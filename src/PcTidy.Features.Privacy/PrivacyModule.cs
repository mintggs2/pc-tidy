using PcTidy.Features.Privacy.Common;
using PcTidy.Features.Privacy.Sweep;
using PcTidy.Features.Privacy.UI;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Privacy;

/// <summary>
/// Privacy sweep (v3): recent files, jump lists, Quick access history, clipboard and activity history, browser cookies
/// with the Cookie keeper, and a way to browser history in Clean. Opt-in per item, nothing ticked, saved passwords never
/// touched, every removal quarantined (restorable).
/// </summary>
public sealed class PrivacyModule : IFeatureModule, IModuleRiskInfo, IModuleFlowText
{
    public PrivacyModule() => Scanners = new IFeatureScanner[] { Scanner };

    public string Id => PrivacyIds.Privacy;
    public string Title => "Privacy sweep";
    public string Summary => "Clear recent files, jump lists and cookies (keeping the sites you choose). You pick each item; saved passwords are never touched.";
    public NavGroup Group => NavGroup.You;
    public string Icon => "ShieldKeyhole24";
    public int Order => 30;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public PrivacySweepScanner Scanner { get; } = new();

    /// <summary>The preview banner: recent lists go to quarantine, cookies keep their old database in quarantine.</summary>
    public string? PreviewNote => "Nothing is ticked. Recent lists you tick go to quarantine, and cookies keep a backup of their old database there, so Undo in History puts everything back. Saved passwords are never touched.";

    /// <summary>The page's top-strip buttons (Settings pages Windows clears itself, and the way to Clean's browser history).</summary>
    public static IReadOnlyList<Shortcut> Shortcuts { get; } = new[]
    {
        new Shortcut("Clipboard history", SymbolRegular.ClipboardTextLtr24,
            "Opens Settings → System → Clipboard. Press Clear to remove what you copied (pinned clips stay).", SettingsTarget: "ms-settings:clipboard"),
        new Shortcut("Activity history", SymbolRegular.History24,
            "Opens Settings → Privacy & security → Activity history. Press Clear history.", SettingsTarget: "ms-settings:privacy-activityhistory"),
        new Shortcut("Browser history", SymbolRegular.Globe24,
            "Opens Clean. Its Privacy extras group lists each browser's history and cookies, unticked.", Navigate: NavigationRequest.CleanPrivacyExtras),
    };

    /// <summary>Needs IKnownLocations, ISafetyGuard, IInUseChecker, AppSettings and IFlowHost (IShellFlowHost for the inline Done card and navigation).</summary>
    public object? CreatePage(IServiceProvider services) => new PrivacyFlowPage(services, this, Risk, "Cleared in Windows instead:", Shortcuts, showKeepList: true,
        "Scanning only reads your recent-files lists, jump lists and which browsers keep cookies. Nothing changes until you confirm.");
}
