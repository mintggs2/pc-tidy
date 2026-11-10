namespace PcTidy.Features.Speed.Sources;

// =====================================================================================
//  Everything the Speed scanners read about this PC, behind small read-only interfaces.
//  Real implementations use APIs, WMI and COM (never localized command output).
//  Tests and the sandbox use the fixed fakes in SandboxSources.cs or their own.
// =====================================================================================

/// <summary>A scheduled task that starts at sign-in or at boot (read-only view).</summary>
public sealed record TaskInfo(
    string Path,
    bool Enabled,
    bool AtLogon,
    bool AtBoot,
    string? Command,
    string? Arguments,
    string? Author,
    string? RunAs,
    bool RunsWithHighestPrivileges);

/// <summary>State of one specific task (e.g. Windows' Optimize Drives schedule).</summary>
public sealed record TaskState(bool Enabled, DateTime? LastRunUtc, DateTime? NextRunUtc);

/// <summary>Scheduled tasks, read-only. Real: Task Scheduler 2.0 COM.</summary>
public interface ITaskSource
{
    /// <summary>Tasks with a logon or boot trigger, outside <c>\Microsoft\</c>.</summary>
    IReadOnlyList<TaskInfo> StartupTasks(CancellationToken ct);
    /// <summary>Null when the task doesn't exist or can't be read.</summary>
    TaskState? GetState(string taskPath);
}

/// <summary>One running process at one moment.</summary>
public sealed record ProcessSnapshot(
    int Pid,
    string Name,
    string? ImagePath,
    int SessionId,
    DateTime? StartUtc,
    TimeSpan? Cpu,
    long PrivateBytes,
    long? IoBytes,
    bool HasMainWindow,
    string? WindowTitle);

/// <summary>The process list, read-only. Real: System.Diagnostics + limited-access process queries.</summary>
public interface IProcessSource
{
    int CurrentSessionId { get; }
    IReadOnlyList<ProcessSnapshot> Snapshot();
}

/// <summary>When the current user signed in, and the clock (tests fix both).</summary>
public interface ISessionClock
{
    DateTime UtcNow { get; }
    /// <summary>Null when Windows doesn't say.</summary>
    DateTime? SignInUtc { get; }
}

/// <summary>What a power plan is for, from Windows' own "personality" setting (never from its localized name).</summary>
public enum PowerPersonality { Unknown, Balanced, HighPerformance, PowerSaver }

public sealed record PowerScheme(Guid Id, string Name, PowerPersonality Personality);

/// <summary>Power plans and the power line, read-only. Real: powrprof + GetSystemPowerStatus.</summary>
public interface IPowerSource
{
    IReadOnlyList<PowerScheme> Schemes();
    Guid? ActiveScheme();
    /// <summary>True on battery, false plugged in, null unknown.</summary>
    bool? OnBattery();
}

/// <summary>Hardware-accelerated GPU scheduling, as the graphics driver reports it.</summary>
public sealed record GpuSchedulingSupport(bool Supported, bool EnabledNow);

/// <summary>Read-only. Real: D3DKMT WDDM 2.7 caps. Null = couldn't tell.</summary>
public interface IGpuSchedulingSource
{
    GpuSchedulingSupport? Read();
}

/// <summary>Detailed counters (admin only).</summary>
public sealed record ReliabilityInfo(int? TemperatureC, int? WearPercent, long? ReadErrors, long? WriteErrors, long? PowerOnHours);

/// <summary>One physical disk (MSFT_PhysicalDisk) and the drive letters on it.</summary>
public sealed record PhysicalDiskInfo(
    string DeviceId,
    string Name,
    DriveMedia Media,
    string BusType,
    DiskHealth Health,
    long SizeBytes,
    IReadOnlyList<char> Letters,
    ReliabilityInfo? Reliability);

public enum DiskHealth { Unknown, Healthy, Warning, Unhealthy }

/// <summary>Physical disks, read-only. Real: WMI root\Microsoft\Windows\Storage.</summary>
public interface IDiskSource
{
    /// <param name="withReliability">Also read temperature, wear and errors (works only as admin).</param>
    IReadOnlyList<PhysicalDiskInfo> PhysicalDisks(bool withReliability, CancellationToken ct);
}

/// <summary>Display names of Store apps. Real: PackageManager for the current user.</summary>
public interface IStoreAppNames
{
    /// <summary>(display name, publisher) or null.</summary>
    (string Name, string? Publisher)? Find(string packageFamilyName);
}

/// <summary>Restart Manager's kind of app (RM_APP_TYPE).</summary>
public enum LockHolderKind { Unknown = 0, MainWindow = 1, OtherWindow = 2, Service = 3, Explorer = 4, Console = 5, Critical = 1000 }

/// <summary>A process that holds a file open.</summary>
public sealed record LockHolder(int Pid, DateTime? StartUtc, string ProcessName, string AppName, LockHolderKind Kind, string? ServiceName, int SessionId);

/// <summary>Who has files open. Real: Restart Manager (read-only: it never asks anything to close).</summary>
public interface ILockFinder
{
    IReadOnlyList<LockHolder> Find(IReadOnlyList<string> files, CancellationToken ct);
}
