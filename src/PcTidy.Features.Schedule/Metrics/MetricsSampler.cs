namespace PcTidy.Features.Schedule.Metrics;

/// <summary>How long to watch the CPU, and how quiet it must be to count as "idle".</summary>
public sealed record MetricsOptions
{
    /// <summary>Before/after a run: a short look. The daily sample uses <see cref="Daily"/>.</summary>
    public static readonly MetricsOptions BeforeAfter = new();
    /// <summary>Scheduled sample: research recommends 30–60 s of quiet.</summary>
    public static readonly MetricsOptions Daily = new() { CpuSamples = 30 };

    public bool IncludeIdleCpu { get; init; } = true;
    /// <summary>One sample per second.</summary>
    public int CpuSamples { get; init; } = 5;
    public TimeSpan CpuInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Every sample must be at or under this, else the PC wasn't idle and the number is left out.</summary>
    public double QuietThresholdPercent { get; init; } = 25;
    /// <summary>Right after sign-in the PC is still busy starting apps: no idle number before this.</summary>
    public TimeSpan SettleAfterBoot { get; init; } = TimeSpan.FromMinutes(2);
    public bool IncludeBoot { get; init; } = true;
}

/// <summary>
/// The before/after numbers (Plan "Across the whole app" → Before/after proof): free space per drive, startup item
/// count, running processes, idle CPU (only if the PC was really quiet), free memory, start-up-to-sign-in time and,
/// when the log is readable (admin), the exact boot time. Read-only. Anything that can't be measured is left out,
/// never guessed.
/// </summary>
public sealed class MetricsSampler
{
    private readonly ISystemInfo _system;
    private readonly IPerformanceSource _performance;
    private readonly IStartupCountSource _startup;
    private readonly IBootEventSource _boot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MetricsSampler(ISystemInfo system, IPerformanceSource performance, IStartupCountSource startup, IBootEventSource boot,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _system = system;
        _performance = performance;
        _startup = startup;
        _boot = boot;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public async Task<Dictionary<string, double>> SampleAsync(MetricsOptions options, CancellationToken ct)
    {
        var sample = new Dictionary<string, double>();
        foreach (var drive in SafeDrives())
            if (DriveLetter(drive.Root) is { } letter && drive.TotalBytes > 0)
                sample[MetricKeys.FreeKey(letter)] = drive.FreeBytes;

        if (Try(() => _startup.Count(ct)) is { } startup) sample[MetricKeys.StartupItems] = startup;
        if (Try(_performance.ProcessCount) is { } processes) sample[MetricKeys.Processes] = processes;
        if (Try(_performance.AvailableMemoryMB) is { } memory) sample[MetricKeys.AvailableMemoryMB] = Math.Round(memory);

        if (options.IncludeBoot)
        {
            var events = TryRef(() => _boot.RecentBootEvents(12, ct));
            if (events is not null && BootTimeline.BootToSignIn(events) is { } toSignIn)
            {
                sample[MetricKeys.BootToSignInSeconds] = Math.Round(toSignIn.Seconds, 1);
                if (toSignIn.BootType is { } type) sample[MetricKeys.BootType] = type;
            }
            if (BootTimeline.ExactBootSeconds(TryRef(() => _boot.RecentBootPerformance(10, ct))) is { } exact)
                sample[MetricKeys.BootSeconds] = Math.Round(exact, 1);
        }

        if (options.IncludeIdleCpu && await IdleCpuAsync(options, ct).ConfigureAwait(false) is { } cpu)
            sample[MetricKeys.IdleCpuPercent] = Math.Round(cpu, 1);
        return sample;
    }

    /// <summary>Average CPU over the window, or null if any second was busier than the quiet threshold.</summary>
    private async Task<double?> IdleCpuAsync(MetricsOptions options, CancellationToken ct)
    {
        if (options.CpuSamples <= 0 || !_performance.MeasuresCpu) return null;
        if (Try(_performance.Uptime) is { } up && up < options.SettleAfterBoot) return null;
        Try(_performance.CpuBusyPercentSinceLastCall); // prime the counter
        var samples = new List<double>(options.CpuSamples);
        for (int i = 0; i < options.CpuSamples; i++)
        {
            await _delay(options.CpuInterval, ct).ConfigureAwait(false);
            if (Try(_performance.CpuBusyPercentSinceLastCall) is not { } busy) return null;
            if (busy > options.QuietThresholdPercent) return null;
            samples.Add(busy);
        }
        return samples.Average();
    }

    private IReadOnlyList<DriveDetails> SafeDrives()
    {
        try { return _system.Drives; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Array.Empty<DriveDetails>(); }
    }

    /// <summary>
    /// "C:\" → 'C'. A practice PC's drives are folders named after their letter ("…\testbed\C\"), so the folder name
    /// is used there.
    /// </summary>
    internal static char? DriveLetter(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var trimmed = root.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0])) return char.ToUpperInvariant(trimmed[0]);
        var name = Path.GetFileName(trimmed);
        return name.Length == 1 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]) : null;
    }

    private static T? Try<T>(Func<T?> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    private static T? TryRef<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or System.Security.SecurityException or NotSupportedException
                                       or System.Diagnostics.Eventing.Reader.EventLogException)
        {
            return null;
        }
    }
}
