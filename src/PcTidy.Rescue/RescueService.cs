using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Hosting;

namespace PcTidy.Rescue;

/// <summary>
/// Everything Rescue can do, independent of any window: list runs and undo / resume / roll back them, or put back
/// what's still in quarantine. Uses the same composition root (and so the same safety checks and engine) as the app.
/// Every action catches its own errors and answers with a <see cref="RescueResult"/>; nothing here throws at the caller.
/// </summary>
public sealed class RescueService : IDisposable
{
    private readonly PcTidyHost _host;
    private readonly bool _ownsHost;

    public RescueService(PcTidyHost host, RescueTarget target, bool ownsHost = true)
    {
        _host = host;
        Target = target;
        _ownsHost = ownsHost;
    }

    public RescueTarget Target { get; }
    public string DataRoot => Target.DataRoot;
    public bool IsSandbox => _host.IsSandbox;

    /// <summary>Builds the host for a target: the practice folder, or the real PC with that data folder.</summary>
    public static RescueService Open(RescueTarget target)
    {
        if (target.SandboxRoot is { } root)
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The practice folder {root} doesn't exist.");
            return new RescueService(PcTidyHost.CreateSandbox(root), target);
        }
        var settings = AppSettings.Load(target.DataRoot);
        settings.DataRoot = target.DataRoot; // the settings file may have been copied from elsewhere
        return new RescueService(PcTidyHost.CreateReal(settings), target);
    }

    // ------------------------------------------------------------------ reading

    /// <summary>All runs, newest first, with what's left in quarantine for each.</summary>
    public async Task<IReadOnlyList<RunEntry>> ListRunsAsync(CancellationToken ct = default)
    {
        var runs = await _host.Journal.ListAsync(ct);
        var quarantine = await LiveQuarantineByRunAsync(ct);
        return runs
            .OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .Select(r => quarantine.TryGetValue(r.RunId, out var q) ? new RunEntry(r, q.Count, q.Bytes) : new RunEntry(r, 0, 0))
            .ToList();
    }

    public async Task<RunEntry?> FindAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            var run = await _host.Journal.LoadAsync(runId, ct);
            if (run is null) return null;
            var live = await LiveQuarantineAsync(runId, ct);
            return new RunEntry(run, live.Count, live.Sum(e => e.SizeBytes));
        }
        catch (ArgumentException) { return null; } // not a valid run id
    }

    /// <summary>The newest run that can be undone (finished, real, with something reversible left).</summary>
    public async Task<RunEntry?> LastUndoableAsync(CancellationToken ct = default)
        => (await ListRunsAsync(ct)).FirstOrDefault(r => r.CanUndo);

    private async Task<Dictionary<string, (int Count, long Bytes)>> LiveQuarantineByRunAsync(CancellationToken ct)
    {
        try
        {
            return (await _host.Quarantine.ListAsync(null, ct))
                .Where(e => !e.Restored && !e.Purged)
                .GroupBy(e => e.RunId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(e => e.SizeBytes)), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RescueLog.Error("Reading the quarantine", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<IReadOnlyList<QuarantineEntry>> LiveQuarantineAsync(string runId, CancellationToken ct)
        => (await _host.Quarantine.ListAsync(runId, ct)).Where(e => !e.Restored && !e.Purged).ToList();

    // ------------------------------------------------------------------ actions

    public Task<RescueResult> UndoAsync(string runId, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
        => GuardedAsync("Undo", runId, async () =>
        {
            var entry = await FindAsync(runId, ct);
            if (entry is null) return RescueResult.Fail("That run wasn't found.");
            if (entry.IsInterrupted) return RescueResult.Fail("This run was interrupted. Use Resume to finish it, or Roll back to undo it.");
            if (!entry.CanUndo) return RescueResult.Fail(entry.IsReal ? "There's nothing left to undo in this run." : "Nothing to undo: it was only a simulation.");
            return FromReport("Undo", await _host.Undo.UndoRunAsync(runId, progress, ct));
        });

    public Task<RescueResult> RollbackAsync(string runId, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
        => GuardedAsync("Roll back", runId, async () =>
        {
            var entry = await FindAsync(runId, ct);
            if (entry is null) return RescueResult.Fail("That run wasn't found.");
            if (!entry.CanRollback) return RescueResult.Fail("Only an interrupted run can be rolled back. Use Undo for a finished run.");
            return FromReport("Roll back", await _host.Undo.RollbackAsync(runId, progress, ct));
        });

    public Task<RescueResult> ResumeAsync(string runId, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
        => GuardedAsync("Resume", runId, async () =>
        {
            var entry = await FindAsync(runId, ct);
            if (entry is null) return RescueResult.Fail("That run wasn't found.");
            if (!entry.CanResume) return RescueResult.Fail("Only an interrupted run can be resumed.");
            var run = await _host.Undo.ResumeAsync(runId, progress, ct);
            var problems = run.Steps.Where(s => s.Status is StepStatus.Failed or StepStatus.Skipped)
                .Select(s => $"{s.Item.Description}: {s.Message ?? s.Status.ToString()}").ToList();
            var message = $"Finished the run: {run.DoneCount} done, {run.SkippedCount} skipped, {run.FailedCount} failed.";
            return new RescueResult(run.FailedCount == 0, message, problems);
        });

    /// <summary>
    /// Puts back every item of this run that is still in quarantine, straight from the quarantine store (never overwrites:
    /// a taken name is restored as "name (restored).ext"). The fallback when an undo can't finish.
    /// </summary>
    public Task<RescueResult> RestoreQuarantineAsync(string runId, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
        => GuardedAsync("Restore quarantine", runId, async () =>
        {
            var entries = await LiveQuarantineAsync(runId, ct);
            if (entries.Count == 0) return RescueResult.Fail("Nothing from this run is in quarantine.");
            int restored = 0;
            var details = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = entries[i];
                progress?.Report(new RunProgress(i, entries.Count, "Putting back " + e.OriginalPath));
                try
                {
                    var to = await _host.Quarantine.RestoreAsync(e, ct);
                    restored++;
                    if (!string.Equals(to, e.OriginalPath, StringComparison.OrdinalIgnoreCase))
                        details.Add($"{e.OriginalPath}: the name was taken, so it was put back as {Path.GetFileName(to)}.");
                    RescueLog.Info($"Restored {e.OriginalPath} -> {to}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    details.Add($"{e.OriginalPath}: {ex.Message}");
                    RescueLog.Error($"Restoring {e.OriginalPath}", ex);
                }
            }
            var failed = entries.Count - restored;
            var message = failed == 0 ? $"Put back {restored} item(s) from quarantine." : $"Put back {restored} item(s); {failed} couldn't be put back.";
            return new RescueResult(failed == 0, message, details);
        });

    private static RescueResult FromReport(string what, UndoReport r)
    {
        string message = r.Complete
            ? $"{what} finished: {r.Restored} change(s) put back."
            : $"{what} finished: {r.Restored} put back, {r.Skipped} skipped, {r.Failed} couldn't be put back.";
        return new RescueResult(r.Failed == 0, message, r.Messages);
    }

    private static async Task<RescueResult> GuardedAsync(string what, string runId, Func<Task<RescueResult>> action)
    {
        RescueLog.Info($"{what} {runId}: starting");
        try
        {
            var result = await action();
            RescueLog.Info($"{what} {runId}: {result.Message}");
            foreach (var d in result.Details) RescueLog.Info("  " + d);
            return result;
        }
        catch (OperationCanceledException)
        {
            RescueLog.Info($"{what} {runId}: stopped by the user");
            return RescueResult.Fail("Stopped. What was already put back stays put back; you can run it again.");
        }
        catch (Exception ex)
        {
            RescueLog.Error($"{what} {runId}", ex);
            return RescueResult.Fail($"{what} didn't finish: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_ownsHost) return;
        try { _host.Dispose(); } catch { /* shutting down anyway */ }
        try { (_host.Journal as IDisposable)?.Dispose(); } catch { /* releases run locks */ }
    }
}
