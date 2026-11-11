using System.Reflection;
using PcTidy.Core;
using PcTidy.UI.Flow.Resume;

namespace PcTidy.App.Composition;

/// <summary>
/// Wires resumable scans (<see cref="ScanResume"/>) for each mode. Only PC Tidy's own data folder is ever written:
/// <list type="bullet">
/// <item>Real PC: checkpoints in &lt;data folder&gt;\scan-checkpoints\; Windows build and restart facts read (only read)
/// from the registry.</item>
/// <item>Practice PC (--sandbox): checkpoints in the practice PC's data folder; Windows facts come from its simulated
/// Sim\sim.json, never the real registry.</item>
/// <item>Demo: checkpoints in memory only (the demo writes nothing to disk) and invented Windows facts.</item>
/// </list>
/// </summary>
public static class ScanResumeSetup
{
    /// <summary>PC Tidy's own version; a checkpoint from another version is never reused.</summary>
    public static string AppVersion =>
        typeof(ScanResumeSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ScanResumeSetup).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>Adds a <see cref="ScanResume"/> to the services unless one is there already.</summary>
    public static ScanResume Register(AppServices services)
    {
        if (services.GetService(typeof(ScanResume)) is ScanResume existing) return existing;
        var resume = Create(services.IsDemo, services.Args.SandboxRoot is not null,
            services.GetService(typeof(IKnownLocations)) as IKnownLocations, services.GetService(typeof(ISystemInfo)) as ISystemInfo);
        services.Add(resume);
        return resume;
    }

    /// <param name="locations">The data folder comes from here (the practice PC's own in practice mode).</param>
    /// <param name="system">Only a practice PC's simulated facts are used; the real one is never asked (it would read the registry).</param>
    public static ScanResume Create(bool isDemo, bool sandboxArg, IKnownLocations? locations, ISystemInfo? system)
    {
        if (isDemo || locations is null)
            return new ScanResume(new MemoryScanCheckpointStore(), new SimulatedWindowsState("demo", restartPending: false), AppVersion);
        bool practice = sandboxArg || locations.IsSandbox;
        IWindowsStateReader windows = practice
            ? system is SandboxSystemInfo simulated ? SimulatedWindowsState.From(simulated) : new SimulatedWindowsState()
            : new RegistryWindowsStateReader();
        return new ScanResume(new FileScanCheckpointStore(locations.DataRoot), windows, AppVersion);
    }
}
