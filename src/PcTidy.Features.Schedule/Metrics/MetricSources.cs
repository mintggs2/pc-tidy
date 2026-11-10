namespace PcTidy.Features.Schedule.Metrics;

/// <summary>Which System-log event a boot record came from (structured fields only, never message text).</summary>
public enum BootEventKind
{
    /// <summary>Kernel-General 12: the OS started. <see cref="BootEvent.StartUtc"/> = its StartTime field.</summary>
    OsStart,
    /// <summary>Kernel-Boot 27: the boot type (BootType field: 0 cold, 1 Fast Startup, 2 resume from hibernation).</summary>
    BootType,
    /// <summary>Winlogon 7001: a user signed in.</summary>
    SignIn,
}

public sealed record BootEvent(BootEventKind Kind, DateTime TimeUtc, DateTime? StartUtc = null, int? BootType = null);

/// <summary>Diagnostics-Performance event 100 (needs admin): BootTime field in milliseconds.</summary>
public sealed record BootPerformanceEvent(DateTime TimeUtc, double BootTimeMs, double? MainPathMs);

/// <summary>Boot events, read-only. Real: the System and Diagnostics-Performance logs. Tests: fakes.</summary>
public interface IBootEventSource
{
    /// <summary>Recent OS-start, boot-type and sign-in events, any order.</summary>
    IReadOnlyList<BootEvent> RecentBootEvents(int maxBoots, CancellationToken ct);
    /// <summary>Recent event-100 records, or null when that log can't be read (no admin): the exact boot time is then omitted.</summary>
    IReadOnlyList<BootPerformanceEvent>? RecentBootPerformance(int max, CancellationToken ct);
}

/// <summary>Live performance numbers, read-only.</summary>
public interface IPerformanceSource
{
    int? ProcessCount();
    double? AvailableMemoryMB();
    /// <summary>False when CPU can't be measured here (practice PC): idle CPU is then skipped without waiting.</summary>
    bool MeasuresCpu { get; }
    /// <summary>Whole-system CPU busy % over the last interval since the previous call (first call primes it).</summary>
    double? CpuBusyPercentSinceLastCall();
    /// <summary>Time since Windows started (idle CPU is only measured once the PC has settled).</summary>
    TimeSpan? Uptime();
}

/// <summary>How many things start with Windows (enabled Run entries + Startup-folder shortcuts), read-only.</summary>
public interface IStartupCountSource
{
    int? Count(CancellationToken ct);
}

/// <summary>No boot data (practice mode without a simulated boot log).</summary>
public sealed class NoBootEvents : IBootEventSource
{
    public static readonly NoBootEvents Instance = new();
    public IReadOnlyList<BootEvent> RecentBootEvents(int maxBoots, CancellationToken ct) => Array.Empty<BootEvent>();
    public IReadOnlyList<BootPerformanceEvent>? RecentBootPerformance(int max, CancellationToken ct) => null;
}
