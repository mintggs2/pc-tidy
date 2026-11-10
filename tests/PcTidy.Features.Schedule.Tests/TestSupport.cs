using System.Text.Json;
using PcTidy.Features.Schedule;
using PcTidy.Features.Schedule.Common;
using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Schedules;
using PcTidy.Features.Schedule.Storage;
using PcTidy.Hosting;

namespace PcTidy.Features.Schedule.Tests;

/// <summary>
/// One fake PC per test under testbed\schedule\, built with the real composition root in sandbox mode (real
/// SafetyGuard, journal, executor). Deleted afterwards.
/// </summary>
internal sealed class ScheduleSandbox : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "schedule");

    public ScheduleSandbox(string name, SandboxProfile? profile = null, object? sim = null)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Root);
        if (sim is not null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Sim"));
            File.WriteAllText(Path.Combine(Root, "Sim", "sim.json"), JsonSerializer.Serialize(sim));
        }
        Host = PcTidyHost.CreateSandbox(Root, profile ?? SandboxProfile.Default);
        K = (SandboxKnownLocations)Host.Locations;
        Directory.CreateDirectory(K.UserProfile);
    }

    public string Root { get; }
    public PcTidyHost Host { get; }
    public SandboxKnownLocations K { get; }

    public string Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The practice-PC environment, optionally with fake sources and a chosen notification state.</summary>
    public ScheduleEnvironment Environment(UserNotificationState notifications = UserNotificationState.AcceptsNotifications,
        IBootEventSource? boot = null, IPerformanceSource? performance = null, IDiskHealthSource? disks = null,
        IRegistryReader? registry = null, TimeProvider? clock = null)
    {
        var practice = ScheduleEnvironment.ForPractice(K, Host.Settings, Host.System);
        return new ScheduleEnvironment
        {
            Locations = K, Settings = Host.Settings, System = Host.System,
            Sampler = boot is null && performance is null
                ? practice.Sampler
                : new MetricsSampler(Host.System, performance ?? new SandboxPerformanceSource(Host.System), new StartupCounter(K, null), boot ?? NoBootEvents.Instance, NoDelay),
            Disks = disks ?? practice.Disks,
            Tasks = NoScheduledTasks.Instance,
            Registry = registry ?? practice.Registry,
            Notifications = new FixedNotificationState(notifications),
            Store = new ScheduleStore(Host.Settings.DataRoot),
            Clock = clock ?? TimeProvider.System,
        };
    }

    public static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    public void Dispose()
    {
        Host.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (Exception) { /* best effort; next run uses a fresh name */ }
    }

    private static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Service provider over the sandbox host, with spies for everything that could change the PC.</summary>
internal sealed class SpyServices : IServiceProvider
{
    private readonly IServiceProvider _inner;
    public SpyServices(IServiceProvider inner, params IFeatureModule[] modules)
    {
        _inner = inner;
        Modules = modules;
    }

    public IReadOnlyList<IFeatureModule> Modules { get; }
    public CountingExecutor Executor { get; } = new();
    public CountingFlowHost Flow { get; } = new();
    public IRunJournal? JournalOverride { get; init; }
    public HashSet<Type> Requested { get; } = new();
    public HashSet<Type> Hidden { get; } = new();

    public object? GetService(Type serviceType)
    {
        Requested.Add(serviceType);
        if (Hidden.Contains(serviceType)) return null;
        if (serviceType == typeof(IExecutor)) return Executor;
        if (serviceType == typeof(IFlowHost)) return Flow;
        if (serviceType == typeof(IRunJournal) && JournalOverride is not null) return JournalOverride;
        if (serviceType == typeof(IEnumerable<IFeatureModule>)) return Modules;
        return _inner.GetService(serviceType);
    }
}

internal sealed class CountingExecutor : IExecutor
{
    public int Calls { get; private set; }
    public Task<RunRecord> ExecuteAsync(ActionPlan plan, ExecuteOptions options, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        Calls++;
        throw new InvalidOperationException("A report-only scan must never execute a plan.");
    }
}

internal sealed class CountingFlowHost : IFlowHost
{
    public int Calls { get; private set; }
    public List<string> Opened { get; } = new();
    public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct) { Calls++; return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) { Calls++; return Task.FromResult<RunRecord?>(null); }
    public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct) { Calls++; return Task.FromResult<RunRecord?>(null); }
    public void OpenInExplorer(string path) => Opened.Add(path);
    public void ShowMessage(string title, string message, bool isError = false) { }
}

internal sealed class FakeScanner : IFeatureScanner
{
    private readonly Func<ScanContext, CancellationToken, Task<ScanResult>> _scan;

    public FakeScanner(string featureId, Func<ScanContext, CancellationToken, Task<ScanResult>> scan, bool includedInOptimizeAll = true)
    {
        FeatureId = featureId;
        _scan = scan;
        IncludedInOptimizeAll = includedInOptimizeAll;
    }

    public string FeatureId { get; }
    public string Title => "Section " + FeatureId;
    public Release Release => Release.V1;
    public bool IncludedInOptimizeAll { get; }
    public List<ScanContext> Contexts { get; } = new();

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        Contexts.Add(ctx);
        return _scan(ctx, ct);
    }

    /// <summary>A section that finds the given Safe, ticked quarantine items plus one "Check" item.</summary>
    public static FakeScanner Finding(string featureId, params (string Path, long Size)[] safe)
        => new(featureId, (ctx, _) => Task.FromResult(new ScanResult
        {
            FeatureId = featureId, Title = "Section " + featureId, Depth = ctx.Depth,
            Groups = new[]
            {
                new ScanGroup
                {
                    Id = "g", Title = "Group",
                    Items = safe.Select(s => new ScanItem
                    {
                        Title = Path.GetFileName(s.Path), Reason = "Rebuilt automatically.", Badge = Badge.Safe, DefaultSelected = true, SizeBytes = s.Size,
                        Actions = new[] { new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = featureId, Description = "Clean " + s.Path, SourcePath = s.Path, ExpectedSize = s.Size } },
                    }).Append(new ScanItem
                    {
                        Title = "Your call", Reason = "Might be wanted.", Badge = Badge.Check, SizeBytes = 999_999_999,
                        Actions = new[] { new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = featureId, Description = "Check item", SourcePath = "x", ExpectedSize = 999_999_999 } },
                    }).ToList(),
                },
            },
        }));
}

internal sealed class FakeModule : IFeatureModule
{
    public FakeModule(params IFeatureScanner[] scanners) => Scanners = scanners;
    public string Id => "fake";
    public string Title => "Fake";
    public string Summary => "Fake module.";
    public NavGroup Group => NavGroup.CleanUp;
    public string Icon => "Broom24";
    public int Order => 1;
    public Release Release => Release.V1;
    public IReadOnlyList<IFeatureScanner> Scanners { get; }
    public object? CreatePage(IServiceProvider services) => null;
}

internal sealed class FakeJournal : IRunJournal
{
    public List<RunRecord> Runs { get; } = new();
    public Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct) => throw new NotSupportedException();
    public Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct) => throw new NotSupportedException();
    public Task SaveRunAsync(RunRecord run, CancellationToken ct) => throw new NotSupportedException();
    public Task<RunRecord?> LoadAsync(string runId, CancellationToken ct) => Task.FromResult(Runs.FirstOrDefault(r => r.RunId == runId));
    public Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunRecord>>(Runs.ToList());
    public Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
}

internal sealed class FakeBootSource : IBootEventSource
{
    public List<BootEvent> Events { get; } = new();
    public List<BootPerformanceEvent>? Performance { get; set; }
    public IReadOnlyList<BootEvent> RecentBootEvents(int maxBoots, CancellationToken ct) => Events;
    public IReadOnlyList<BootPerformanceEvent>? RecentBootPerformance(int max, CancellationToken ct) => Performance;
}

internal sealed class FakePerformance : IPerformanceSource
{
    public int? Processes { get; set; } = 150;
    public double? MemoryMB { get; set; } = 8192.4;
    public Queue<double?> Cpu { get; } = new();
    public TimeSpan? UptimeValue { get; set; } = TimeSpan.FromHours(3);
    public bool MeasuresCpu => true;
    public int? ProcessCount() => Processes;
    public double? AvailableMemoryMB() => MemoryMB;
    public double? CpuBusyPercentSinceLastCall() => Cpu.Count > 0 ? Cpu.Dequeue() : null;
    public TimeSpan? Uptime() => UptimeValue;
}

internal sealed class FakeDisks(params DiskReading[] disks) : IDiskHealthSource
{
    public int Reads { get; private set; }
    public IReadOnlyList<DiskReading> Read(CancellationToken ct) { Reads++; return disks; }
}

internal sealed class FixedClock(DateTime utc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}

internal static class Runs
{
    public static StepRecord Step(int index, PlanItem item, StepStatus status = StepStatus.Done, long bytes = 0, string? message = null)
        => new() { Index = index, Item = item, Status = status, BytesAffected = bytes, Message = message, FinishedUtc = DateTime.UtcNow };

    public static RunRecord Run(string feature, DateTime finishedUtc, RunStatus status, params StepRecord[] steps)
        => new()
        {
            RunId = ActionPlan.NewRunId(), FeatureId = feature, Title = feature, Mode = ExecutionMode.Real,
            CreatedUtc = finishedUtc.AddMinutes(-1), FinishedUtc = finishedUtc, Status = status, Steps = steps,
        };

    public static PlanItem SetDword(string key, string name, string data, string feature = "tweaks") => new()
    {
        Kind = ActionKind.RegistrySetValue, FeatureId = feature, Description = $"Set {name}",
        Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = key, ValueName = name, Kind = RegistryKind.DWord, Data = data },
        IsSystemChange = true,
    };

    public static PlanItem SetString(string key, string name, string data) => new()
    {
        Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = $"Set {name}",
        Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = key, ValueName = name, Kind = RegistryKind.String, Data = data },
        IsSystemChange = true,
    };

    public static PlanItem DeleteValue(string key, string name) => new()
    {
        Kind = ActionKind.RegistryDeleteValue, FeatureId = "tweaks.badtweaks", Description = $"Remove {name}",
        Registry = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = key, ValueName = name, Kind = RegistryKind.DWord },
        IsSystemChange = true,
    };
}
