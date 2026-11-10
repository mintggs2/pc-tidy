using System.Diagnostics.Eventing.Reader;

namespace PcTidy.Features.Schedule.Metrics;

/// <summary>
/// Reads boot events with <see cref="EventLogReader"/> and named (structured) event fields, never message text, so it
/// works in every display language. The System log is readable without admin; Diagnostics-Performance needs admin and
/// is simply reported as unreadable otherwise.
/// </summary>
public sealed class WindowsBootEventSource : IBootEventSource
{
    private const string SystemLog = "System";
    private const string PerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public IReadOnlyList<BootEvent> RecentBootEvents(int maxBoots, CancellationToken ct)
    {
        var events = new List<BootEvent>();
        // Kernel-General 12 (OS start) with its StartTime field.
        Read(SystemLog, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]", maxBoots, ct,
            "Event/EventData/Data[@Name='StartTime']",
            (time, values) => events.Add(new BootEvent(BootEventKind.OsStart, time, AsUtc(values.ElementAtOrDefault(0)))));
        // Kernel-Boot 27 (boot type).
        Read(SystemLog, "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]", maxBoots, ct,
            "Event/EventData/Data[@Name='BootType']",
            (time, values) => events.Add(new BootEvent(BootEventKind.BootType, time, BootType: AsInt(values.ElementAtOrDefault(0)))));
        // Winlogon 7001 (user sign-in). Several per boot are possible; the timeline takes the first.
        Read(SystemLog, "*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and EventID=7001]]", maxBoots * 4, ct,
            null, (time, _) => events.Add(new BootEvent(BootEventKind.SignIn, time)));
        return events;
    }

    public IReadOnlyList<BootPerformanceEvent>? RecentBootPerformance(int max, CancellationToken ct)
    {
        var events = new List<BootPerformanceEvent>();
        bool readable = Read(PerformanceLog, "*[System[EventID=100]]", max, ct,
            "Event/EventData/Data[@Name='BootTime'] | Event/EventData/Data[@Name='MainPathBootTime']",
            (time, values) =>
            {
                if (AsDouble(values.ElementAtOrDefault(0)) is { } boot)
                    events.Add(new BootPerformanceEvent(time, boot, AsDouble(values.ElementAtOrDefault(1))));
            });
        return readable ? events : null;
    }

    /// <returns>False when the log can't be opened (missing, or needs admin).</returns>
    private static bool Read(string log, string xpath, int max, CancellationToken ct, string? fields, Action<DateTime, IList<object?>> onEvent)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            using var selector = fields is null ? null : new EventLogPropertySelector(fields.Split(" | "));
            for (int n = 0; n < max; n++)
            {
                ct.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (record.TimeCreated is not { } created) continue;
                IList<object?> values = selector is not null && record is EventLogRecord r
                    ? r.GetPropertyValues(selector)!
                    : Array.Empty<object?>();
                onEvent(created.ToUniversalTime(), values);
            }
            return true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static DateTime? AsUtc(object? v) => v switch
    {
        DateTime d => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime(),
        string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) => p,
        _ => null,
    };

    private static int? AsInt(object? v) => v switch
    {
        null => null,
        IConvertible c => TryConvert(() => c.ToInt32(System.Globalization.CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static double? AsDouble(object? v) => v switch
    {
        null => null,
        IConvertible c => TryConvert(() => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static T? TryConvert<T>(Func<T> convert) where T : struct
    {
        try { return convert(); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }
}
