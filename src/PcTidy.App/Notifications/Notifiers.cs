using Microsoft.Toolkit.Uwp.Notifications;
using PcTidy.App.Shell;

namespace PcTidy.App.Notifications;

/// <summary>One notification. <see cref="NavigateTo"/> is a shell page key ("optimize", "schedule"…) for the button.</summary>
public sealed record AppNotification(AlertKind Kind, string Title, string Body, string ActionLabel, string NavigateTo);

/// <summary>Shows (or, outside the real PC, only logs) a notification.</summary>
public interface INotifier
{
    void Show(AppNotification notification);
}

/// <summary>
/// Windows notifications for the installed app (Microsoft.Toolkit.Uwp.Notifications, unpackaged). Used ONLY on the
/// real PC: the toolkit registers PC Tidy with Windows' notification system the first time a toast is shown
/// (removed again by <see cref="Unregister"/>, which the uninstall path calls). Every toast says what happened,
/// that nothing was changed, and has a "Turn off" button that really turns that alert off.
/// </summary>
public sealed class ToastNotifier : INotifier
{
    /// <summary>Toast argument keys (the only ones PC Tidy reads back).</summary>
    public const string NavigateArg = "navigate";
    public const string MuteArg = "mute";

    public void Show(AppNotification n)
    {
        new ToastContentBuilder()
            .AddArgument(NavigateArg, n.NavigateTo)
            .AddText(n.Title)
            .AddText(n.Body)
            .AddButton(new ToastButton().SetContent(n.ActionLabel).AddArgument(NavigateArg, n.NavigateTo))
            .AddButton(new ToastButton().SetContent("Turn off these alerts").AddArgument(MuteArg, n.Kind.ToString()))
            .Show();
    }

    /// <summary>Clicks on PC Tidy's toasts (also after Windows started PC Tidy because of a click). Real PC only.</summary>
    public static void ListenForClicks(Action<string?, AlertKind?> onClick)
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            try
            {
                var args = ToastArguments.Parse(e.Argument);
                AlertKind? mute = args.TryGetValue(MuteArg, out var m) && Enum.TryParse<AlertKind>(m, out var k) ? k : null;
                string? page = args.TryGetValue(NavigateArg, out var p) ? p : null;
                onClick(mute is null ? page : null, mute);
            }
            catch (Exception ex) { CrashHandler.Write(ex, "Notification click", terminating: false); }
        };
    }

    /// <summary>Removes PC Tidy's notification registration and its notifications from Action Center (uninstall).</summary>
    public static void Unregister()
    {
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception ex) { CrashHandler.Write(ex, "Removing notification registration", terminating: false); }
    }
}

/// <summary>
/// Practice PC and demo: never a real toast. The notification is written to &lt;DataRoot&gt;\logs\notifications.log
/// (practice PC) and kept in <see cref="Recent"/> so Settings and screenshots can show what would have appeared.
/// </summary>
public sealed class LogNotifier : INotifier
{
    private readonly string? _logFile;
    private readonly List<AppNotification> _recent = new();

    /// <param name="logFolder">Null in demo mode (nothing is written at all).</param>
    public LogNotifier(string? logFolder) => _logFile = logFolder is null ? null : Path.Combine(logFolder, "notifications.log");

    public IReadOnlyList<AppNotification> Recent { get { lock (_recent) return _recent.ToList(); } }

    public void Show(AppNotification n)
    {
        lock (_recent) _recent.Add(n);
        Log($"Would show: {n.Title} | {n.Body} [{n.ActionLabel} → {n.NavigateTo}]");
    }

    /// <summary>Also logs decisions not to notify, so practice runs show why nothing appeared.</summary>
    public void Log(string line)
    {
        if (_logFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!); // boundary-ok: own data in DataRoot
            File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); // boundary-ok: own data in DataRoot
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging is best effort */ }
    }
}
