using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;

namespace PcTidy.Engine;

/// <summary>
/// Undo (replay a run backwards), Resume (finish an interrupted run) and Roll back (undo an interrupted run).
/// Each handler checks the item is still exactly what it moved (size + modified time, + hash if recorded)
/// before moving anything back, puts it back only where the journaled plan says it came from, and never
/// overwrites ("name (restored).ext").
/// </summary>
public sealed class UndoEngine : IUndoEngine
{
    private readonly Executor _executor;

    /// <summary>Uses the executor's handlers, journal, safety guard and elevation service.</summary>
    public UndoEngine(Executor executor) => _executor = executor;

    private IRunJournal Journal => _executor.Journal;

    public async Task<UndoReport> UndoRunAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        using var timings = EngineTimings.Attach(_executor.Timings);
        using var work = _executor.BeginWork();
        var run = await LoadAsync(runId, ct).ConfigureAwait(false);
        if (run.Mode == ExecutionMode.Simulate)
            return new UndoReport(runId, 0, 0, 0, new[] { "Nothing to undo: it was only a simulation." });
        if (run.Status == RunStatus.Running) throw new InvalidOperationException("This run is still going. Wait for it to finish first.");
        var session = new RunSession(Journal, run);
        if (run.Status == RunStatus.Interrupted) await ResolveStartedAsync(session, ct).ConfigureAwait(false);
        return await UndoDoneStepsAsync(session, RunStatus.Undone, progress, ct).ConfigureAwait(false);
    }

    public async Task<RunRecord> ResumeAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        using var timings = EngineTimings.Attach(_executor.Timings);
        using var work = _executor.BeginWork();
        var run = await LoadAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.Interrupted || run.Mode != ExecutionMode.Real)
            throw new InvalidOperationException("Only an interrupted run can be resumed.");
        var session = new RunSession(Journal, run);
        await session.SaveAsync(r => r with { Status = RunStatus.Running }).ConfigureAwait(false); // takes the run lock again
        try
        {
            await ResolveStartedAsync(session, ct).ConfigureAwait(false);
            var pending = session.Steps.Where(s => s.Status == StepStatus.Pending).Select(s => s.Index).ToList();
            await _executor.RunStepsAsync(session, pending, progress, ct).ConfigureAwait(false);
            await Executor.FinishAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Executor.FinishAsync(session).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await _executor.MarkInterruptedAsync(session).ConfigureAwait(false);
            throw;
        }
        return session.Snapshot();
    }

    public async Task<UndoReport> RollbackAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        using var timings = EngineTimings.Attach(_executor.Timings);
        using var work = _executor.BeginWork();
        var run = await LoadAsync(runId, ct).ConfigureAwait(false);
        if (run.Status == RunStatus.Running) throw new InvalidOperationException("This run is still going. Wait for it to finish first.");
        var session = new RunSession(Journal, run);
        await ResolveStartedAsync(session, ct).ConfigureAwait(false);
        return await UndoDoneStepsAsync(session, RunStatus.RolledBack, progress, ct).ConfigureAwait(false);
    }

    private async Task<RunRecord> LoadAsync(string runId, CancellationToken ct)
        => await Journal.LoadAsync(runId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("That run wasn't found.");

    /// <summary>
    /// For every step left "Started" by a crash (each step of an intent batch whose result never reached the disk):
    /// did it actually happen? Yes → Done, no → Pending.
    /// Admin steps the elevated helper ran are never judged here (this process can't see the helper's admin-only
    /// quarantine, so it would call a real change "didn't happen"): the helper's read-only settle request answers them
    /// from its own ledger. Unanswered ones stay Started; Undo and Roll back then send them to the helper's undo, which
    /// reverses them only if its own record says they happened.
    /// </summary>
    private async Task ResolveStartedAsync(RunSession session, CancellationToken ct)
    {
        var ctx = _executor.Context(session.RunId, ExecutionMode.Real);
        var resolvedAll = new List<StepRecord>();
        var helperSteps = session.Steps.Where(s => s.Status == StepStatus.Started && _executor.IsHelperStep(s)).ToList();
        if (helperSteps.Count > 0)
        {
            foreach (var chunk in helperSteps.Chunk(Executor.AdminRequestSteps))
            {
                // Settled → Done/Skipped/Failed as the helper says. Unsettled stay Started.
                resolvedAll.AddRange(await _executor.SettleWithHelperAsync(session.RunId, chunk).ConfigureAwait(false));
            }
        }
        foreach (var s in session.Steps.Where(s => s.Status == StepStatus.Started && !_executor.IsHelperStep(s)).ToList())
        {
            var handler = _executor.FindHandler(s.Item);
            StepRecord resolved;
            if (handler is null) resolved = Executor.Fail(s, "PC Tidy can't tell whether this step happened.");
            else
            {
                try
                {
                    resolved = await handler.WasCompletedAsync(s, ctx, ct).ConfigureAwait(false)
                        ? s with { Status = StepStatus.Done, Message = "Finished before the interruption.", FinishedUtc = DateTime.UtcNow }
                        : s with { Status = StepStatus.Pending, Message = null, StartedUtc = null };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    resolved = Executor.Fail(s, "PC Tidy couldn't check whether this step happened: " + FileHandlerBase.Friendly(ex));
                }
            }
            resolvedAll.Add(resolved);
        }
        await session.RecordAsync(resolvedAll, durable: true).ConfigureAwait(false);
    }

    private async Task<UndoReport> UndoDoneStepsAsync(RunSession session, RunStatus fullStatus, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var ctx = _executor.Context(session.RunId, ExecutionMode.Real);
        var messages = new List<string>();
        int restored = 0, skipped = 0, failed = 0;
        // UndoFailed steps are retried (e.g. a file was in use last time); handlers re-check identity anyway.
        // Helper steps still Started (the helper stopped and couldn't say) go to the helper's undo too: its ledger knows
        // whether they happened and reverses them only then.
        var targets = session.Steps.Where(s => s.Status is StepStatus.Done or StepStatus.UndoFailed
                                               || (s.Status == StepStatus.Started && _executor.IsHelperStep(s)))
            .OrderByDescending(s => s.Index).Select(ForRetry).ToList();

        void Count(StepRecord r)
        {
            if (r.Status is StepStatus.Done or StepStatus.Started) { skipped++; messages.Add($"{r.Item.Description}: not undone yet ({(r.Status == StepStatus.Started ? "PC Tidy's admin helper couldn't be asked" : "stopped")})."); return; }
            if (r.Status == StepStatus.Undone) restored++;
            else if (r.UndoData?.GetValueOrDefault(UndoKeys.UndoOutcome) == "skipped") skipped++;
            else failed++;
            if (r.Status != StepStatus.Undone && r.Message is { } m) messages.Add($"{r.Item.Description}: {m}");
        }

        // Not reversible → reported, left as Done.
        foreach (var s in targets.Where(s => !s.Item.Reversible))
        {
            skipped++;
            messages.Add($"{s.Item.Description}: can't be undone.");
        }
        targets = targets.Where(s => s.Item.Reversible).ToList();

        // Admin steps were run last, so they are undone first, in as few elevated calls as possible.
        var admin = targets.Where(s => s.Item.NeedsAdmin && !_executor.RunsAdminStepsLocally).ToList();
        foreach (var chunk in admin.Chunk(Executor.AdminRequestSteps))
        {
            if (ct.IsCancellationRequested) break;
            var results = await UndoAdminAsync(session, chunk, ct).ConfigureAwait(false);
            await session.RecordAsync(results, durable: false).ConfigureAwait(false);
            foreach (var r in results) Count(r);
        }

        var local = targets.Except(admin).ToList();
        for (int i = 0; i < local.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var step = local[i];
            progress?.Report(new RunProgress(step.Index, session.Steps.Count, "Undoing: " + step.Item.Description));
            var handler = _executor.FindHandler(step.Item);
            if (handler is null)
            {
                failed++;
                messages.Add($"{step.Item.Description}: PC Tidy can't undo this kind of change here.");
                continue;
            }

            IReadOnlyList<StepRecord> results;
            using (EngineTimings.Measure("undo.handler"))
            {
                if (handler is RenamePathHandler renamer)
                {
                    var group = new List<StepRecord> { step };
                    while (i + 1 < local.Count && _executor.FindHandler(local[i + 1].Item) == renamer) group.Add(local[++i]);
                    results = await SafeAsync(group, () => renamer.UndoGroupAsync(group, ctx, CancellationToken.None)).ConfigureAwait(false);
                }
                else
                {
                    var one = new[] { step };
                    results = await SafeAsync(one, async () => new[] { await handler.UndoAsync(step, ctx, ct).ConfigureAwait(false) }).ConfigureAwait(false);
                }
            }
            var normalized = results.Select(Normalize).ToList();
            await session.RecordAsync(normalized, durable: false).ConfigureAwait(false);
            foreach (var r in normalized) Count(r);
        }

        var left = session.Steps.Count(s => s.Status is StepStatus.Done or StepStatus.UndoFailed || (s.Status == StepStatus.Started && _executor.IsHelperStep(s)));
        var anyUndone = session.Steps.Any(s => s.Status == StepStatus.Undone);
        var status = left == 0 ? fullStatus
            : anyUndone || session.Run.Status == RunStatus.Interrupted ? RunStatus.PartiallyUndone
            : session.Run.Status;
        await session.SaveAsync(r => r with { Status = status }).ConfigureAwait(false);
        return new UndoReport(session.RunId, restored, skipped, failed, messages);
    }

    private async Task<IReadOnlyList<StepRecord>> UndoAdminAsync(RunSession session, IReadOnlyList<StepRecord> steps, CancellationToken ct)
    {
        var elevation = _executor.Elevation;
        if (elevation is null)
            return steps.Select(s => s with { Status = StepStatus.UndoFailed, Message = "Needs admin rights, which aren't available here." }).ToList();
        try
        {
            var results = await elevation.ExecuteElevatedAsync(session.RunId, steps, ExecutionMode.Real, undo: true, ct).ConfigureAwait(false);
            return steps.Select(s => results.FirstOrDefault(r => r.Index == s.Index) is { } r
                // The helper stopped mid-undo: leave the step Done, so it can be undone again (handlers re-check).
                ? r.Status is StepStatus.Started or StepStatus.Pending ? s with { Message = "Not undone yet: PC Tidy's admin helper stopped. Try Undo again." } : Normalize(r)
                : s with { Status = StepStatus.UndoFailed, Message = "The admin helper didn't report this step." }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return steps.Select(s => s with { Status = StepStatus.UndoFailed, Message = "Couldn't get admin rights: " + ex.Message }).ToList();
        }
    }

    private static StepRecord ForRetry(StepRecord s)
    {
        if (s.UndoData is null || !s.UndoData.ContainsKey(UndoKeys.UndoOutcome)) return s;
        var d = new Dictionary<string, string>(s.UndoData);
        d.Remove(UndoKeys.UndoOutcome);
        return s with { UndoData = d };
    }

    private static async Task<IReadOnlyList<StepRecord>> SafeAsync(IReadOnlyList<StepRecord> steps, Func<Task<IReadOnlyList<StepRecord>>> undo)
    {
        try { return await undo().ConfigureAwait(false); }
        catch (OperationCanceledException) { return steps; } // left as Done: can be undone again later
        catch (Exception ex)
        {
            return steps.Select(s => s with { Status = StepStatus.UndoFailed, Message = FileHandlerBase.Friendly(ex), FinishedUtc = DateTime.UtcNow }).ToList();
        }
    }

    /// <summary>A handler must answer Undone or UndoFailed; Done means it didn't get to it (kept for a later retry).</summary>
    private static StepRecord Normalize(StepRecord r)
        => r.Status is StepStatus.Undone or StepStatus.UndoFailed or StepStatus.Done ? r
            : r with { Status = StepStatus.UndoFailed, Message = r.Message ?? "Undo didn't report a result." };
}
