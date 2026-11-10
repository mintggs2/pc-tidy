using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Storage;

namespace PcTidy.Features.Schedule.Tests;

/// <summary>
/// Review CR-14: the app and the scheduled-scan process share the history files. Appends are serialised across processes,
/// a history that can't be read is never rewritten, a damaged one falls back to its .bak, and a failed history write
/// never costs the scheduled scan its report. Everything runs in testbed\schedule.
/// </summary>
public class HistoryStoreTests
{
    private static MetricsHistoryEntry Entry(int day, double value) => new()
    {
        TimeUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day), Kind = SampleKind.Daily,
        Values = new Dictionary<string, double> { ["boot"] = value },
    };

    private static ScheduleStore Fast(string dataRoot) => new(dataRoot)
    {
        LockTimeout = TimeSpan.FromMilliseconds(300), IoAttempts = 2, IoRetryDelay = TimeSpan.FromMilliseconds(20),
    };

    private static string MetricsFile(ScheduleStore s) => Path.Combine(s.OwnFolder, "metrics-history.json");

    [Fact]
    public void Two_stores_on_the_same_folder_never_lose_each_others_entries()
    {
        using var box = new ScheduleSandbox("history-race");
        var app = new ScheduleStore(box.Host.Settings.DataRoot);
        var scheduled = new ScheduleStore(box.Host.Settings.DataRoot); // stands in for the second PcTidy.exe

        Parallel.For(0, 40, i => (i % 2 == 0 ? app : scheduled).AppendMetrics(Entry(i, i)));

        Assert.Equal(40, app.LoadMetrics().Count);
        Assert.Equal(Enumerable.Range(0, 40).Select(i => (double)i).OrderBy(v => v), app.LoadMetrics().Select(m => m.Values["boot"]).OrderBy(v => v));
    }

    [Fact]
    public void The_lock_is_per_data_folder_and_shared_by_name()
    {
        Assert.Equal(ScheduleStore.HistoryMutexName(@"C:\Data\PC Tidy"), ScheduleStore.HistoryMutexName(@"c:\data\pc tidy\"));
        Assert.NotEqual(ScheduleStore.HistoryMutexName(@"C:\Data\PC Tidy"), ScheduleStore.HistoryMutexName(@"D:\Other"));
        Assert.StartsWith(@"Local\PCTidy-schedule-", ScheduleStore.HistoryMutexName(@"C:\Data\PC Tidy"));
    }

    [Fact]
    public void An_append_waits_for_the_other_process_and_gives_up_without_touching_the_file()
    {
        using var box = new ScheduleSandbox("history-lock");
        var store = Fast(box.Host.Settings.DataRoot);
        store.AppendMetrics(Entry(0, 1));
        var before = File.ReadAllText(MetricsFile(store));

        // Another process holds the history lock (a thread owning the same named mutex).
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var m = new Mutex(false, ScheduleStore.HistoryMutexName(box.Host.Settings.DataRoot));
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        other.Start();
        held.Wait();
        try
        {
            Assert.Throws<IOException>(() => store.AppendMetrics(Entry(1, 2)));
            Assert.Equal(before, File.ReadAllText(MetricsFile(store)));
        }
        finally
        {
            release.Set();
            other.Join();
        }
        store.AppendMetrics(Entry(1, 2)); // once the other side is done, it works
        Assert.Equal(2, store.LoadMetrics().Count);
    }

    [Fact]
    public void A_history_another_program_holds_open_is_never_overwritten()
    {
        using var box = new ScheduleSandbox("history-busy");
        var store = Fast(box.Host.Settings.DataRoot);
        for (var i = 0; i < 5; i++) store.AppendMetrics(Entry(i, i));

        using (new FileStream(MetricsFile(store), FileMode.Open, FileAccess.Read, FileShare.None)) // like antivirus holding it
            Assert.Throws<IOException>(() => store.AppendMetrics(Entry(9, 9)));

        Assert.Equal(5, store.LoadMetrics().Count); // years of history are still there
        store.AppendMetrics(Entry(9, 9));
        Assert.Equal(6, store.LoadMetrics().Count);
    }

    [Fact]
    public void A_damaged_history_falls_back_to_its_backup()
    {
        using var box = new ScheduleSandbox("history-bak");
        var store = Fast(box.Host.Settings.DataRoot);
        for (var i = 0; i < 3; i++) store.AppendMetrics(Entry(i, i)); // the .bak now holds the first two entries
        Assert.True(File.Exists(MetricsFile(store) + ".bak"));

        File.WriteAllText(MetricsFile(store), "{ this is not json");
        Assert.Equal(2, store.LoadMetrics().Count); // shown from the backup
        store.AppendMetrics(Entry(5, 5));
        Assert.Equal(new[] { 0d, 1d, 5d }, store.LoadMetrics().Select(m => m.Values["boot"]).ToArray());
    }

    [Fact]
    public void A_damaged_history_without_a_backup_is_set_aside_not_deleted()
    {
        using var box = new ScheduleSandbox("history-aside");
        var store = Fast(box.Host.Settings.DataRoot);
        Directory.CreateDirectory(store.OwnFolder);
        File.WriteAllText(MetricsFile(store), "[ broken");

        store.AppendMetrics(Entry(0, 7));
        Assert.Single(store.LoadMetrics());
        var aside = Assert.Single(Directory.GetFiles(store.OwnFolder, "metrics-history.damaged-*.json"));
        Assert.Equal("[ broken", File.ReadAllText(aside));
    }

    [Fact]
    public async Task A_history_that_cant_be_written_doesnt_cost_the_scheduled_scan_its_report()
    {
        using var box = new ScheduleSandbox("history-scan");
        var e = box.Environment();
        var env = new ScheduleEnvironment
        {
            Locations = e.Locations, Settings = e.Settings, System = e.System, Sampler = e.Sampler, Disks = e.Disks, Tasks = e.Tasks,
            Registry = e.Registry, Notifications = e.Notifications, Clock = e.Clock, Store = Fast(box.Host.Settings.DataRoot),
        };
        var services = new SpyServices(box.Host, new FakeModule(FakeScanner.Finding("cleanup", (Path.Combine(box.K.UserTemp, "a"), 600L << 20))));
        var scanFile = Path.Combine(env.Store.OwnFolder, "scan-history.json");
        Directory.CreateDirectory(env.Store.OwnFolder);
        File.WriteAllText(scanFile, "[]");

        ScheduledScanResult result;
        using (new FileStream(scanFile, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await ScheduledScan.RunAsync(services, new ScheduledScanRequest(false), CancellationToken.None, new ScheduledScanOptions { Environment = env });

        Assert.Equal(ScheduledScanExitCodes.Done, result.ExitCode);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains(result.HistoryProblems, p => p.StartsWith("Scan history", StringComparison.Ordinal));
        Assert.Contains("history couldn't be updated", result.Summary);
        Assert.Equal("[]", File.ReadAllText(scanFile)); // left exactly as it was
    }

    [Fact]
    public void The_drift_check_covers_privacy_and_notification_switches_too()
    {
        Assert.True(TweakDriftChecker.IsTweakFeature("tweaks"));
        Assert.True(TweakDriftChecker.IsTweakFeature("tweaks.badtweaks"));
        Assert.True(TweakDriftChecker.IsTweakFeature("privacy"));
        Assert.True(TweakDriftChecker.IsTweakFeature("notifications"));
        Assert.False(TweakDriftChecker.IsTweakFeature("cleanup"));
        Assert.Contains("Windows update", DriftReport.Empty.Advice);
    }
}
