using System.Runtime.InteropServices;

namespace PcTidy.Features.Schedule.Runner;

/// <summary>Mirror of Windows' QUERY_USER_NOTIFICATION_STATE (SHQueryUserNotificationState).</summary>
public enum UserNotificationState
{
    Unknown = 0,
    /// <summary>QUNS_NOT_PRESENT: screen saver, locked, or fast user switching.</summary>
    NotPresent = 1,
    /// <summary>QUNS_BUSY: a full-screen app is running.</summary>
    Busy = 2,
    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN: a full-screen game.</summary>
    RunningFullScreenGame = 3,
    /// <summary>QUNS_PRESENTATION_MODE.</summary>
    PresentationMode = 4,
    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS: the only state PC Tidy notifies in.</summary>
    AcceptsNotifications = 5,
    /// <summary>QUNS_QUIET_TIME: first hour after setup / Focus.</summary>
    QuietTime = 6,
    /// <summary>QUNS_APP: a Windows Store app is in full screen.</summary>
    App = 7,
}

public interface INotificationState
{
    UserNotificationState Current();
}

/// <summary>Reads the real state with SHQueryUserNotificationState (read-only).</summary>
public sealed class ShellNotificationState : INotificationState
{
    public UserNotificationState Current()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && Enum.IsDefined(typeof(UserNotificationState), state)
                ? (UserNotificationState)state
                : UserNotificationState.Unknown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return UserNotificationState.Unknown; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>A fixed state (tests, and practice mode where nothing should pop up unexpectedly).</summary>
public sealed class FixedNotificationState : INotificationState
{
    private readonly UserNotificationState _state;
    public FixedNotificationState(UserNotificationState state) => _state = state;
    public UserNotificationState Current() => _state;
}

/// <summary>
/// Respect for the person at the keyboard: a scheduled scan only notifies when Windows says notifications are welcome
/// right now (not during games, presentations, Focus / quiet time, full-screen apps or when nobody is there).
/// Anything else, including "unknown", means no notification: the report is still saved and shown in the app.
/// </summary>
public static class QuietHours
{
    public static bool ShouldNotify(UserNotificationState state) => state == UserNotificationState.AcceptsNotifications;

    public static string WhyNot(UserNotificationState state) => state switch
    {
        UserNotificationState.NotPresent => "No notification: nobody was at the PC (locked or screen saver).",
        UserNotificationState.Busy or UserNotificationState.App => "No notification: a full-screen app was open.",
        UserNotificationState.RunningFullScreenGame => "No notification: a full-screen game was running.",
        UserNotificationState.PresentationMode => "No notification: presentation mode was on.",
        UserNotificationState.QuietTime => "No notification: Windows was in quiet time.",
        UserNotificationState.AcceptsNotifications => "",
        _ => "No notification: Windows didn't say notifications were welcome.",
    };
}
