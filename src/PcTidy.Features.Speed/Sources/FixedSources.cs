namespace PcTidy.Features.Speed.Sources;

// Fixed, in-memory sources for the sandbox (testbed) and tests: they read nothing from the real PC.

public sealed class FixedTaskSource : ITaskSource
{
    public FixedTaskSource(IEnumerable<TaskInfo>? tasks = null, IReadOnlyDictionary<string, TaskState>? states = null)
    {
        Tasks = tasks?.ToList() ?? new List<TaskInfo>();
        States = states ?? new Dictionary<string, TaskState>();
    }

    public IReadOnlyList<TaskInfo> Tasks { get; }
    public IReadOnlyDictionary<string, TaskState> States { get; }

    public IReadOnlyList<TaskInfo> StartupTasks(CancellationToken ct) => Tasks;
    public TaskState? GetState(string taskPath) => States.TryGetValue(taskPath, out var s) ? s : null;
}

public sealed class FixedProcessSource : IProcessSource
{
    private readonly Func<int, IReadOnlyList<ProcessSnapshot>> _snapshot;
    private int _calls;

    /// <param name="snapshot">Gets the call number (0, 1, …) so tests can make CPU time move between samples.</param>
    public FixedProcessSource(Func<int, IReadOnlyList<ProcessSnapshot>>? snapshot = null, int sessionId = 1)
    {
        _snapshot = snapshot ?? (_ => Array.Empty<ProcessSnapshot>());
        CurrentSessionId = sessionId;
    }

    public FixedProcessSource(IReadOnlyList<ProcessSnapshot> processes, int sessionId = 1) : this(_ => processes, sessionId) { }

    public int CurrentSessionId { get; }
    public IReadOnlyList<ProcessSnapshot> Snapshot() => _snapshot(Interlocked.Increment(ref _calls) - 1);
}

public sealed class FixedSessionClock(DateTime utcNow, DateTime? signInUtc) : ISessionClock
{
    public DateTime UtcNow { get; } = utcNow;
    public DateTime? SignInUtc { get; } = signInUtc;
}

public sealed class FixedPowerSource : IPowerSource
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public FixedPowerSource(Guid? active = null, bool? onBattery = false, IReadOnlyList<PowerScheme>? schemes = null)
    {
        SchemeList = schemes ?? new[]
        {
            new PowerScheme(Balanced, "Balanced", PowerPersonality.Balanced),
            new PowerScheme(HighPerformance, "High performance", PowerPersonality.HighPerformance),
            new PowerScheme(PowerSaver, "Power saver", PowerPersonality.PowerSaver),
        };
        Active = active ?? Balanced;
        Battery = onBattery;
    }

    public IReadOnlyList<PowerScheme> SchemeList { get; }
    public Guid? Active { get; }
    public bool? Battery { get; }

    public IReadOnlyList<PowerScheme> Schemes() => SchemeList;
    public Guid? ActiveScheme() => Active;
    public bool? OnBattery() => Battery;
}

public sealed class FixedGpuScheduling(GpuSchedulingSupport? support) : IGpuSchedulingSource
{
    public GpuSchedulingSupport? Read() => support;
}

public sealed class FixedDiskSource(IReadOnlyList<PhysicalDiskInfo>? disks = null) : IDiskSource
{
    public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks(bool withReliability, CancellationToken ct)
        => (disks ?? Array.Empty<PhysicalDiskInfo>()).Select(d => withReliability ? d : d with { Reliability = null }).ToList();
}

public sealed class NoStoreAppNames : IStoreAppNames
{
    public (string Name, string? Publisher)? Find(string packageFamilyName) => null;
}

public sealed class FixedLockFinder(IReadOnlyList<LockHolder>? holders = null) : ILockFinder
{
    public IReadOnlyList<LockHolder> Find(IReadOnlyList<string> files, CancellationToken ct) => holders ?? Array.Empty<LockHolder>();
}
