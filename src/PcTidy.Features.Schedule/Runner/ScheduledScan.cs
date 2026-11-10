using PcTidy.Features.Schedule.DiskHealth;
using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Reports;
using PcTidy.Features.Schedule.Schedules;
using PcTidy.Features.Schedule.Storage;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Schedule.Runner;

/// <summary>
/// Process exit codes of <c>PcTidy.exe --scheduled-scan [--checkup]</c>. Task Scheduler shows them as "Last run result".
/// 1 (unexpected error, crash log written) and 2 (mode not wired) come from the shell itself.
/// </summary>
public static class ScheduledScanExitCodes
{
    /// <summary>Scan finished and its report was saved (whether or not anything was found or notified).</summary>
    public const int Done = 0;
    /// <summary>PC Tidy's services (locations, safety, file walker…) weren't available: nothing was scanned.</summary>
    public const int MissingServices = 3;
    /// <summary>The scan finished but its report couldn't be saved in the data folder.</summary>
    public const int ReportNotSaved = 4;
    /// <summary>Stopped before it finished (Windows ended the task, or the PC shut down).</summary>
    public const int Cancelled = 5;
}

/// <summary>What the command line (or the page) asked for.</summary>
/// <param name="Manual">Started with "Scan now" / "Checkup now" on the page, not by a schedule: the report says so.</param>
public sealed record ScheduledScanRequest(bool Checkup, bool Manual = false)
{
    /// <summary>Only PC Tidy's own switches are understood; anything else is ignored (never executed).</summary>
    public static ScheduledScanRequest Parse(IEnumerable<string> args)
        => new(args.Any(a => string.Equals(a, ScheduleIds.CheckupSwitch, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>What the shell shows as a Windows notification. Null on the result when it shouldn't notify.</summary>
/// <param name="NavigateTo">Shell page key for the Review button ("optimize" or "schedule").</param>
public sealed record ScanNotification(string Title, string Body, string ActionLabel, string NavigateTo, string? ReportPath);

public sealed record ScheduledScanResult(int ExitCode, string Summary, ScanNotification? Notification, string? ReportPath, ScanFindings? Findings)
{
    /// <summary>Why there's no notification (quiet hours, nothing worth saying), or null when there is one.</summary>
    public string? NotNotifiedBecause { get; init; }
    /// <summary>History files that couldn't be updated this time (the scan and its report are fine; the shell logs these).</summary>
    public IReadOnlyList<string> HistoryProblems { get; init; } = Array.Empty<string>();
}

/// <summary>Tunables for the runner (tests use short ones).</summary>
public sealed record ScheduledScanOptions
{
    /// <summary>One slow section can't hold up the rest.</summary>
    public TimeSpan PerScannerTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public MetricsOptions DailyMetrics { get; init; } = MetricsOptions.Daily;
    /// <summary>Override the scanners (tests). Default: every IncludedInOptimizeAll scanner of the visible modules.</summary>
    public IReadOnlyList<IFeatureScanner>? Scanners { get; init; }
    /// <summary>Override the environment (tests). Default: <see cref="ScheduleEnvironment.From"/>.</summary>
    public ScheduleEnvironment? Environment { get; init; }
}

/// <summary>
/// The window-less <c>--scheduled-scan</c> mode (Plan §8 "Schedule and report", "Monthly checkup"): runs every
/// Optimize-everything scanner in Quick mode, READ-ONLY, writes a report into &lt;DataRoot&gt;\reports, keeps the daily
/// metrics and disk-health samples, and (monthly checkup) checks whether tweaks were changed back. It never asks for
/// IExecutor or IFlowHost, so it cannot run a plan; nothing on the PC is changed. The shell shows the returned
/// notification (only when Windows says notifications are welcome) and exits with <see cref="ScheduledScanResult.ExitCode"/>.
/// </summary>
public static class ScheduledScan
{
    public static async Task<ScheduledScanResult> RunAsync(IServiceProvider services, ScheduledScanRequest request, CancellationToken ct, ScheduledScanOptions? options = null)
    {
        options ??= new ScheduledScanOptions();
        var env = options.Environment ?? ScheduleEnvironment.From(services);
        var context = env is null ? null : CreateContext(services, env);
        if (env is null || context is null)
            return new ScheduledScanResult(ScheduledScanExitCodes.MissingServices, "PC Tidy couldn't start its scan: some of its parts weren't available.", null, null, null);

        try
        {
            var now = env.Clock.GetUtcNow().UtcDateTime;
            var settings = ScheduleSettings.Load(env.Settings);
            var scanners = options.Scanners ?? DefaultScanners(services);

            var sections = new List<SectionFinding>();
            foreach (var scanner in scanners)
            {
                ct.ThrowIfCancellationRequested();
                sections.Add(await ScanOneAsync(scanner, context, options.PerScannerTimeout, ct).ConfigureAwait(false));
            }

            DriftReport? drift = null;
            if (request.Checkup && services.GetService(typeof(IRunJournal)) is IRunJournal journal)
                drift = await SafeDriftAsync(new TweakDriftChecker(journal, env.Registry), ct).ConfigureAwait(false);

            // History appends are optional extras (review CR-14): a busy or unreadable history file is noted and the scan
            // and its report carry on. The store never rewrites a history it couldn't read.
            var historyProblems = new List<string>();
            void History(string what, Action append)
            {
                try { append(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    historyProblems.Add($"{what}: {ex.Message}");
                }
            }

            var disks = SampleDisksIfDue(env, settings, now, ct, History);
            var diskHistory = env.Store.LoadDiskHealth();
            Dictionary<string, double>? metrics = null;
            if (env.Store.DailyMetricsDueToday(now) || request.Checkup)
            {
                metrics = await env.Sampler.SampleAsync(options.DailyMetrics, ct).ConfigureAwait(false);
                var sample = metrics;
                if (sample.Count > 0) History("Speed history", () => env.Store.AppendMetrics(new MetricsHistoryEntry { TimeUtc = now, Kind = SampleKind.Daily, Values = sample }));
            }

            // One time for this scan everywhere (Last scan, the report, its file): when it finished.
            var finishedUtc = env.Clock.GetUtcNow().UtcDateTime;
            var findings = new ScanFindings
            {
                TimeUtc = finishedUtc, Checkup = request.Checkup, Manual = request.Manual, Sections = sections,
                Previous = env.Store.LoadScanSummaries().OrderByDescending(s => s.TimeUtc).FirstOrDefault(),
                Drift = drift,
                Disks = disks ?? diskHistory.OrderByDescending(s => s.TimeUtc).FirstOrDefault()?.Disks ?? Array.Empty<DiskReading>(),
                DiskWarnings = DiskHealthAdvice.Warnings(diskHistory),
                Metrics = metrics,
                PracticeMode = env.IsPractice,
            };

            string? reportPath;
            try
            {
                var doc = ScanReportBuilder.Build(findings);
                reportPath = env.Store.SaveReport(finishedUtc, doc.Slug, doc.ToMarkdown(), settings.SaveHtmlReports ? doc.ToHtml() : null).Markdown;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new ScheduledScanResult(ScheduledScanExitCodes.ReportNotSaved, "The scan finished but its report couldn't be saved: " + ex.Message, null, null, findings)
                {
                    HistoryProblems = historyProblems,
                };
            }
            // The report is saved: a failed scan-history entry only means next week's "since last time" has less to compare.
            History("Scan history", () => env.Store.AppendScanSummary(findings.ToSummary(reportPath)));

            var (notification, whyNot) = Notify(findings, settings, env.Notifications.Current(), reportPath);
            var summary = findings.Headline + ". Nothing was changed."
                          + (historyProblems.Count > 0 ? " Some history couldn't be updated this time (" + string.Join("; ", historyProblems) + ")." : "");
            return new ScheduledScanResult(ScheduledScanExitCodes.Done, summary, notification, reportPath, findings)
            {
                NotNotifiedBecause = whyNot,
                HistoryProblems = historyProblems,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ScheduledScanResult(ScheduledScanExitCodes.Cancelled, "The scan was stopped before it finished. Nothing was changed.", null, null, null);
        }
    }

    /// <summary>
    /// Decides whether to notify and what to say. Only when Windows accepts notifications, and only when there's
    /// something worth a look: enough to clean, tweaks changed back, or a disk warning. Calm wording, no urgency.
    /// </summary>
    public static (ScanNotification? Notification, string? WhyNot) Notify(ScanFindings f, ScheduleSettings settings, UserNotificationState state, string? reportPath)
    {
        int drifted = f.Drift?.Drifted.Count ?? 0;
        bool enough = f.CleanBytes >= settings.NotifyMinBytes;
        if (!enough && drifted == 0 && f.DiskWarnings.Count == 0)
            return (null, "No notification: nothing worth a look this time.");
        if (!QuietHours.ShouldNotify(state)) return (null, QuietHours.WhyNot(state));

        const string body = "Nothing has been changed. Review it when it suits you.";
        if (enough) return (new ScanNotification(f.Headline, body, "Review", "optimize", reportPath), null);
        if (drifted > 0)
            return (new ScanNotification($"{drifted} of your tweaks {(drifted == 1 ? "was" : "were")} changed back", "Usually a Windows update does this. " + body, "Review", "schedule", reportPath), null);
        return (new ScanNotification("A disk needs a look", f.DiskWarnings[0], "Review", "schedule", reportPath), null);
    }

    /// <summary>Every visible module's Optimize-everything scanner, once each.</summary>
    public static IReadOnlyList<IFeatureScanner> DefaultScanners(IServiceProvider services)
        => (services.GetService(typeof(IEnumerable<IFeatureModule>)) as IEnumerable<IFeatureModule> ?? Array.Empty<IFeatureModule>())
            .SelectMany(m => m.Scanners)
            .Where(s => s.IncludedInOptimizeAll)
            .GroupBy(s => s.FeatureId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

    private static ScanContext? CreateContext(IServiceProvider services, ScheduleEnvironment env)
    {
        if (services.GetService(typeof(ISafetyGuard)) is not ISafetyGuard guard) return null;
        if (services.GetService(typeof(IInUseChecker)) is not IInUseChecker inUse) return null;
        var walker = services.GetService(typeof(IFileWalker)) as IFileWalker ?? new ManagedFileWalker();
        IReadOnlySet<string> running;
        try { running = env.System.RunningProcesses(); } catch (InvalidOperationException) { running = new HashSet<string>(); }
        return new ScanContext
        {
            Locations = env.Locations, Safety = guard, InUse = inUse, Settings = env.Settings, Walker = walker,
            Depth = ScanDepth.Quick, System = env.System, RunningProcesses = running,
        };
    }

    private static async Task<SectionFinding> ScanOneAsync(IFeatureScanner scanner, ScanContext context, TimeSpan timeout, CancellationToken ct)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var tooLong = new SectionFinding(scanner.FeatureId, scanner.Title, 0, 0, 0, Array.Empty<string>(), "it took too long and was stopped; it will try again next time.");
        try
        {
            // Review CR-15: a scanner stuck in a blocking call ignores the token, so don't wait for it past the timeout.
            // Its task is left to finish (or not) on its own; the scan moves on and the process ends when the scan does.
            var scan = Task.Run(() => scanner.ScanAsync(context, null, limit.Token), CancellationToken.None);
            var finished = await Task.WhenAny(scan, Task.Delay(timeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != scan)
            {
                limit.Cancel();
                _ = scan.ContinueWith(t => { _ = t.Exception; limit.Dispose(); }, TaskScheduler.Default); // observe a late failure
                return tooLong;
            }
            limit.Dispose();
            return Summarize(scanner, await scan.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return tooLong;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One section failing never stops the others; the report says which and why.
            return new SectionFinding(scanner.FeatureId, scanner.Title, 0, 0, 0, Array.Empty<string>(), "it couldn't finish (" + ex.Message + ").");
        }
    }

    /// <summary>
    /// "Can clean" = bytes of the items the standard flow would tick by default (Safe, scanner-ticked, reversible),
    /// counted with the same <see cref="PlanCounts"/> as the bottom bar and the confirm dialog.
    /// </summary>
    internal static SectionFinding Summarize(IFeatureScanner scanner, ScanResult result)
    {
        var offered = result.AllItems.Where(i => i.DisabledReason is null && i.Actions.Count > 0).ToList();
        var safe = offered.Where(OptimizePresets.IsTickableByDefault).ToList();
        var bytes = PlanCounts.Of(safe.SelectMany(i => i.Actions)).CleanBytes;
        return new SectionFinding(result.FeatureId, string.IsNullOrWhiteSpace(result.Title) ? scanner.Title : result.Title,
            bytes, safe.Count, offered.Count - safe.Count, result.Notes, null);
    }

    private static async Task<DriftReport?> SafeDriftAsync(TweakDriftChecker checker, CancellationToken ct)
    {
        try { return await checker.CheckAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null; // the report just leaves the tweaks part out
        }
    }

    private static IReadOnlyList<DiskReading>? SampleDisksIfDue(ScheduleEnvironment env, ScheduleSettings settings, DateTime nowUtc, CancellationToken ct,
        Action<string, Action> history)
    {
        if (!settings.DiskHealthHistory || !env.Store.DiskSampleDueToday(nowUtc)) return null;
        var disks = env.Disks.Read(ct);
        if (disks.Count > 0) history("Disk health history", () => env.Store.AppendDiskHealth(new DiskHealthSample(nowUtc, disks)));
        return disks;
    }
}
