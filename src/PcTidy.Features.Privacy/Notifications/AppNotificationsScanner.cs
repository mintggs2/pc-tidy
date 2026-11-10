using System.Diagnostics;
using PcTidy.Catalog.Privacy;
using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>One app's notification switch as read (logical app id, physical key the plan writes to).</summary>
public sealed record AppNotificationSetting(string AppId, string Name, string PhysicalKey, bool? Enabled, bool Protected);

/// <summary>
/// Per-app notification switches (Settings → System → Notifications), read live from
/// <c>HKCU\…\Notifications\Settings\&lt;AppId&gt;</c> DWORD <c>Enabled</c> (0 = off; 1 or missing = on).
/// Each row flips one app: off if it's on, back on if it's off. Security alerts stay on (greyed). Nothing is ticked.
/// The writes are allowed by <see cref="NotificationAllowances"/>, the catalog's dynamic source built from the same key.
/// </summary>
public sealed class AppNotificationsScanner : IFeatureScanner
{
    private readonly Func<ScanContext, NotificationsEnvironment> _environment;

    public AppNotificationsScanner(Func<ScanContext, NotificationsEnvironment>? environment = null)
        => _environment = environment ?? NotificationsEnvironment.ForContext;

    public string FeatureId => PrivacyIds.Notifications;
    public string Title => "App notifications";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    /// <summary>Every app with a notification switch, sorted by name (read-only).</summary>
    public static IReadOnlyList<AppNotificationSetting> Read(UserRegistry registry)
    {
        var list = new List<AppNotificationSetting>();
        foreach (var id in registry.SubKeyNames(NotificationApps.SettingsKey).Where(NotificationApps.IsUsableAppId))
        {
            var logical = NotificationApps.AppKey(NotificationApps.SettingsKey, id);
            var read = registry.ReadDword(logical, NotificationApps.EnabledValue);
            bool? enabled = read.IsOtherType ? null : !read.Exists || read.Value != 0;
            list.Add(new AppNotificationSetting(id, AppNames.Friendly(id, registry), registry.Map(logical), enabled, NotificationApps.IsProtected(id)));
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string NoAppsReason = "Nothing to change: Windows has no notification switches for any app on this account yet.";

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var env = _environment(ctx);
            progress?.Report(new ScanProgress("Reading notification settings", 0.2));
            var notes = new List<string>();
            var items = new List<ScanItem>();
            int unknown = 0;
            foreach (var app in Read(env.Registry))
            {
                ct.ThrowIfCancellationRequested();
                var item = Row(ctx, app);
                if (item.DisabledReason == UnknownAppReason) unknown++;
                items.Add(item);
            }
            if (unknown > 0)
                notes.Add($"{unknown} app{(unknown == 1 ? "" : "s")} started sending notifications after PC Tidy opened. Restart PC Tidy to change {(unknown == 1 ? "it" : "them")} here.");
            progress?.Report(new ScanProgress("Done", 1, items.Count));
            // No app has a switch yet: one plain line, so the section isn't an empty header.
            var groups = new[]
            {
                items.Count == 0
                    ? new ScanGroup
                    {
                        Id = PrivacyIds.AppsGroup, Title = "Apps that can send notifications",
                        Description = "Apps show up here once they ask Windows to send notifications.",
                        Badge = Badge.Safe,
                        Items = new[]
                        {
                            new ScanItem
                            {
                                Id = PrivacyIds.AppsGroup + ".none", Title = "No apps have asked to send notifications yet",
                                Reason = NoAppsReason, DisabledReason = NoAppsReason, Badge = Badge.Safe,
                            },
                        },
                    }
                    : new ScanGroup
                    {
                        Id = PrivacyIds.AppsGroup, Title = "Apps that can send notifications",
                        Description = "Tick an app to flip its switch: on becomes off, off becomes on. The app keeps working either way, and Undo puts each switch back exactly.",
                        Badge = Badge.Check, Items = items,
                    },
            };
            return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
        }, ct);

    private const string UnknownAppReason = "PC Tidy noticed this app after it started. Restart PC Tidy to change it here, or use Settings → Notifications.";

    /// <summary>
    /// Why a switch can't be flipped (review CR-17). "Restart PC Tidy" only when the app really is missing from the
    /// allowance list read at start; any other refusal shows the safety rule's own reason, so a restart isn't suggested in vain.
    /// </summary>
    public static string RefusalReason(string appId, SafetyVerdict verdict, Func<string, bool>? inSnapshot = null)
    {
        inSnapshot ??= id => PcTidy.Catalog.RegistryCatalog.Find(RegistryHive.CurrentUser,
            NotificationApps.AppKey(NotificationApps.SettingsKey, id), NotificationApps.EnabledValue) is not null;
        if (!inSnapshot(appId)) return UnknownAppReason;
        var why = string.IsNullOrWhiteSpace(verdict.Reason) ? "the change isn't allowed" : verdict.Reason!.Trim().TrimEnd('.');
        return $"PC Tidy's safety rules don't allow this change ({why}). You can still change it in Settings → Notifications.";
    }

    private static ScanItem Row(ScanContext ctx, AppNotificationSetting app)
    {
        var details = new Dictionary<string, string> { ["App id"] = app.AppId };
        if (app.Protected)
            return Info(app, "Windows' own security alerts: a threat found, the firewall turned off, a backup failing.",
                "Security alerts stay on, so you never miss a threat or firewall warning.", details);
        if (app.Enabled is null)
            return Info(app, "This switch was set in an unusual way, so PC Tidy leaves it alone.",
                "Set in an unusual way. Change it in Settings → Notifications.", details);

        var turnOff = app.Enabled == true;
        var action = PrivacyPlanItems.SetUserDword(PrivacyIds.Notifications, app.PhysicalKey, NotificationApps.EnabledValue,
            turnOff ? 0u : 1u, $"Turn {(turnOff ? "off" : "on")} notifications from {app.Name}");
        var verdict = ctx.Safety.Check(action);
        details["What changes"] = turnOff
            ? "No banners, sounds or notification-centre entries from this app. Its badges and the app itself keep working."
            : "This app can show banners and notification-centre entries again.";
        return new ScanItem
        {
            Title = app.Name,
            Detail = app.AppId,
            Reason = turnOff
                ? $"Turn off: {app.Name} stops showing pop-ups and nothing goes to the notification centre. The app keeps working."
                : $"Turn back on: {app.Name} can show notifications again.",
            Badge = Badge.Check,
            DefaultSelected = false,
            CurrentState = turnOff ? "On" : "Off",
            Details = details,
            DisabledReason = verdict.Allowed ? null : RefusalReason(app.AppId, verdict),
            Actions = new[] { action },
        };
    }

    private static ScanItem Info(AppNotificationSetting app, string reason, string disabled, Dictionary<string, string> details) => new()
    {
        Title = app.Name,
        Detail = app.AppId,
        Reason = reason,
        CurrentState = app.Enabled switch { true => "On", false => "Off", _ => "Unusual" },
        Details = details,
        DisabledReason = disabled,
    };
}
