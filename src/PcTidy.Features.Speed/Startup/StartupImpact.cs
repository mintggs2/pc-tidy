using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Startup;

public enum ImpactLevel { NotMeasured, Low, Medium, High }

/// <summary>A startup impact, with what it was measured from.</summary>
public sealed record ImpactReading(ImpactLevel Level, TimeSpan Cpu, long DiskBytes)
{
    public static readonly ImpactReading NotMeasured = new(ImpactLevel.NotMeasured, TimeSpan.Zero, 0);

    public string Text => Level switch
    {
        ImpactLevel.NotMeasured => "Not measured",
        _ => $"{Level} (measured: {Cpu.TotalSeconds:0.0} s CPU, {Sizes.Format(DiskBytes)} disk)",
    };
}

/// <summary>
/// Startup impact with Microsoft's own thresholds (Research §4): High = more than 1 s of CPU or more than 3 MB of
/// disk I/O at startup; Medium = 0.3–1 s or 0.3–3 MB; Low = less. Measured from the CPU time and I/O of the
/// entry's processes that started with the sign-in, and only while the scan itself runs in the first minutes after
/// sign-in (the counters then still describe startup). Otherwise "Not measured" — never a guess.
/// </summary>
public sealed class StartupImpactMeter
{
    /// <summary>The scan must run within this long after sign-in for the counters to describe startup.</summary>
    public static readonly TimeSpan MeasureWindow = TimeSpan.FromMinutes(5);
    /// <summary>Processes that started this soon after sign-in count as "started with it".</summary>
    public static readonly TimeSpan StartedWithSignIn = TimeSpan.FromMinutes(3);

    private const long MB = 1024 * 1024;
    private readonly Dictionary<string, List<ProcessSnapshot>> _byPath;
    private readonly DateTime? _signIn;

    public StartupImpactMeter(ISessionClock clock, IProcessSource processes)
    {
        _signIn = clock.SignInUtc;
        CanMeasure = _signIn is { } s && clock.UtcNow - s <= MeasureWindow && clock.UtcNow >= s;
        _byPath = new Dictionary<string, List<ProcessSnapshot>>(StringComparer.OrdinalIgnoreCase);
        if (!CanMeasure) return;
        var session = processes.CurrentSessionId;
        foreach (var p in processes.Snapshot())
        {
            if (p.ImagePath is null || p.StartUtc is not { } started) continue;
            // Services run in session 0; apps in the user's session. Both count, as long as they started with the sign-in.
            if (p.SessionId != session && p.SessionId != 0) continue;
            if (started < _signIn!.Value.AddSeconds(-90) || started > _signIn.Value + StartedWithSignIn) continue;
            if (!_byPath.TryGetValue(p.ImagePath, out var list)) _byPath[p.ImagePath] = list = new List<ProcessSnapshot>();
            list.Add(p);
        }
    }

    /// <summary>False when the scan isn't in the first minutes after sign-in (so nothing can be measured honestly).</summary>
    public bool CanMeasure { get; }

    public ImpactReading Measure(string? targetPath)
    {
        if (!CanMeasure || string.IsNullOrWhiteSpace(targetPath) || !_byPath.TryGetValue(targetPath, out var procs)) return ImpactReading.NotMeasured;
        // Without CPU time or I/O for every process we can't say how heavy it was.
        if (procs.Any(p => p.Cpu is null || p.IoBytes is null)) return ImpactReading.NotMeasured;
        var cpu = TimeSpan.FromTicks(procs.Sum(p => p.Cpu!.Value.Ticks));
        var disk = procs.Sum(p => p.IoBytes!.Value);
        return new ImpactReading(Rate(cpu, disk), cpu, disk);
    }

    /// <summary>Microsoft's thresholds.</summary>
    public static ImpactLevel Rate(TimeSpan cpu, long diskBytes)
    {
        if (cpu > TimeSpan.FromSeconds(1) || diskBytes > 3 * MB) return ImpactLevel.High;
        if (cpu >= TimeSpan.FromMilliseconds(300) || diskBytes >= 3 * MB / 10) return ImpactLevel.Medium;
        return ImpactLevel.Low;
    }
}
