using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Reports;
using PcTidy.Features.Schedule.Storage;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Schedule;

/// <summary>What happened after a run: the before/after lines, the saved report, and the optional copy plan.</summary>
/// <param name="CopyPlan">A WriteTextFile plan copying the report into the user's chosen folder, or null (setting off,
/// folder missing, or already copied). The user switched this on in Settings, so the shell may run it with
/// <see cref="IFlowHost.RunApprovedAsync"/>; it is journaled and undoable like any run.</param>
public sealed record RunProofResult(IReadOnlyList<MetricChange> Changes, string? ReportPath, string? HtmlReportPath, ActionPlan? CopyPlan);

/// <summary>
/// Before/after proof and the after-every-run report (Plan "Across the whole app" + §8). For the shell's flow host:
///   var proof = RunProof.From(services);
///   var before = proof is null ? null : await proof.SampleBeforeAsync(plan.RunId, ct);  // → ExecuteOptions.MetricsBefore
///   var run = await executor.ExecuteAsync(plan, new ExecuteOptions { MetricsBefore = before }, progress, ct);
///   var after = await proof.AfterRunAsync(new[] { run }, ct);                            // Done card lines + report
/// Reads only; writes only PC Tidy's own data through <see cref="ScheduleStore"/>.
/// </summary>
public sealed class RunProof
{
    private readonly ScheduleEnvironment _env;
    private readonly MetricsOptions _options;

    public RunProof(ScheduleEnvironment env, MetricsOptions? options = null)
    {
        _env = env;
        _options = options ?? MetricsOptions.BeforeAfter;
    }

    public static RunProof? From(IServiceProvider services)
        => ScheduleEnvironment.From(services) is { } env ? new RunProof(env) : null;

    /// <summary>Samples now and returns the keys with "before." for <see cref="ExecuteOptions.MetricsBefore"/>.</summary>
    public async Task<Dictionary<string, double>> SampleBeforeAsync(string? runId, CancellationToken ct)
    {
        var sample = await _env.Sampler.SampleAsync(_options, ct).ConfigureAwait(false);
        TryStore(new MetricsHistoryEntry { TimeUtc = Now, Kind = SampleKind.Before, RunId = runId, Values = sample });
        return MetricKeys.Prefixed(sample, MetricKeys.BeforePrefix);
    }

    /// <summary>
    /// After a run (or the runs of one Optimize-everything): samples again, records the history, saves the Markdown
    /// (and HTML) report and plans the optional copy. Simulations get no report and no samples.
    /// </summary>
    public async Task<RunProofResult> AfterRunAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct)
    {
        if (runs.Count == 0 || runs.All(r => r.Mode == ExecutionMode.Simulate))
            return new RunProofResult(Array.Empty<MetricChange>(), null, null, null);

        var first = runs.OrderBy(r => r.CreatedUtc).First();
        var before = runs.Select(BeforeAfter.BeforeOf).FirstOrDefault(b => b.Count > 0) ?? new Dictionary<string, double>();
        IReadOnlyList<MetricChange> changes = Array.Empty<MetricChange>();
        if (before.Count > 0)
        {
            var after = await _env.Sampler.SampleAsync(_options, ct).ConfigureAwait(false);
            TryStore(new MetricsHistoryEntry { TimeUtc = Now, Kind = SampleKind.After, RunId = first.ParentRunId ?? first.RunId, Values = after });
            changes = BeforeAfter.Compare(before, after);
        }

        var settings = ScheduleSettings.Load(_env.Settings);
        var doc = RunReportBuilder.Build(runs, changes, RunSummaryContext.Current);
        var markdown = doc.ToMarkdown();
        string? md = null, html = null;
        try { (md, html) = _env.Store.SaveReport(doc.TimeUtc, doc.Slug, markdown, settings.SaveHtmlReports ? doc.ToHtml() : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* the run itself is fine; History still has it */ }

        ActionPlan? copy = null;
        if (settings.CopyReportsToFolder && settings.ReportCopyFolder is { Length: > 0 } folder && Directory.Exists(folder)
            && ReportCopyPlanner.Plan(doc, folder, markdown) is { } item)
            copy = new ActionPlan { FeatureId = Schedules.ScheduleIds.FeatureId, Title = "Copy report", ParentRunId = first.ParentRunId, Items = new[] { item } };
        return new RunProofResult(changes, md, html, copy);
    }

    private DateTime Now => _env.Clock.GetUtcNow().UtcDateTime;

    private void TryStore(MetricsHistoryEntry entry)
    {
        if (entry.Values.Count == 0) return;
        try { _env.Store.AppendMetrics(entry); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* history is a nice-to-have */ }
    }
}
