using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.Tests;

public class ScheduledScanTests
{
    [Fact]
    public async Task Runs_every_optimize_all_scanner_read_only_writes_a_report_and_never_executes()
    {
        using var box = new ScheduleSandbox("runner");
        var cache = box.Put(Path.Combine(box.K.UserTemp, "cache.bin"), new string('x', 4096));
        var before = File.ReadAllText(cache);
        var cleanup = FakeScanner.Finding("cleanup", (cache, 3L << 30), (cache + "2", 1L << 30));
        var notInOptimizeAll = new FakeScanner("other", (_, _) => throw new InvalidOperationException("must not run"), includedInOptimizeAll: false);
        var services = new SpyServices(box.Host, new FakeModule(cleanup, notInOptimizeAll));

        var result = await ScheduledScan.RunAsync(services, ScheduledScanRequest.Parse(new[] { "--scheduled-scan" }), CancellationToken.None,
            new ScheduledScanOptions { Environment = box.Environment() });

        Assert.Equal(ScheduledScanExitCodes.Done, result.ExitCode);
        // Never executed anything, never asked for anything that could.
        Assert.Equal(0, services.Executor.Calls);
        Assert.Equal(0, services.Flow.Calls);
        Assert.DoesNotContain(typeof(IExecutor), services.Requested);
        Assert.DoesNotContain(typeof(IFlowHost), services.Requested);
        Assert.Empty(await box.Host.Journal.ListAsync(CancellationToken.None));
        Assert.Equal(before, File.ReadAllText(cache));

        // Quick, real SafetyGuard, practice PC.
        var ctx = Assert.Single(cleanup.Contexts);
        Assert.Equal(ScanDepth.Quick, ctx.Depth);
        Assert.IsType<PcTidy.Safety.SafetyGuard>(ctx.Safety);
        Assert.Same(box.K, ctx.Locations);

        // Only the Safe ticked items count (the 999 MB "Check" item doesn't).
        Assert.Equal(4L << 30, result.Findings!.CleanBytes);
        Assert.StartsWith("PC Tidy found 4 GB you can clean", result.Summary);

        // Report is PC Tidy's own data in <DataRoot>\reports.
        Assert.NotNull(result.ReportPath);
        Assert.True(PathUtil.IsUnder(result.ReportPath!, DataPaths.Reports(box.Host.Settings.DataRoot)));
        var md = File.ReadAllText(result.ReportPath!);
        Assert.Contains("pctidy-id: scan-", md);
        Assert.Contains("nothing on this PC was changed", md);
        Assert.True(File.Exists(Path.ChangeExtension(result.ReportPath!, ".html")));

        // Toast text for the shell.
        var toast = result.Notification!;
        Assert.Equal("PC Tidy found 4 GB you can clean", toast.Title);
        Assert.Equal("Review", toast.ActionLabel);
        Assert.Equal("optimize", toast.NavigateTo);
        Assert.Contains("Nothing has been changed", toast.Body);
    }

    [Fact]
    public async Task One_failing_or_slow_section_doesnt_stop_the_others()
    {
        using var box = new ScheduleSandbox("runner-fail");
        var good = FakeScanner.Finding("cleanup", (Path.Combine(box.K.UserTemp, "a"), 600L << 20));
        var broken = new FakeScanner("broken", (_, _) => throw new IOException("disk went away"));
        // Review CR-25/CR-15: the slow section blocks and ignores the token (like a hung network drive), so only a real
        // timeout in the runner can get past it.
        using var stuck = new ManualResetEventSlim(false);
        var slow = new FakeScanner("slow", (_, _) => { stuck.Wait(TimeSpan.FromMinutes(5)); throw new InvalidOperationException(); });
        var services = new SpyServices(box.Host, new FakeModule(broken, slow, good));

        var result = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(false), CancellationToken.None,
            new ScheduledScanOptions { Environment = box.Environment(), PerScannerTimeout = TimeSpan.FromMilliseconds(200) });

        Assert.Equal(ScheduledScanExitCodes.Done, result.ExitCode);
        var sections = result.Findings!.Sections;
        Assert.Contains("disk went away", sections.Single(s => s.FeatureId == "broken").Problem);
        Assert.Contains("too long", sections.Single(s => s.FeatureId == "slow").Problem);
        Assert.Equal(600L << 20, sections.Single(s => s.FeatureId == "cleanup").CleanBytes);
        Assert.Contains("Couldn't finish", File.ReadAllText(result.ReportPath!));
        stuck.Set(); // let the abandoned section end
    }

    [Fact]
    public async Task Quiet_hours_suppress_the_notification_but_the_report_is_still_saved()
    {
        using var box = new ScheduleSandbox("runner-quiet");
        var services = new SpyServices(box.Host, new FakeModule(FakeScanner.Finding("cleanup", (Path.Combine(box.K.UserTemp, "a"), 8L << 30))));

        var result = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(false), CancellationToken.None,
            new ScheduledScanOptions { Environment = box.Environment(UserNotificationState.RunningFullScreenGame) });

        Assert.Equal(ScheduledScanExitCodes.Done, result.ExitCode);
        Assert.Null(result.Notification);
        Assert.Contains("game", result.NotNotifiedBecause);
        Assert.True(File.Exists(result.ReportPath));
    }

    [Fact]
    public async Task Practice_pc_default_environment_never_notifies()
    {
        using var box = new ScheduleSandbox("runner-practice", SandboxProfile.Minimal);
        var services = new SpyServices(box.Host, new FakeModule(FakeScanner.Finding("cleanup", (Path.Combine(box.K.UserTemp, "a"), 8L << 30))));

        var result = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(false), CancellationToken.None);

        Assert.Equal(ScheduledScanExitCodes.Done, result.ExitCode);
        Assert.Null(result.Notification);
        Assert.Contains("Practice PC", File.ReadAllText(result.ReportPath!));
    }

    [Fact]
    public async Task Checkup_adds_drift_disk_health_and_what_built_up_since_last_time()
    {
        using var box = new ScheduleSandbox("runner-checkup");
        var journal = new FakeJournal();
        var disks = new FakeDisks(new DiskReading { Id = "0", Name = "Test SSD", Media = DriveMedia.Ssd, Health = DiskHealthStatus.Healthy, TemperatureC = 41 });
        var size = 1L << 30;
        var scanner = new FakeScanner("cleanup", (ctx, ct) => FakeScanner.Finding("cleanup", (Path.Combine(box.K.UserTemp, "a"), size)).ScanAsync(ctx, null, ct));
        var services = new SpyServices(box.Host, new FakeModule(scanner)) { JournalOverride = journal };
        var options = new ScheduledScanOptions { Environment = box.Environment(disks: disks) };

        var first = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(true), CancellationToken.None, options);
        size = 3L << 30;
        var second = await ScheduledScan.RunAsync(services, ScheduledScanRequest.Parse(new[] { "--scheduled-scan", "--checkup" }), CancellationToken.None, options);

        Assert.Equal(ScheduledScanExitCodes.Done, second.ExitCode);
        Assert.True(second.Findings!.Checkup);
        Assert.NotNull(second.Findings.Drift);
        Assert.Equal(1, disks.Reads); // one disk reading per day
        var md = File.ReadAllText(second.ReportPath!);
        Assert.Contains("What built up", md);
        Assert.Contains("2 GB more to clean", md);
        Assert.Contains("Test SSD", md);
        Assert.Contains("pctidy-kind: checkup", md);
        Assert.NotEqual(first.ReportPath, second.ReportPath);
        var summaries = new ScheduleStore(box.Host.Settings.DataRoot).LoadScanSummaries();
        Assert.Equal(2, summaries.Count);
    }

    [Fact]
    public async Task Missing_services_and_cancellation_have_their_own_exit_codes()
    {
        using var box = new ScheduleSandbox("runner-codes");
        var services = new SpyServices(box.Host, new FakeModule(FakeScanner.Finding("cleanup")));
        services.Hidden.Add(typeof(ISafetyGuard));
        var missing = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(false), CancellationToken.None);
        Assert.Equal(ScheduledScanExitCodes.MissingServices, missing.ExitCode);

        var ok = new SpyServices(box.Host, new FakeModule(FakeScanner.Finding("cleanup")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await ScheduledScan.RunAsync(ok, new ScheduledScanRequest(false), cts.Token, new ScheduledScanOptions { Environment = box.Environment() });
        Assert.Equal(ScheduledScanExitCodes.Cancelled, cancelled.ExitCode);
        Assert.Equal(0, ok.Executor.Calls);
    }

    [Fact]
    public void Default_scanners_are_the_optimize_all_ones_once_each()
    {
        var a = FakeScanner.Finding("cleanup");
        var dup = FakeScanner.Finding("cleanup");
        var off = new FakeScanner("x", (_, _) => throw new InvalidOperationException(), includedInOptimizeAll: false);
        var services = new SpyServices(new EmptyProvider(), new FakeModule(a, off), new FakeModule(dup));
        Assert.Same(a, Assert.Single(ScheduledScan.DefaultScanners(services)));
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
