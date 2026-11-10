using PcTidy.App.Notifications;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Features.Schedule.Runner;

namespace PcTidy.App.Composition;

/// <summary>
/// <c>PcTidy.exe --scheduled-scan [--checkup]</c> (no window): runs Schedule &amp; reports' report-only scan, then shows
/// its one notification (if it has one) through the same rules as the tray alerts: the user's switch, quiet hours,
/// Windows' busy state and at most one a day. On a practice PC nothing pops up; the decision is logged instead.
/// Exit codes are the scan's own (0 done, 3 services missing, 4 report not saved, 5 cancelled).
/// </summary>
public static class ScheduledScanBridge
{
    public static async Task<int> RunAsync(AppArgs args, AppServices services, CancellationToken ct)
    {
        var result = await ScheduledScan.RunAsync(services, ScheduledScanRequest.Parse(args.Raw), ct).ConfigureAwait(false);
        var settings = services.Get<AppSettings>();
        var notifications = AppNotifications.For(services);
        // A history file that couldn't be updated doesn't stop the scan (the report is saved anyway); it goes in the log.
        if (result.HistoryProblems.Count > 0)
        {
            var problems = string.Join("; ", result.HistoryProblems);
            notifications.Log("Scheduled scan: some history wasn't updated: " + problems);
            CrashHandler.Write(new IOException(problems), "Scheduled scan: history not updated", terminating: false);
        }
        if (result.Notification is { } n)
            notifications.TryShow(new AppNotification(AlertKind.ScanReport, n.Title, n.Body, n.ActionLabel, n.NavigateTo), ShellPrefs.Get(settings));
        else
            notifications.Log("Scheduled scan: " + (result.NotNotifiedBecause ?? result.Summary));
        return result.ExitCode;
    }
}

/// <summary>
/// The app's notification channel: the policy (switches, quiet hours, busy state, once a day) in front of the right
/// notifier — real Windows notifications on the real PC only; a log in practice mode; memory only in demo mode.
/// </summary>
public sealed class AppNotifications
{
    private readonly AlertPolicy _policy;
    private readonly INotifier _notifier;

    public AppNotifications(AlertPolicy policy, INotifier notifier)
    {
        _policy = policy;
        _notifier = notifier;
    }

    /// <summary>True when notifications really reach Windows (the real PC, not demo or practice mode).</summary>
    public bool IsReal => _notifier is ToastNotifier;

    /// <summary>What practice/demo mode "showed" (empty on the real PC).</summary>
    public IReadOnlyList<AppNotification> Simulated => (_notifier as LogNotifier)?.Recent ?? Array.Empty<AppNotification>();

    public static AppNotifications For(AppServices services)
    {
        bool real = !services.IsDemo && services.Args.SandboxRoot is null
                    && (services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.IsSandbox != true;
        var dataRoot = services.Get<AppSettings>().DataRoot;
        INotifier notifier = real ? new ToastNotifier() : new LogNotifier(services.IsDemo ? null : DataPaths.Logs(dataRoot));
        IAlertMemory memory = services.IsDemo ? new InMemoryAlertMemory() : new AlertMemoryStore(dataRoot);
        IBusyState busy = real ? new WindowsBusyState() : new AlwaysFree();
        return new AppNotifications(new AlertPolicy(TimeProvider.System, busy, memory), notifier);
    }

    /// <summary>Shows the notification if the rules allow it now. Returns the verdict (logged in practice mode).</summary>
    public AlertVerdict TryShow(AppNotification n, ShellPrefs prefs)
    {
        var verdict = _policy.TryClaim(n.Kind, prefs.AlertEnabled(n.Kind), prefs.Quiet);
        if (verdict == AlertVerdict.Show)
        {
            try { _notifier.Show(n); }
            catch (Exception ex) { CrashHandler.Write(ex, "Showing a notification", terminating: false); }
        }
        else Log($"{n.Kind}: \"{n.Title}\" {AlertPolicy.Describe(verdict)}");
        return verdict;
    }

    public void Log(string line) => (_notifier as LogNotifier)?.Log(line);

    /// <summary>Practice and demo mode: Windows' busy state isn't read (nothing real pops up anyway).</summary>
    private sealed class AlwaysFree : IBusyState
    {
        public bool AcceptsNotifications() => true;
    }
}
