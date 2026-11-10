using PcTidy.Features.Schedule.Common;
using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Schedules;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule;

/// <summary>
/// Every read-only source this section uses, picked for the real PC or a practice PC. On a practice PC
/// (<see cref="SandboxKnownLocations"/>) nothing of the real PC is read except PC Tidy's own test registry keys that
/// tweaks were redirected to: no real tasks, boot log, disks or startup registry, and no notification pops up.
/// </summary>
public sealed class ScheduleEnvironment
{
    public required IKnownLocations Locations { get; init; }
    public required AppSettings Settings { get; init; }
    public required ISystemInfo System { get; init; }
    public required MetricsSampler Sampler { get; init; }
    public required IDiskHealthSource Disks { get; init; }
    public required IScheduledTaskReader Tasks { get; init; }
    /// <summary>For the tweak drift check: reads where the tweaks were written (the test key on a practice PC).</summary>
    public required IRegistryReader Registry { get; init; }
    public required INotificationState Notifications { get; init; }
    public required ScheduleStore Store { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public bool IsPractice => Locations.IsSandbox;

    /// <summary>Null when the services this section needs aren't registered.</summary>
    public static ScheduleEnvironment? From(IServiceProvider services)
    {
        if (services.GetService(typeof(IKnownLocations)) is not IKnownLocations locations) return null;
        var settings = services.GetService(typeof(AppSettings)) as AppSettings ?? new AppSettings { DataRoot = locations.DataRoot };
        var system = services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        return locations is SandboxKnownLocations sandbox
            ? ForPractice(sandbox, settings, system ?? new SandboxSystemInfo(sandbox))
            : system is null ? null : ForRealPc(locations, settings, system);
    }

    public static ScheduleEnvironment ForRealPc(IKnownLocations locations, AppSettings settings, ISystemInfo system) => new()
    {
        Locations = locations, Settings = settings, System = system,
        Sampler = new MetricsSampler(system, new WindowsPerformanceSource(), new StartupCounter(locations, WindowsRegistryReader.Instance), new WindowsBootEventSource()),
        Disks = new WmiDiskHealthSource(),
        Tasks = new ComScheduledTaskReader(),
        Registry = WindowsRegistryReader.Instance,
        Notifications = new ShellNotificationState(),
        Store = new ScheduleStore(settings.DataRoot),
    };

    public static ScheduleEnvironment ForPractice(SandboxKnownLocations locations, AppSettings settings, ISystemInfo system) => new()
    {
        Locations = locations, Settings = settings, System = system,
        Sampler = new MetricsSampler(system, new SandboxPerformanceSource(system), new StartupCounter(locations, registry: null), NoBootEvents.Instance),
        Disks = new SimulatedDiskHealthSource(locations),
        Tasks = NoScheduledTasks.Instance,
        Registry = WindowsRegistryReader.Instance,
        Notifications = new FixedNotificationState(UserNotificationState.NotPresent),
        Store = new ScheduleStore(settings.DataRoot),
    };
}
