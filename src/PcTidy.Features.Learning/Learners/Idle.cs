using System.Runtime.InteropServices;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>Windows' QUERY_USER_NOTIFICATION_STATE (SHQueryUserNotificationState).</summary>
public enum UserNotificationState
{
    Unknown = 0,
    /// <summary>Screen saver running, PC locked or another user switched in: nobody is using it.</summary>
    NotPresent = 1,
    /// <summary>A full-screen app (not D3D) is running, or presentation settings are on.</summary>
    Busy = 2,
    /// <summary>A full-screen Direct3D app (usually a game) is running.</summary>
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    /// <summary>A full-screen Store app is running.</summary>
    App = 7,
}

/// <summary>The raw signals idle detection needs. Injectable for tests.</summary>
public interface IIdleSignals
{
    /// <summary>GetTickCount: milliseconds since boot, wraps every 49.7 days.</summary>
    uint TickCount { get; }
    /// <summary>GetLastInputInfo: tick of the last keyboard/mouse input, or null if unknown.</summary>
    uint? LastInputTick { get; }
    UserNotificationState NotificationState { get; }
    /// <summary>True on battery, false on mains, null when unknown (desktops report mains).</summary>
    bool? OnBattery { get; }
}

public sealed record IdleState(bool IsIdle, TimeSpan IdleFor, string Reason);

/// <summary>
/// "Is now a good moment for a long background job?" Idle = no input for the required time, nothing full-screen
/// (games, presentations, full-screen apps) and not on battery. Never guesses: unknown input time = not idle.
/// </summary>
public sealed class IdleDetector
{
    public static readonly TimeSpan DefaultRequired = TimeSpan.FromMinutes(10);
    private readonly IIdleSignals _signals;

    public IdleDetector(IIdleSignals signals) => _signals = signals;

    /// <summary>Time since the last input, correct across the 49.7-day tick wraparound.</summary>
    public static TimeSpan IdleTime(uint nowTick, uint lastInputTick) => TimeSpan.FromMilliseconds(unchecked(nowTick - lastInputTick));

    public IdleState Check(TimeSpan? required = null)
    {
        var need = required ?? DefaultRequired;
        if (_signals.OnBattery == true) return new IdleState(false, TimeSpan.Zero, "On battery: long jobs wait for the charger.");
        var state = _signals.NotificationState;
        if (state is UserNotificationState.RunningD3DFullScreen or UserNotificationState.Busy or UserNotificationState.App)
            return new IdleState(false, TimeSpan.Zero, "A full-screen app or game is running.");
        if (state is UserNotificationState.PresentationMode)
            return new IdleState(false, TimeSpan.Zero, "Presentation mode is on.");
        if (_signals.LastInputTick is not { } last)
            return new IdleState(false, TimeSpan.Zero, "Windows didn't say when the PC was last used.");
        var idle = IdleTime(_signals.TickCount, last);
        if (state is UserNotificationState.NotPresent && idle < need) idle = need; // locked / screen saver: nobody is there
        return idle >= need
            ? new IdleState(true, idle, $"Nobody has used the PC for {(int)idle.TotalMinutes} minutes.")
            : new IdleState(false, idle, "The PC is in use.");
    }
}

/// <summary>The real signals (user32 / shell32 / kernel32, read-only).</summary>
public sealed class WindowsIdleSignals : IIdleSignals
{
    public static readonly WindowsIdleSignals Instance = new();

    public uint TickCount => unchecked((uint)Environment.TickCount);

    public uint? LastInputTick
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : null;
        }
    }

    public UserNotificationState NotificationState
        => SHQueryUserNotificationState(out var state) == 0 && Enum.IsDefined(typeof(UserNotificationState), state)
            ? (UserNotificationState)state : UserNotificationState.Unknown;

    public bool? OnBattery
    {
        get
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            return s.ACLineStatus switch { 0 => true, 1 => false, _ => null };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>When the PC is usually idle, learned from idle checks per hour of the day.</summary>
public sealed record QuietHours(IReadOnlyList<int> Hours, int Samples, string Reason)
{
    public const string ItemId = "idle";
}

/// <summary>Hours with at least 5 checks of which at least 80% found the PC idle. Pure.</summary>
public static class QuietHoursLearner
{
    public const int MinSamplesPerHour = 5;

    public static QuietHours Evaluate(IEnumerable<IdleHourCounts> counts)
    {
        var list = counts.ToList();
        var hours = list.Where(c => c.TotalSamples >= MinSamplesPerHour && c.IdleSamples >= 0.8 * c.TotalSamples)
            .Select(c => c.Hour).OrderBy(h => h).ToList();
        int samples = list.Sum(c => c.TotalSamples);
        var reason = hours.Count == 0
            ? "Not sure yet when your PC is usually free."
            : "Your PC is usually free " + string.Join(", ", Ranges(hours).Select(r => $"{Clock(r.From)}–{Clock(r.To + 1)}")) + ".";
        return new QuietHours(hours, samples, reason);
    }

    private static IEnumerable<(int From, int To)> Ranges(List<int> hours)
    {
        int start = hours[0], prev = hours[0];
        foreach (var h in hours.Skip(1))
        {
            if (h != prev + 1) { yield return (start, prev); start = h; }
            prev = h;
        }
        yield return (start, prev);
    }

    private static string Clock(int hour) => new DateTime(2000, 1, 1, hour % 24, 0, 0).ToString("t", System.Globalization.CultureInfo.CurrentCulture);
}
