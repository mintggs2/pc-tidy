namespace PcTidy.Features.Schedule.Metrics;

/// <summary>One boot worked out from the log: when Windows started, which kind of boot, and when someone signed in.</summary>
public sealed record BootRecord(DateTime StartUtc, int? BootType, DateTime? SignInUtc)
{
    public TimeSpan? ToSignIn => SignInUtc is { } s && s >= StartUtc ? s - StartUtc : null;
}

/// <summary>
/// Pure logic for boot numbers (research: "Boot time and before/after numbers"):
/// boot-to-sign-in ≈ Winlogon 7001 − Kernel-General 12; only boots of the same type (cold vs Fast Startup) are compared,
/// median of 3. Sign-ins long after the boot (the PC sat at the lock screen) are left out because they measure the
/// person, not the PC.
/// </summary>
public static class BootTimeline
{
    public const int MedianOf = 3;
    /// <summary>A sign-in later than this after start-up says nothing about the PC.</summary>
    public static readonly TimeSpan MaxBootToSignIn = TimeSpan.FromMinutes(10);
    /// <summary>Kernel-Boot 27 is logged within moments of the OS start.</summary>
    private static readonly TimeSpan BootTypeWindow = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<BootRecord> Boots(IEnumerable<BootEvent> events)
    {
        var all = events.OrderBy(e => e.TimeUtc).ToList();
        var starts = all.Where(e => e.Kind == BootEventKind.OsStart).ToList();
        var boots = new List<BootRecord>();
        for (int i = 0; i < starts.Count; i++)
        {
            var start = starts[i].StartUtc ?? starts[i].TimeUtc;
            var nextStart = i + 1 < starts.Count ? starts[i + 1].StartUtc ?? starts[i + 1].TimeUtc : DateTime.MaxValue;
            var type = all.FirstOrDefault(e => e.Kind == BootEventKind.BootType && e.TimeUtc >= start && e.TimeUtc <= start + BootTypeWindow && e.TimeUtc < nextStart)?.BootType;
            var signIn = all.FirstOrDefault(e => e.Kind == BootEventKind.SignIn && e.TimeUtc >= start && e.TimeUtc < nextStart)?.TimeUtc;
            boots.Add(new BootRecord(start, type, signIn));
        }
        return boots.OrderByDescending(b => b.StartUtc).ToList();
    }

    /// <summary>
    /// Median boot-to-sign-in of the latest boots that share the newest boot's type, plus that type.
    /// Null when no usable boot is in the log.
    /// </summary>
    public static (double Seconds, int? BootType)? BootToSignIn(IEnumerable<BootEvent> events)
    {
        var usable = Boots(events).Where(b => b.ToSignIn is { } t && t <= MaxBootToSignIn).ToList();
        if (usable.Count == 0) return null;
        var type = usable[0].BootType;
        var sameType = usable.Where(b => b.BootType == type).Take(MedianOf).Select(b => b.ToSignIn!.Value.TotalSeconds).ToList();
        return (Median(sameType), type);
    }

    /// <summary>Median of the last 3 event-100 boot times, in seconds. Null when the log wasn't readable or is empty.</summary>
    public static double? ExactBootSeconds(IEnumerable<BootPerformanceEvent>? events)
    {
        var latest = events?.Where(e => e.BootTimeMs > 0).OrderByDescending(e => e.TimeUtc).Take(MedianOf).Select(e => e.BootTimeMs / 1000.0).ToList();
        return latest is { Count: > 0 } ? Median(latest) : null;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No values.", nameof(values));
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
