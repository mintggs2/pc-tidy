using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.Tests;

public class MetricsTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<BootEvent> Boot(DateTime start, int type, double signInSeconds)
    {
        yield return new BootEvent(BootEventKind.OsStart, start.AddSeconds(1), StartUtc: start);
        yield return new BootEvent(BootEventKind.BootType, start.AddSeconds(2), BootType: type);
        yield return new BootEvent(BootEventKind.SignIn, start.AddSeconds(signInSeconds));
    }

    [Fact]
    public async Task Sampler_reads_the_practice_pc_and_fake_event_sources()
    {
        using var box = new ScheduleSandbox("metrics", sim: new { RunningProcesses = new[] { "explorer", "chrome", "steam" } });
        // Startup folders of the practice PC: 2 shortcuts + desktop.ini (not counted).
        var userStartup = Path.Combine(box.K.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        box.Put(Path.Combine(userStartup, "a.lnk"), "x");
        box.Put(Path.Combine(userStartup, "desktop.ini"), "x");
        box.Put(Path.Combine(box.K.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp", "b.lnk"), "x");

        var boot = new FakeBootSource();
        boot.Events.AddRange(Boot(T0, 0, 40));                  // cold
        boot.Events.AddRange(Boot(T0.AddDays(1), 1, 20));       // fast startup
        boot.Events.AddRange(Boot(T0.AddDays(2), 0, 50));       // cold
        boot.Events.AddRange(Boot(T0.AddDays(3), 0, 30));       // cold (latest)
        boot.Performance = new List<BootPerformanceEvent> { new(T0, 30_000, null), new(T0.AddDays(1), 34_000, null), new(T0.AddDays(2), 32_000, null) };
        var perf = new FakePerformance();
        foreach (var v in new double?[] { null, 3, 5, 4, 2, 6 }) perf.Cpu.Enqueue(v);

        var sampler = new MetricsSampler(box.Host.System, perf, new StartupCounter(box.K, registry: null), boot, ScheduleSandbox.NoDelay);
        var s = await sampler.SampleAsync(MetricsOptions.BeforeAfter, CancellationToken.None);

        Assert.Equal(128L << 30, s["freeC"]);
        Assert.Equal(128L << 30, s["freeD"]);                    // Default profile: two drives
        Assert.Equal(2, s[MetricKeys.StartupItems]);
        Assert.Equal(150, s[MetricKeys.Processes]);
        Assert.Equal(8192, s[MetricKeys.AvailableMemoryMB]);
        Assert.Equal(40, s[MetricKeys.BootToSignInSeconds]);     // median of the cold boots 30, 50, 40 (fast startup left out)
        Assert.Equal(0, s[MetricKeys.BootType]);
        Assert.Equal(32, s[MetricKeys.BootSeconds]);            // median of 30, 34, 32
        Assert.Equal(4, s[MetricKeys.IdleCpuPercent]);

        var before = MetricKeys.Prefixed(s, MetricKeys.BeforePrefix);
        Assert.Equal(128L << 30, before["before.freeC"]);
        Assert.Equal(s.Count, MetricKeys.Unprefixed(before, MetricKeys.BeforePrefix).Count);
    }

    [Fact]
    public async Task Minimal_practice_pc_without_admin_or_quiet_cpu_omits_what_it_cant_measure()
    {
        using var box = new ScheduleSandbox("metrics-min", SandboxProfile.Minimal);
        var boot = new FakeBootSource { Performance = null };   // event 100 unreadable (no admin)
        var perf = new FakePerformance { MemoryMB = null };
        foreach (var v in new double?[] { null, 3, 80, 2 }) perf.Cpu.Enqueue(v); // a busy second

        var s = await new MetricsSampler(box.Host.System, perf, new StartupCounter(box.K, null), boot, ScheduleSandbox.NoDelay)
            .SampleAsync(MetricsOptions.BeforeAfter, CancellationToken.None);

        Assert.True(s.ContainsKey("freeC"));
        Assert.False(s.ContainsKey("freeD"));
        Assert.False(s.ContainsKey(MetricKeys.BootSeconds));
        Assert.False(s.ContainsKey(MetricKeys.BootToSignInSeconds));
        Assert.False(s.ContainsKey(MetricKeys.IdleCpuPercent));
        Assert.False(s.ContainsKey(MetricKeys.AvailableMemoryMB));
        Assert.Equal(0, s[MetricKeys.StartupItems]);
    }

    [Fact]
    public async Task Idle_cpu_isnt_measured_right_after_sign_in()
    {
        using var box = new ScheduleSandbox("metrics-uptime", SandboxProfile.Minimal);
        var perf = new FakePerformance { UptimeValue = TimeSpan.FromSeconds(50) };
        foreach (var v in new double?[] { null, 1, 1, 1, 1, 1 }) perf.Cpu.Enqueue(v);
        var s = await new MetricsSampler(box.Host.System, perf, new StartupCounter(box.K, null), NoBootEvents.Instance, ScheduleSandbox.NoDelay)
            .SampleAsync(MetricsOptions.BeforeAfter, CancellationToken.None);
        Assert.False(s.ContainsKey(MetricKeys.IdleCpuPercent));
    }

    [Fact]
    public void Boot_timeline_pairs_events_per_boot_and_drops_long_lock_screen_waits()
    {
        var events = Boot(T0, 0, 35).Concat(Boot(T0.AddDays(1), 0, 3600)).ToList(); // second sat at the lock screen an hour
        var boots = BootTimeline.Boots(events);
        Assert.Equal(2, boots.Count);
        Assert.Equal(T0.AddDays(1), boots[0].StartUtc);
        var r = BootTimeline.BootToSignIn(events);
        Assert.Equal(35, r!.Value.Seconds);

        // A boot without a sign-in yet, or no events at all.
        Assert.Null(BootTimeline.BootToSignIn(new[] { new BootEvent(BootEventKind.OsStart, T0, T0) }));
        Assert.Null(BootTimeline.BootToSignIn(Array.Empty<BootEvent>()));
        Assert.Null(BootTimeline.ExactBootSeconds(null));
        Assert.Equal(2.5, BootTimeline.Median(new double[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Drive_letters_from_real_roots_and_practice_folders()
    {
        Assert.Equal('C', MetricsSampler.DriveLetter(@"C:\"));
        Assert.Equal('D', MetricsSampler.DriveLetter(@"d:"));
        Assert.Equal('C', MetricsSampler.DriveLetter(@"X:\testbed\run\C\"));
        Assert.Null(MetricsSampler.DriveLetter(@"X:\testbed\run\Data\"));
        Assert.Null(MetricsSampler.DriveLetter(""));
    }

    [Fact]
    public void Before_after_says_what_improved_and_is_honest_about_boot_numbers()
    {
        var before = new Dictionary<string, double> { ["freeC"] = 40L << 30, [MetricKeys.StartupItems] = 12, [MetricKeys.BootToSignInSeconds] = 41, [MetricKeys.Processes] = 200 };
        var after = new Dictionary<string, double> { ["freeC"] = 46L << 30, [MetricKeys.StartupItems] = 9, [MetricKeys.BootToSignInSeconds] = 41, [MetricKeys.IdleCpuPercent] = 2 };

        var changes = BeforeAfter.Compare(before, after);

        Assert.Equal(new[] { "freeC", MetricKeys.BootToSignInSeconds, MetricKeys.StartupItems }, changes.Select(c => c.Key));
        var free = changes[0];
        Assert.True(free.Improved);
        Assert.Contains("40 GB → 46 GB (+6 GB)", free.Text);
        var startup = changes.Single(c => c.Key == MetricKeys.StartupItems);
        Assert.True(startup.Improved);
        Assert.Contains("12 → 9 (−3)", startup.Text);
        var bootChange = changes.Single(c => c.Key == MetricKeys.BootToSignInSeconds);
        Assert.True(bootChange.Unchanged);
        Assert.Contains("after the next restart", bootChange.Text);
    }

    [Fact]
    public void Run_before_numbers_come_from_the_run_record()
    {
        var run = Runs.Run("cleanup", DateTime.UtcNow, RunStatus.Completed) with
        {
            Metrics = new Dictionary<string, double> { ["before.freeC"] = 5, ["before.processes"] = 9, ["other"] = 1 },
        };
        var b = BeforeAfter.BeforeOf(run);
        Assert.Equal(2, b.Count);
        Assert.Equal(5, b["freeC"]);
    }

    [Fact]
    public async Task Run_proof_samples_before_and_after_saves_the_report_and_history()
    {
        using var box = new ScheduleSandbox("proof");
        var perf = new FakePerformance { Processes = 180 };
        var env = box.Environment(performance: perf);
        var proof = new RunProof(env, MetricsOptions.BeforeAfter with { IncludeIdleCpu = false });

        var before = await proof.SampleBeforeAsync("run-1", CancellationToken.None);
        Assert.Equal(180, before["before.processes"]);

        perf.Processes = 170;
        var file = Path.Combine(box.K.UserTemp, "big.tmp");
        var run = Runs.Run("cleanup", DateTime.UtcNow, RunStatus.Completed,
            Runs.Step(0, new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "Clean big.tmp", SourcePath = file, ExpectedSize = 1 << 20 }, bytes: 1 << 20))
            with { Metrics = before };
        var result = await proof.AfterRunAsync(new[] { run }, CancellationToken.None);

        var change = Assert.Single(result.Changes, c => c.Key == MetricKeys.Processes);
        Assert.Contains("180 → 170", change.Text);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains("Background processes: 180 → 170", File.ReadAllText(result.ReportPath!));
        Assert.Null(result.CopyPlan); // copying is off by default
        var history = env.Store.LoadMetrics();
        Assert.Equal(new[] { SampleKind.Before, SampleKind.After }, history.Select(h => h.Kind));

        // Simulations get nothing.
        var sim = run with { Mode = ExecutionMode.Simulate, RunId = "sim" };
        var none = await proof.AfterRunAsync(new[] { sim }, CancellationToken.None);
        Assert.Null(none.ReportPath);
    }

    [Fact]
    public async Task Run_proof_plans_the_optional_copy_only_when_switched_on()
    {
        using var box = new ScheduleSandbox("proof-copy");
        var vault = Path.Combine(box.K.Documents, "Vault", "40 Reports");
        Directory.CreateDirectory(vault);
        var settings = ScheduleSettings.Load(box.Host.Settings);
        settings.CopyReportsToFolder = true;
        settings.ReportCopyFolder = vault;
        settings.Store(box.Host.Settings);
        var proof = new RunProof(box.Environment());
        var run = Runs.Run("tweaks", DateTime.UtcNow, RunStatus.Completed, Runs.Step(0, Runs.SetDword(@"Software\X", "A", "1")));

        var result = await proof.AfterRunAsync(new[] { run }, CancellationToken.None);

        var item = Assert.Single(result.CopyPlan!.Items);
        Assert.Equal(ActionKind.WriteTextFile, item.Kind);
        Assert.True(PathUtil.IsUnder(item.DestinationPath!, vault));
        Assert.Contains("pctidy-id: run-", item.Args!["content"]);
        Assert.Empty(Directory.GetFiles(vault)); // planned, not written
    }
}
