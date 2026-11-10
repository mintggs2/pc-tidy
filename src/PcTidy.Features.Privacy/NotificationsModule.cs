using PcTidy.Features.Privacy.Notifications;
using PcTidy.Features.Privacy.UI;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Privacy;

/// <summary>
/// Notification manager (Plan "Twelve more approved features" item 8, v3): per-app notification switches, Windows
/// tips/suggestions/ads (Start, lock screen, Settings) and the AI-features group (Copilot app, Recall). Every switch is a
/// per-user registry value with an exact undo (the registry handler records the prior state, "wasn't there" included).
/// </summary>
public sealed class NotificationsModule : IFeatureModule, IModuleRiskInfo
{
    /// <param name="environment">Readers for all three scanners (tests pass a redirected registry). Default: real PC, or the sandbox.</param>
    public NotificationsModule(Func<ScanContext, NotificationsEnvironment>? environment = null)
    {
        Apps = new AppNotificationsScanner(environment);
        Tips = new TipsScanner(environment);
        Ai = new AiFeaturesScanner(environment);
        Scanners = new IFeatureScanner[] { Tips, Apps, Ai };
    }

    public string Id => PrivacyIds.Notifications;
    public string Title => "Notifications";
    public string Summary => "Choose which apps may send notifications, turn off Windows tips and ads, and decide about Copilot and Recall. Everything can be undone.";
    public NavGroup Group => NavGroup.You;
    public string Icon => "Alert24";
    public int Order => 40;
    public Release Release => Release.V3;
    public ModuleRisk Risk => ModuleRisk.Check;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }

    public AppNotificationsScanner Apps { get; }
    public TipsScanner Tips { get; }
    public AiFeaturesScanner Ai { get; }

    public static IReadOnlyList<Shortcut> Shortcuts { get; } = new[]
    {
        new Shortcut("Notification settings", SymbolRegular.Alert24,
            "Opens Settings → System → Notifications (Do not disturb, focus, and the same per-app switches).", SettingsTarget: "ms-settings:notifications"),
    };

    public object? CreatePage(IServiceProvider services) => new PrivacyFlowPage(services, this, Risk, "In Windows Settings:", Shortcuts, showKeepList: false,
        "Scanning only reads your notification and tip settings. Nothing changes until you confirm.");
}
