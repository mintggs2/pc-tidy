using PcTidy.Features.Schedule.Common;
using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Schedules;

namespace PcTidy.Features.Schedule.Tests;

/// <summary>
/// The real-PC sources only READ (Task Scheduler COM, event logs, WMI, registry, process list). These tests prove they
/// work, or degrade to "not available", on whatever PC runs them, without throwing. Nothing is changed.
/// </summary>
public class RealPcReadOnlyTests
{
    [Fact]
    public async Task Real_sampler_measures_drives_and_processes_without_changing_anything()
    {
        var system = new PcTidy.Platform.WindowsSystemInfo();
        var locations = new SandboxKnownLocations(Path.Combine(ScheduleSandbox.TestbedRoot, "unused")); // only for the Startup-folder paths
        var sampler = new MetricsSampler(system, new WindowsPerformanceSource(), new StartupCounter(locations, WindowsRegistryReader.Instance), new WindowsBootEventSource());

        var s = await sampler.SampleAsync(MetricsOptions.BeforeAfter with { IncludeIdleCpu = false }, CancellationToken.None);

        Assert.Contains(s.Keys, MetricKeys.IsFreeKey);
        Assert.True(s[MetricKeys.Processes] > 5);
        Assert.True(s[MetricKeys.AvailableMemoryMB] > 0);
        Assert.True(s[MetricKeys.StartupItems] >= 0);
        if (s.TryGetValue(MetricKeys.BootToSignInSeconds, out var boot)) Assert.InRange(boot, 0, BootTimeline.MaxBootToSignIn.TotalSeconds);
    }

    [Fact]
    public void Real_readers_degrade_quietly()
    {
        var tasks = new ComScheduledTaskReader().ReadOwnTasks(CancellationToken.None);
        Assert.All(tasks, t => Assert.StartsWith(@"\PC Tidy\", t.Path));
        _ = new WmiDiskHealthSource().Read(CancellationToken.None);
        _ = new WindowsBootEventSource().RecentBootPerformance(3, CancellationToken.None); // null without admin
        Assert.True(Enum.IsDefined(new ShellNotificationState().Current()));

        var cpu = new WindowsPerformanceSource();
        Assert.Null(cpu.CpuBusyPercentSinceLastCall()); // first call primes
        Thread.Sleep(50);
        Assert.InRange(cpu.CpuBusyPercentSinceLastCall() ?? 0, 0, 100);
    }
}
