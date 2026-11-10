using System.Diagnostics;
using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>
/// The AI-features group (census v3): the Copilot app (removed for your account, back from the Microsoft Store) and
/// Recall, which only exists on Copilot+ PCs: anywhere else it's a greyed "Not on this PC" row, never an action.
/// Detection is read-only (PackageManager, WMI). Nothing is ticked.
/// </summary>
public sealed class AiFeaturesScanner : IFeatureScanner
{
    private readonly Func<ScanContext, NotificationsEnvironment> _environment;

    public AiFeaturesScanner(Func<ScanContext, NotificationsEnvironment>? environment = null)
        => _environment = environment ?? NotificationsEnvironment.ForContext;

    public string FeatureId => PrivacyIds.Notifications;
    public string Title => "AI features";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            progress?.Report(new ScanProgress("Checking Copilot and Recall", 0.2));
            var facts = _environment(ctx).Ai.Read(ct);
            var items = new[] { CopilotRow(facts), RecallRow(facts) };
            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult
            {
                FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
                Groups = new[]
                {
                    new ScanGroup
                    {
                        Id = PrivacyIds.AiGroup, Title = "AI features",
                        Description = "Copilot and Recall. Each is your call; nothing here is ticked.",
                        Badge = Badge.Check, Items = items,
                    },
                },
            };
        }, ct);

    public const string CopilotName = "Copilot";

    public static ScanItem CopilotRow(AiFeatureFacts facts)
    {
        if (facts.CopilotPackageFullName is not { } package)
            return new ScanItem
            {
                Title = "Copilot app",
                Reason = "Microsoft's AI assistant app.",
                CurrentState = "Not installed",
                DisabledReason = "Not on this PC: the Copilot app isn't installed for your account.",
            };
        return new ScanItem
        {
            Title = "Remove the Copilot app",
            Detail = package,
            Reason = "Uninstalls the Copilot app for your account. Nothing else in Windows depends on it. To get it back, install Copilot from the Microsoft Store.",
            Badge = Badge.Permanent,
            DefaultSelected = false,
            CurrentState = "Installed",
            OwnerApp = CopilotName,
            Details = new Dictionary<string, string>
            {
                ["What's lost"] = "The Copilot app and its local settings. Your Microsoft account and chats saved online stay.",
                ["Undo"] = "Reinstall Copilot from the Microsoft Store (PC Tidy can't put a removed app back by itself).",
            },
            Actions = new[] { PrivacyPlanItems.RemoveStoreApp(PrivacyIds.Notifications, package, CopilotName, "Remove the Copilot app for this account") },
        };
    }

    public static ScanItem RecallRow(AiFeatureFacts facts)
    {
        if (!facts.IsCopilotPlus)
            return new ScanItem
            {
                Title = "Recall",
                Reason = "Recall saves snapshots of your screen so you can search them later. It only exists on Copilot+ PCs.",
                CurrentState = "Not on this PC",
                DisabledReason = "Not on this PC: Recall only exists on Copilot+ PCs, so there's nothing to turn off.",
            };
        if (facts.Recall == RecallState.Disabled)
            return new ScanItem
            {
                Title = "Recall",
                Reason = "Recall's Windows feature is already turned off on this PC.",
                CurrentState = "Off",
                DisabledReason = "Already off. Nothing to do.",
            };
        return new ScanItem
        {
            Title = "Turn off Recall",
            Detail = "Windows feature \"Recall\"",
            Reason = "Turns the Recall feature off in Windows Features, so it can't take snapshots of your screen. Windows deletes the snapshots it saved. Undo turns the feature back on; old snapshots don't come back.",
            Badge = Badge.Check,
            DefaultSelected = false,
            NeedsAdmin = true,
            NeedsRestart = true,
            CurrentState = "On",
            Details = new Dictionary<string, string>
            {
                ["What's lost"] = "Recall and any snapshots it saved.",
                ["Needs"] = "Admin rights and a restart. A restore point is made first.",
            },
            Actions = new[] { PrivacyPlanItems.RemoveOptionalFeature(PrivacyIds.Notifications, WindowsAiFeatureProbe.RecallFeatureName, "Turn off the Recall feature") },
        };
    }
}
