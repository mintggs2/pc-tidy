using PcTidy.Core;

namespace PcTidy.App.Notifications;

/// <summary>The kinds of notification PC Tidy can show. There is no promotional kind, and there never will be.</summary>
public enum AlertKind
{
    /// <summary>A fixed drive has less than 10 % free (tray only).</summary>
    LowDisk,
    /// <summary>The scheduled scan found enough to look at (its own threshold, see Schedule &amp; reports).</summary>
    ScanReport,
}

/// <summary>Quiet hours as "from–to" in local time. Overnight windows (22:00–08:00) wrap past midnight.</summary>
public sealed record QuietHoursWindow(bool Enabled, TimeOnly From, TimeOnly To)
{
    public static readonly QuietHoursWindow Default = new(true, new TimeOnly(22, 0), new TimeOnly(8, 0));

    /// <summary>True when <paramref name="now"/> falls inside the window. From == To means "no quiet hours".</summary>
    public bool Contains(TimeOnly now)
    {
        if (!Enabled || From == To) return false;
        return From < To ? now >= From && now < To : now >= From || now < To;
    }

    public static QuietHoursWindow Parse(bool enabled, string? from, string? to)
        => new(enabled, TimeOnly.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : Default.From,
                        TimeOnly.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : Default.To);
}

/// <summary>Why a notification was or wasn't shown (logged, and asserted in tests).</summary>
public enum AlertVerdict { Show, Off, QuietHours, Busy, AlreadyToday }

/// <summary>Is Windows happy to show a notification right now (not full screen, not presenting, not Focus)?</summary>
public interface IBusyState
{
    bool AcceptsNotifications();
}

/// <summary>The real state, read with SHQueryUserNotificationState (Schedule's reader; read-only).</summary>
public sealed class WindowsBusyState : IBusyState
{
    private readonly PcTidy.Features.Schedule.Runner.ShellNotificationState _state = new();
    public bool AcceptsNotifications() => PcTidy.Features.Schedule.Runner.QuietHours.ShouldNotify(_state.Current());
}

/// <summary>
/// Decides whether a notification may be shown now: the user's switch, their quiet hours, Windows' busy state
/// (full-screen game, presentation, Focus) and at most one of each kind per 24 hours. Pure logic: the clock, the busy
/// state and the remembered times are injected, so tests drive it with a fake clock.
/// </summary>
public sealed class AlertPolicy
{
    /// <summary>One of each kind per day at most (rolling, so 23:59 and 00:01 don't both get one).</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(24);

    private readonly TimeProvider _clock;
    private readonly IBusyState _busy;
    private readonly IAlertMemory _memory;

    public AlertPolicy(TimeProvider clock, IBusyState busy, IAlertMemory memory)
    {
        _clock = clock;
        _busy = busy;
        _memory = memory;
    }

    public AlertVerdict Evaluate(AlertKind kind, bool enabled, QuietHoursWindow quiet)
    {
        if (!enabled) return AlertVerdict.Off;
        var now = _clock.GetLocalNow();
        if (quiet.Contains(TimeOnly.FromDateTime(now.DateTime))) return AlertVerdict.QuietHours;
        if (_memory.LastShownUtc(kind) is { } last && _clock.GetUtcNow().UtcDateTime - last < MinimumGap) return AlertVerdict.AlreadyToday;
        if (!_busy.AcceptsNotifications()) return AlertVerdict.Busy;
        return AlertVerdict.Show;
    }

    /// <summary>Evaluate, and when the answer is Show remember it as shown now.</summary>
    public AlertVerdict TryClaim(AlertKind kind, bool enabled, QuietHoursWindow quiet)
    {
        var verdict = Evaluate(kind, enabled, quiet);
        if (verdict == AlertVerdict.Show) _memory.MarkShown(kind, _clock.GetUtcNow().UtcDateTime);
        return verdict;
    }

    public static string Describe(AlertVerdict v) => v switch
    {
        AlertVerdict.Show => "shown",
        AlertVerdict.Off => "not shown: this alert is switched off",
        AlertVerdict.QuietHours => "not shown: quiet hours",
        AlertVerdict.Busy => "not shown: Windows is busy (full screen, presenting or Focus)",
        _ => "not shown: already shown in the last 24 hours",
    };
}

/// <summary>When each kind of alert was last shown.</summary>
public interface IAlertMemory
{
    DateTime? LastShownUtc(AlertKind kind);
    void MarkShown(AlertKind kind, DateTime utc);
}

public sealed class InMemoryAlertMemory : IAlertMemory
{
    private readonly Dictionary<AlertKind, DateTime> _last = new();
    public DateTime? LastShownUtc(AlertKind kind) { lock (_last) return _last.TryGetValue(kind, out var t) ? t : null; }
    public void MarkShown(AlertKind kind, DateTime utc) { lock (_last) _last[kind] = utc; }
}

/// <summary>
/// Remembers alert times in &lt;DataRoot&gt;\shell\alerts.json so a restart doesn't repeat today's alert.
/// The only class that writes this file (PC Tidy's own data).
/// </summary>
public sealed class AlertMemoryStore : IAlertMemory
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<AlertKind, DateTime>? _cache;

    public AlertMemoryStore(string dataRoot) => _path = Path.Combine(dataRoot, "shell", "alerts.json");

    public DateTime? LastShownUtc(AlertKind kind)
    {
        lock (_gate) return Load().TryGetValue(kind, out var t) ? t : null;
    }

    public void MarkShown(AlertKind kind, DateTime utc)
    {
        lock (_gate)
        {
            var map = Load();
            map[kind] = utc;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!); // boundary-ok: own data in DataRoot
                File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(map, AppSettings.Json)); // boundary-ok: own data in DataRoot
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* worst case: one extra alert after a restart */ }
        }
    }

    private Dictionary<AlertKind, DateTime> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = File.Exists(_path)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<AlertKind, DateTime>>(File.ReadAllText(_path), AppSettings.Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { _cache = new(); }
        return _cache;
    }
}
