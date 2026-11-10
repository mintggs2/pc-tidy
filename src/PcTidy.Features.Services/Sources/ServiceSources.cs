namespace PcTidy.Features.Services.Sources;

// =====================================================================================
//  Everything the Services scanner reads, behind small read-only interfaces.
//  Real: the Service Control Manager, the registry (read), process queries, WinVerifyTrust.
//  Sandbox and tests: SimulatedServices (<root>\Sim\services.json) or fakes.
// =====================================================================================

/// <summary>What the Service Control Manager says a service is doing right now.</summary>
public enum ServiceRunState { Unknown, Stopped, Starting, Stopping, Running, Resuming, Pausing, Paused }

/// <summary>
/// One installed service or driver exactly as Windows stores it (no verdicts, no flags). Raw values are kept so the
/// scanner can explain them and the plan can target the right start type.
/// </summary>
public sealed record ServiceRecord
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    /// <summary>The service's own description (QueryServiceConfig2), shown when the catalog has no plain-English text.</summary>
    public string? Description { get; init; }
    /// <summary>SERVICE_* type bits: 0x1 kernel driver, 0x2 file-system driver, 0x10 own process, 0x20 shared process, 0x40/0x80 per-user.</summary>
    public int ServiceType { get; init; } = 0x10;
    /// <summary>Raw start: 0 boot, 1 system, 2 automatic, 3 manual, 4 disabled; -1 when Windows wouldn't say.</summary>
    public int Start { get; init; } = 3;
    public bool DelayedAutostart { get; init; }
    /// <summary>Has start triggers (a device, a network change, a policy...): Windows starts it only when needed.</summary>
    public bool TriggerStart { get; init; }
    public ServiceRunState State { get; init; } = ServiceRunState.Unknown;
    /// <summary>Hosting process while running, else 0.</summary>
    public int ProcessId { get; init; }
    /// <summary>The command line Windows runs (ImagePath), as stored.</summary>
    public string? ImagePath { get; init; }
    /// <summary>For services hosted in svchost: the DLL that is the actual service (Parameters\ServiceDll).</summary>
    public string? ServiceDll { get; init; }
    /// <summary>The account it runs as (LocalSystem, NT AUTHORITY\LocalService...).</summary>
    public string? Account { get; init; }
    /// <summary>Services (and "+group" load-order groups) it needs.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    public bool IsDriver => (ServiceType & 0xF) != 0 && (ServiceType & 0x30) == 0;
    /// <summary>Per-user services: Windows makes a fresh copy for each sign-in, so a change to one wouldn't stick.</summary>
    public bool IsPerUser => (ServiceType & 0xC0) != 0;
    public bool IsSharedProcess => (ServiceType & 0x20) != 0;
    public bool StartKnown => Start is >= 0 and <= 4;

    /// <summary>The start type as PC Tidy names it (Automatic vs Automatic (delayed)).</summary>
    public ServiceStart? StartType => StartKnown
        ? Start switch
        {
            0 => ServiceStart.Boot,
            1 => ServiceStart.System,
            2 => DelayedAutostart ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
            3 => ServiceStart.Manual,
            _ => ServiceStart.Disabled,
        }
        : null;
}

/// <summary>Every installed service and driver. Real: the Service Control Manager (query rights only).</summary>
public interface IServiceSource
{
    /// <summary>All services and drivers. Problems with single services are reported in <paramref name="problems"/>, never thrown.</summary>
    IReadOnlyList<ServiceRecord> ReadAll(ICollection<string> problems, CancellationToken ct);
}

/// <summary>Memory and CPU time of one process.</summary>
/// <param name="PrivateBytes">Memory the process uses on its own (Task Manager's "Memory" column).</param>
/// <param name="CpuTime">CPU time used since it started; null when Windows wouldn't say.</param>
public sealed record ProcessUsage(int Pid, long PrivateBytes, TimeSpan? CpuTime);

/// <summary>Per-process usage, read-only. Real: the process list plus a limited-rights query per process.</summary>
public interface IProcessUsageSource
{
    /// <summary>Usage of the given processes (missing entries: ended or not readable).</summary>
    IReadOnlyDictionary<int, ProcessUsage> Read(IReadOnlyCollection<int> pids);
}
