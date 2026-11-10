using System.Diagnostics;
using System.Security.Principal;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// Runs a confirmed plan, write-ahead, in batches:
/// delete cap (measured on disk) → restore point (if a system change is planned) → for each batch of up to
/// <see cref="IntentBatchSteps"/> steps: safety re-check → still the same file as scanned → required apps closed →
/// not in use → prepare exact targets → ONE durable journal write of every step's intent ("Started" with its targets)
/// → each step: last-moment in-use test → handler → result journaled (reaches the disk with the next intent).
/// One step failing never stops the run. Admin steps go to the elevated helper in as few requests as possible.
/// Cancelling stops between steps; the journal stays consistent (remaining steps stay Pending). Whatever goes wrong,
/// the run's status is always finalised and its lock released.
/// </summary>
public sealed class Executor : IExecutor
{
    /// <summary>Steps journaled ahead with one forced disk flush. Recovery checks each of them if power is cut.</summary>
    internal const int IntentBatchSteps = 64;
    /// <summary>A batch also closes when its planned bytes pass this (big copies get small batches).</summary>
    internal const long IntentBatchBytes = 64L << 20;
    /// <summary>Admin steps per elevated request (far below the helper's own limit and message size).</summary>
    internal const int AdminRequestSteps = 1000;

    private readonly IReadOnlyList<IActionHandler> _handlers;
    private readonly IRunJournal _journal;
    private readonly ISafetyGuard _safety;
    private readonly IInUseChecker _inUse;
    private readonly IQuarantineStore _quarantine;
    private readonly IKnownLocations _locations;
    private readonly AppSettings _settings;
    private readonly IRestorePointService? _restorePoints;
    private readonly IElevationService? _elevation;
    private readonly HelperSettlement.Settle? _settle;
    private int _activeWork;

    public Executor(
        IEnumerable<IActionHandler> handlers,
        IRunJournal journal,
        ISafetyGuard safety,
        IInUseChecker inUse,
        IQuarantineStore quarantine,
        IKnownLocations locations,
        AppSettings settings,
        IRestorePointService? restorePoints = null,
        IElevationService? elevation = null)
    {
        _handlers = handlers.ToList();
        _journal = journal;
        _safety = safety;
        _inUse = inUse;
        _quarantine = quarantine;
        _locations = locations;
        _settings = settings;
        _restorePoints = restorePoints;
        _elevation = elevation;
        _settle = HelperSettlement.For(elevation);
    }

    /// <summary>
    /// True while a run, resume, undo or roll back is in progress in this process. The app shell asks before it
    /// closes the window: stopping mid-step is safe, but the user should be told.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _activeWork) > 0;

    /// <summary>Running process names (lower-case, no .exe). Replaceable for tests.</summary>
    internal Func<IReadOnlySet<string>> RunningProcesses { get; set; } = CurrentProcesses;

    /// <summary>Optional per-phase timings (perf tests / diagnostics).</summary>
    internal EngineTimings? Timings { get; set; }

    internal IRunJournal Journal => _journal;
    internal IElevationService? Elevation => _elevation;
    internal IKnownLocations Locations => _locations;

    /// <summary>True when this process may do admin steps itself (elevated, or a sandbox where nothing real runs).</summary>
    internal bool RunsAdminStepsLocally => _locations.IsSandbox || IsElevated;

    private bool IsElevated => _elevation?.IsElevated ?? IsProcessElevated();

    /// <summary>Marks work in progress for <see cref="IsRunning"/> until disposed.</summary>
    internal IDisposable BeginWork()
    {
        Interlocked.Increment(ref _activeWork);
        return new WorkScope(this);
    }

    private sealed class WorkScope(Executor owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._activeWork);
        }
    }

    public async Task<RunRecord> ExecuteAsync(ActionPlan plan, ExecuteOptions options, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        using var timings = EngineTimings.Attach(Timings);
        using var work = BeginWork();
        var created = await _journal.CreateAsync(plan, options.Mode, CancellationToken.None).ConfigureAwait(false);
        var session = new RunSession(_journal, created);
        try
        {
            if (options.MetricsBefore is { Count: > 0 } metrics)
                await session.SaveAsync(r => r with { Metrics = new Dictionary<string, double>(metrics) }).ConfigureAwait(false);

            if (options.Mode == ExecutionMode.Real && await DeleteCapProblemAsync(plan, ct).ConfigureAwait(false) is { } capProblem)
            {
                await StopBeforeAnyChangeAsync(session, capProblem).ConfigureAwait(false);
                return session.Snapshot();
            }

            if (options.Mode == ExecutionMode.Real && plan.RequiresRestorePoint && !_locations.IsSandbox)
            {
                var (info, proceed) = await EnsureRestorePointAsync(plan, options, ct).ConfigureAwait(false);
                await session.SaveAsync(r => r with { RestorePoint = info }).ConfigureAwait(false);
                if (!proceed)
                {
                    await StopBeforeAnyChangeAsync(session, "Stopped before any change: " + (info?.Problem ?? "no restore point could be made.")).ConfigureAwait(false);
                    return session.Snapshot();
                }
            }

            await RunStepsAsync(session, Enumerable.Range(0, session.Steps.Count).ToList(), progress, ct).ConfigureAwait(false);
            await FinishAsync(session).ConfigureAwait(false);
            return session.Snapshot();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped before or between steps: what didn't run stays Pending, and the run is finished as it stands.
            await FinishAsync(session).ConfigureAwait(false);
            return session.Snapshot();
        }
        catch (Exception)
        {
            await MarkInterruptedAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    internal static Task FinishAsync(RunSession session)
        => session.SaveAsync(r => r with { Status = session.FinalStatus(), FinishedUtc = DateTime.UtcNow });

    /// <summary>
    /// Something unexpected (a bug, the journal's disk failing) stopped the run. The run is finished from what the
    /// journal itself holds (memory may be ahead of it), as Interrupted so Resume / Roll back are offered; that also
    /// releases its lock. Never throws.
    /// </summary>
    internal async Task MarkInterruptedAsync(RunSession session)
    {
        try
        {
            var onDisk = await _journal.LoadAsync(session.RunId, CancellationToken.None).ConfigureAwait(false) ?? session.Snapshot();
            var status = session.Mode == ExecutionMode.Simulate ? RunStatus.Simulated : RunStatus.Interrupted;
            await _journal.SaveRunAsync(onDisk with { Status = status, FinishedUtc = DateTime.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The journal itself is failing: nothing more can be written. Its lock goes with the process.
        }
    }

    private static async Task StopBeforeAnyChangeAsync(RunSession session, string reason)
    {
        await session.RecordAsync(session.Steps.Where(s => s.Status == StepStatus.Pending).Select(s => Skip(s, reason)).ToList(), durable: false)
            .ConfigureAwait(false);
        await session.SaveAsync(r => r with { Status = RunStatus.Failed, FinishedUtc = DateTime.UtcNow }).ConfigureAwait(false);
    }

    /// <summary>Delete cap with this PC's settings, measured on disk (off the caller's thread). See <see cref="DeleteCap"/>.</summary>
    private Task<string?> DeleteCapProblemAsync(ActionPlan plan, CancellationToken ct)
    {
        var cfg = EngineSettings.From(_settings);
        return Task.Run(() => DeleteCap.Problem(plan.Items, cfg.MaxRemoveItemsPerRun, cfg.MaxRemoveBytesPerRun, ct), ct);
    }

    private async Task<(RestorePointInfo? Info, bool Proceed)> EnsureRestorePointAsync(ActionPlan plan, ExecuteOptions options, CancellationToken ct)
    {
        RestorePointInfo info;
        if (_restorePoints is null) info = new RestorePointInfo(false, false, null, null, "Restore points aren't available on this PC.");
        else
        {
            try { info = await _restorePoints.EnsureAsync($"PC Tidy: {plan.Title}", ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { info = new RestorePointInfo(false, false, null, null, ex.Message); }
        }
        if ((info.Created || info.Reused) && info.Problem is null) return (info, true);
        if (options.ContinueWithoutRestorePoint is null) return (info, false);
        var problem = info.Problem ?? "Windows didn't make a restore point.";
        return (info, await options.ContinueWithoutRestorePoint(problem).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ the step pipeline

    private sealed record Candidate(StepRecord Step, IActionHandler? Handler);

    /// <summary>Runs the given steps (in order). Shared by Execute and Resume.</summary>
    internal async Task RunStepsAsync(RunSession session, IReadOnlyList<int> indexes, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var ctx = Context(session.RunId, session.Mode);
        using (EngineTimings.Measure("inuse.batch"))
            _inUseBatch.Value = await InUseBatch.BuildAsync(_inUse,
                indexes.Select(i => session[i].Item).Where(it => HasFileSource(it.Kind) && it.SourcePath is not null).Select(it => SafeDisplay(it.SourcePath!)).OfType<string>(),
                ct).ConfigureAwait(false);

        var deferredAdmin = new List<int>();
        // Steps that need an admin step (directly or through another deferred step) run after the admin steps.
        var afterAdmin = new List<int>();
        var deferredIds = new HashSet<string>(StringComparer.Ordinal);
        await RunLocalAsync(indexes, collectDeferred: true).ConfigureAwait(false);

        if (deferredAdmin.Count > 0 && !ct.IsCancellationRequested)
            await RunAdminAsync(session, deferredAdmin, ctx, progress, ct).ConfigureAwait(false);
        if (afterAdmin.Count > 0 && !ct.IsCancellationRequested)
            await RunLocalAsync(afterAdmin, collectDeferred: false).ConfigureAwait(false);

        async Task RunLocalAsync(IReadOnlyList<int> which, bool collectDeferred)
        {
            var pos = 0;
            while (pos < which.Count)
            {
                if (ct.IsCancellationRequested) return; // the rest stay Pending
                var batch = new List<Candidate>();
                long bytes = 0;
                while (pos < which.Count)
                {
                    var step = session[which[pos]];
                    var handler = FindHandler(step.Item);
                    var full = batch.Count >= IntentBatchSteps || bytes > IntentBatchBytes;
                    // A run of renames is never split: its two phases make swaps and case-only renames work.
                    if (full && !(handler is RenamePathHandler && batch.Count > 0 && batch[^1].Handler == handler)) break;
                    pos++;
                    if (collectDeferred && session.Mode == ExecutionMode.Real && handler is not null && step.Item.NeedsAdmin && !RunsAdminStepsLocally)
                    {
                        deferredAdmin.Add(step.Index);
                        deferredIds.Add(step.Item.Id);
                        continue;
                    }
                    if (collectDeferred && step.Item.DependsOn is { Count: > 0 } needs && needs.Any(deferredIds.Contains))
                    {
                        afterAdmin.Add(step.Index);
                        deferredIds.Add(step.Item.Id);
                        continue;
                    }
                    batch.Add(new Candidate(step, handler));
                    bytes += Math.Max(0, step.Item.ExpectedSize);
                }
                if (session.Mode == ExecutionMode.Simulate) await SimulateBatchAsync(session, batch, ctx, progress, ct).ConfigureAwait(false);
                else await RunBatchAsync(session, batch, ctx, progress, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SimulateBatchAsync(RunSession session, List<Candidate> batch, HandlerContext ctx, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var results = new List<StepRecord>(batch.Count);
        try
        {
            foreach (var (step, handler) in batch)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RunProgress(step.Index, session.Steps.Count, step.Item.Description));
                if (handler is null) { results.Add(Skip(step, "PC Tidy can't do this kind of change here.")); continue; }
                StepRecord result;
                if (DependencyProblem(session, step, simulate: true, results) is { } needs) result = Skip(step, needs);
                else if (await SafePreCheckAsync(step.Item, ct).ConfigureAwait(false) is { } reason) result = Skip(step, reason);
                else
                {
                    try { result = await handler.SimulateAsync(step, ctx, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result = Skip(step, FileHandlerBase.Friendly(ex)); }
                    if (result.Status is not (StepStatus.Simulated or StepStatus.Skipped)) result = result with { Status = StepStatus.Simulated };
                }
                results.Add(Finish(result, step));
            }
        }
        finally
        {
            await session.RecordAsync(results, durable: false).ConfigureAwait(false);
        }
    }

    /// <summary>One batch: checks and prepares every step, journals all intents with one disk flush, then acts.</summary>
    private async Task RunBatchAsync(RunSession session, List<Candidate> batch, HandlerContext ctx, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var (records, ready) = await PrepareAsync(batch, ctx, ct).ConfigureAwait(false);
        using (EngineTimings.Measure("journal.intents"))
            await session.RecordAsync(records, durable: ready.Count > 0).ConfigureAwait(false);

        for (int k = 0; k < ready.Count; k++)
        {
            var (started, handler) = ready[k];
            if (ct.IsCancellationRequested)
            {
                // Journaled as about to start, but never touched: back to Pending (recovery would find the same).
                await session.RecordAsync(ready.Skip(k).Select(r => NotStarted(r.Step)).ToList(), durable: false).ConfigureAwait(false);
                return;
            }
            progress?.Report(new RunProgress(started.Index, session.Steps.Count, started.Item.Description));

            if (handler is RenamePathHandler renamer)
            {
                var group = new List<StepRecord> { started };
                while (k + 1 < ready.Count && ready[k + 1].Handler == renamer) group.Add(ready[++k].Step);
                // Like every other step: a rename whose DependsOn didn't finish is skipped, not handed to the group.
                var blocked = group.Select(s => (Step: s, Why: DependencyProblem(session, s, simulate: false))).Where(x => x.Why is not null).ToList();
                if (blocked.Count > 0)
                {
                    await session.RecordAsync(blocked.Select(x => Finish(Skip(x.Step, x.Why!), x.Step)).ToList(), durable: false).ConfigureAwait(false);
                    group = group.Except(blocked.Select(x => x.Step)).ToList();
                    if (group.Count == 0) continue;
                }
                IReadOnlyList<StepRecord> results;
                try
                {
                    using (EngineTimings.Measure("handler.rename-group"))
                        results = await renamer.ExecuteGroupAsync(group, ctx, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { results = group.Select(s => Fail(s, FileHandlerBase.Friendly(ex))).ToList(); }
                await session.RecordAsync(group.Select((s, i) => Finish(results[i], s)).ToList(), durable: false).ConfigureAwait(false);
                continue;
            }

            StepRecord result;
            if (DependencyProblem(session, started, simulate: false) is { } needs) result = Skip(started, needs);
            else if (LastMomentInUse(started.Item) is { } busy) result = Skip(started, busy);
            else
            {
                try
                {
                    using (EngineTimings.Measure("handler.execute"))
                        result = await handler.ExecuteAsync(started, ctx, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = await ResolveCancelledAsync(handler, started, ctx).ConfigureAwait(false); }
                catch (Exception ex) { result = Fail(started, FileHandlerBase.Friendly(ex)); }
            }
            await session.RecordAsync(new[] { Finish(result, started) }, durable: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Null when every step this one depends on (<see cref="PlanItem.DependsOn"/>) finished successfully (Simulated
    /// counts in a simulation); otherwise why it's skipped. A need that isn't in the run, or comes later, also blocks it.
    /// <paramref name="sameRequest"/>: ids sent to the elevated helper together with this step (it checks those itself).
    /// <paramref name="pending"/>: results not yet recorded in the session (a simulation batch).
    /// </summary>
    internal static string? DependencyProblem(RunSession session, StepRecord step, bool simulate,
        IReadOnlyList<StepRecord>? pending = null, IReadOnlySet<string>? sameRequest = null)
    {
        if (step.Item.DependsOn is not { Count: > 0 } needs) return null;
        foreach (var id in needs)
        {
            if (sameRequest?.Contains(id) == true) continue;
            var dep = pending?.LastOrDefault(r => r.Item.Id == id) ?? session.Steps.FirstOrDefault(s => s.Item.Id == id);
            if (dep is null || dep.Index >= step.Index) return "Not done, because a step it needs isn't in this run.";
            var ok = dep.Status == StepStatus.Done || (simulate && dep.Status == StepStatus.Simulated);
            if (!ok) return $"Not done, because an earlier step it needs didn't finish: {dep.Item.Description}";
        }
        return null;
    }

    /// <summary>
    /// Pre-checks and prepares each candidate. Returns the records to journal (skips + intents, in step order) and the
    /// steps ready to run (their Started records, with prepared targets). Stops early (nothing more ready) if cancelled.
    /// <paramref name="precheck"/> false: the checks happen in the elevated helper instead (admin steps; see <see cref="RunAdminAsync"/>).
    /// </summary>
    private async Task<(List<StepRecord> Records, List<(StepRecord Step, IActionHandler Handler)> Ready)> PrepareAsync(
        IReadOnlyList<Candidate> batch, HandlerContext ctx, CancellationToken ct, bool precheck = true)
    {
        var records = new List<StepRecord>(batch.Count);
        var ready = new List<(StepRecord, IActionHandler)>(batch.Count);
        using var planning = PlannedTargets.Begin(); // free names chosen for one step are never promised to another
        for (int i = 0; i < batch.Count; i++)
        {
            var (step, handler) = batch[i];
            if (ct.IsCancellationRequested) break;
            if (handler is RenamePathHandler && (i == 0 || batch[i - 1].Handler != handler))
            {
                // A rename group parks all its sources first, so names it frees count as free for its own targets.
                var group = batch.Skip(i).TakeWhile(c => c.Handler == handler).Select(c => c.Step.Item.SourcePath).OfType<string>();
                PlannedTargets.SetVacating(group.Where(p => SafeDisplay(p) is not null));
            }
            if (handler is null) { records.Add(Skip(step, "PC Tidy can't do this kind of change here.")); continue; }
            string? reason = null;
            if (precheck)
                using (EngineTimings.Measure("precheck")) reason = await SafePreCheckAsync(step.Item, ct).ConfigureAwait(false);
            if (reason is not null) { records.Add(Skip(step, reason)); continue; }
            StepRecord prepared;
            try { prepared = handler is IPreparingHandler p ? p.Prepare(step, ctx) : step; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                records.Add(Fail(step, FileHandlerBase.Friendly(ex)));
                continue;
            }
            var started = prepared with { Status = StepStatus.Started, StartedUtc = DateTime.UtcNow, Message = null };
            records.Add(started);
            ready.Add((started, handler));
        }
        return (records, ready);
    }

    /// <summary>
    /// All admin steps in as few elevated requests as possible (one admin prompt per run). They are NOT pre-checked
    /// here: a standard user often can't even read the places admin steps work in (C:\Windows\Temp), so the elevated
    /// helper runs the safety guard and the same-as-scanned / in-use checks itself. Every step's intent is
    /// journaled first; a step the helper reports as only Started (it stopped or was cancelled mid-step) is checked
    /// with its handler, so work that did happen is recorded Done and can be undone.
    /// </summary>
    private async Task RunAdminAsync(RunSession session, List<int> indexes, HandlerContext ctx, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        if (_elevation is null)
        {
            await session.RecordAsync(indexes.Select(i => Skip(session[i], "Needs admin rights, which aren't available here.")).ToList(), durable: false)
                .ConfigureAwait(false);
            return;
        }
        var (records, ready) = await PrepareAsync(indexes.Select(i => new Candidate(session[i], FindHandler(session[i].Item))).ToList(), ctx, ct, precheck: false)
            .ConfigureAwait(false);
        await session.RecordAsync(records, durable: ready.Count > 0).ConfigureAwait(false);

        foreach (var chunk in ready.Chunk(AdminRequestSteps))
        {
            // Needs outside this request must already be Done; needs inside it are checked by the helper, in order.
            var inRequest = chunk.Select(r => r.Step.Item.Id).ToHashSet(StringComparer.Ordinal);
            var blocked = chunk.Select(r => (r.Step, Problem: DependencyProblem(session, r.Step, simulate: false, sameRequest: inRequest)))
                .Where(x => x.Problem is not null).ToList();
            if (blocked.Count > 0)
                await session.RecordAsync(blocked.Select(x => Finish(Skip(x.Step, x.Problem!), x.Step)).ToList(), durable: false).ConfigureAwait(false);
            var blockedIds = blocked.Select(x => x.Step.Index).ToHashSet();
            var started = chunk.Select(r => r.Step).Where(s => !blockedIds.Contains(s.Index)).ToList();
            if (started.Count == 0) continue;
            if (ct.IsCancellationRequested)
            {
                await session.RecordAsync(started.Select(NotStarted).ToList(), durable: false).ConfigureAwait(false);
                continue;
            }
            progress?.Report(new RunProgress(started[0].Index, session.Steps.Count, "Admin steps: " + started[0].Item.Description));
            IReadOnlyList<StepRecord> results;
            var helperAnswered = true;
            try { results = await _elevation.ExecuteElevatedAsync(session.RunId, started, ExecutionMode.Real, undo: false, ct).ConfigureAwait(false); }
            catch (Exception)
            {
                // The helper didn't answer (it may have run some steps): they stay Started until it can be asked.
                helperAnswered = false;
                results = started;
            }

            var finished = new List<StepRecord>(started.Count);
            var unsettled = new List<StepRecord>();
            foreach (var s in started)
            {
                var r = results.FirstOrDefault(x => x.Index == s.Index) ?? Fail(s, "The admin helper didn't report this step.");
                if (r.Status is StepStatus.Started or StepStatus.Pending) unsettled.Add(Unsettled(s, r));
                else finished.Add(Finish(r, s));
            }
            // A helper that answered (but left some steps part-way, e.g. cancelled) is asked to settle them right away;
            // a lost helper isn't relaunched for it: they're settled on the next Resume or Undo.
            if (unsettled.Count > 0)
                finished.AddRange(helperAnswered ? await SettleWithHelperAsync(session.RunId, unsettled).ConfigureAwait(false) : unsettled);
            await session.RecordAsync(finished, durable: false).ConfigureAwait(false);
        }
    }

    internal const string UnsettledMessage =
        "PC Tidy's admin helper stopped during this step. PC Tidy will ask it whether the step happened when you Resume or Undo.";

    /// <summary>A step the helper left unfinished: still Started (it may have happened), with everything the helper reported.</summary>
    private static StepRecord Unsettled(StepRecord started, StepRecord reported) => started with
    {
        Status = StepStatus.Started,
        UndoData = FileHandlerBase.Merge(started.UndoData, reported.UndoData),
        RegistryPrior = reported.RegistryPrior ?? started.RegistryPrior,
        Message = UnsettledMessage,
    };

    /// <summary>True for an admin step that only the elevated helper can act on or check (its quarantine is admin-only).</summary>
    internal bool IsHelperStep(StepRecord s) => s.Item.NeedsAdmin && !RunsAdminStepsLocally;

    /// <summary>
    /// Admin steps the helper left Started (it stopped mid-batch) can't be judged here: what the helper quarantined lives
    /// in its admin-only folder, which this process can't see, so a local check would call a real change "didn't happen"
    /// and make it impossible to undo. The helper's read-only settle request answers them from its own ledger and context
    /// (<see cref="HelperSettlement"/>); they are never re-run to find out. Unanswered → they stay Started (the run ends
    /// Interrupted) and are asked about again on Resume, Undo or Roll back.
    /// </summary>
    internal async Task<IReadOnlyList<StepRecord>> SettleWithHelperAsync(string runId, IReadOnlyList<StepRecord> unsettled)
    {
        IReadOnlyList<StepRecord> answers = Array.Empty<StepRecord>();
        if (_settle is not null)
        {
            try
            {
                answers = await _settle(runId, unsettled.Select(s => s with { Status = StepStatus.Started }).ToList(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception) { /* no helper right now: they stay Started */ }
        }
        return unsettled.Select(s => answers.FirstOrDefault(a => a.Index == s.Index) is { Status: not (StepStatus.Started or StepStatus.Pending) } answer
            ? Finish(answer, s)
            : s with { Status = StepStatus.Started, Message = UnsettledMessage }).ToList();
    }

    /// <summary>Cancelled mid-step: handlers roll back their own partial work; confirm whether it finished anyway.</summary>
    private static async Task<StepRecord> ResolveCancelledAsync(IActionHandler handler, StepRecord started, HandlerContext ctx)
    {
        try
        {
            if (await handler.WasCompletedAsync(started, ctx, CancellationToken.None).ConfigureAwait(false))
                return started with { Status = StepStatus.Done, Message = "Finished just as you stopped." };
        }
        catch (Exception) { /* fall through */ }
        return Skip(started, "Stopped before this one finished. Nothing was changed.");
    }

    private static StepRecord NotStarted(StepRecord s) => s with { Status = StepStatus.Pending, StartedUtc = null, Message = null };

    /// <summary><see cref="PreCheckAsync"/>, with any unexpected error turned into a friendly skip reason.</summary>
    private async Task<string?> SafePreCheckAsync(PlanItem item, CancellationToken ct)
    {
        try { return await PreCheckAsync(item, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "PC Tidy couldn't check it, so it was left alone: " + FileHandlerBase.Friendly(ex); }
    }

    /// <summary>
    /// The checks right before a step runs. Returns why it must be skipped, or null to go ahead.
    /// Order: safety guard → still the same file as scanned → required apps closed → not in use (files and folders).
    /// </summary>
    internal async Task<string?> PreCheckAsync(PlanItem item, CancellationToken ct)
    {
        SafetyVerdict verdict;
        try
        {
            using (EngineTimings.Measure("precheck.safety")) verdict = _safety.Check(item);
        }
        catch (Exception ex) { return "The safety check couldn't run: " + ex.Message; }
        if (!verdict.Allowed) return verdict.Reason ?? "PC Tidy's safety rules don't allow this.";

        FileSystemInfo? source = null;
        if (HasFileSource(item.Kind) && item.SourcePath is { } src)
        {
            try { source = FsOps.Stat(src); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return "PC Tidy couldn't read it, so it was left alone: " + FileHandlerBase.Friendly(ex);
            }
            if (source is null)
            {
                // A config edit may create its file ("createIfMissing"); its handler refuses if one appears meanwhile.
                if (!CreatesMissingFile(item)) return "It's no longer there.";
            }
            else
            {
                if (CloudFile.IsPlaceholder(source.Attributes)) return "It's an online-only OneDrive file. PC Tidy never downloads it.";
                if (ChangedSinceScan(item, source)) return "It changed since the scan, so it was left alone.";
            }
        }

        if (item.RequiresClosedApps is { Count: > 0 } apps)
        {
            var running = CachedRunningProcesses();
            var open = apps.Where(a => running.Contains(a.ToLowerInvariant().Replace(".exe", ""))).ToList();
            if (open.Count > 0) return $"Close {JoinNames(open.Select(Pretty))} first.";
        }

        // Folders too: Restart Manager is asked about the files inside them (it never follows links).
        if (source is not null && !FsOps.IsReparsePoint(source))
        {
            var path = LongPath.Display(source.FullName);
            if (_inUseBatch.Value is { } batch && batch.WasChecked(path))
            {
                if (batch.AppsUsing(path) is { } holders)
                    return holders.Count > 0 ? $"In use by {JoinNames(holders)}." : "It's in use right now.";
            }
            else
            {
                IReadOnlyList<InUseInfo> info;
                using (EngineTimings.Measure("precheck.inuse-single")) info = await _inUse.CheckAsync(new[] { path }, ct).ConfigureAwait(false);
                if (info.FirstOrDefault(x => x.InUse) is { } busy)
                    return busy.Apps.Count > 0 ? $"In use by {JoinNames(busy.Apps)}." : "It's in use right now.";
            }
        }
        return null;
    }

    /// <summary>Right before acting on a file: a cheap exclusive-open test catches anything opened since the batch check.</summary>
    private string? LastMomentInUse(PlanItem item)
    {
        if (!HasFileSource(item.Kind) || item.IsDirectory || item.SourcePath is null) return null;
        try
        {
            using (EngineTimings.Measure("inuse.last-moment"))
                return _inUse.IsLocked(LongPath.Display(LongPath.Io(item.SourcePath))) ? "It's in use right now." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string? SafeDisplay(string path)
    {
        try { return LongPath.Display(LongPath.Io(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // Per-run caches so big runs don't pay a Restart Manager session and a process listing per file.
    private readonly AsyncLocal<InUseBatch?> _inUseBatch = new();
    private IReadOnlySet<string>? _processes;
    private DateTime _processesAt;
    private readonly object _processGate = new();

    /// <summary>Running processes, refreshed at most every 2 seconds.</summary>
    private IReadOnlySet<string> CachedRunningProcesses()
    {
        lock (_processGate)
        {
            if (_processes is null || DateTime.UtcNow - _processesAt > TimeSpan.FromSeconds(2))
            {
                _processes = RunningProcesses();
                _processesAt = DateTime.UtcNow;
            }
            return _processes;
        }
    }

    /// <summary>Restart Manager results for every file and folder of a run, fetched in batches before the first step.</summary>
    private sealed class InUseBatch
    {
        private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _busy = new(StringComparer.OrdinalIgnoreCase);

        public bool WasChecked(string path) => _checked.Contains(path);
        public IReadOnlyList<string>? AppsUsing(string path) => _busy.TryGetValue(path, out var a) ? a : null;

        public static async Task<InUseBatch> BuildAsync(IInUseChecker checker, IEnumerable<string> paths, CancellationToken ct)
        {
            var batch = new InUseBatch();
            foreach (var chunk in paths.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(200))
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<InUseInfo> results;
                try { results = await checker.CheckAsync(chunk, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { continue; } // those items fall back to per-item checks
                foreach (var p in chunk) batch._checked.Add(p);
                foreach (var r in results.Where(r => r.InUse)) batch._busy[r.Path] = r.Apps;
            }
            return batch;
        }
    }

    private static bool HasFileSource(ActionKind k) => k is ActionKind.QuarantinePath or ActionKind.MovePath or ActionKind.RenamePath
        or ActionKind.MoveFolderWithJunction or ActionKind.ArchiveToZip or ActionKind.ExtractZip or ActionKind.CopyPath
        or ActionKind.EditTextFile or ActionKind.ShredFile or ActionKind.HostsCommentOut or ActionKind.OneDriveFreeUpSpace;

    private static bool CreatesMissingFile(PlanItem item)
        => item.Kind == ActionKind.EditTextFile && item.Args is { } a && a.TryGetValue("createIfMissing", out var v)
           && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>In-place edits: only the modified time identifies the scanned file (their ExpectedSize is 0, "frees nothing").</summary>
    private static bool IsInPlaceEdit(ActionKind k) => k is ActionKind.EditTextFile or ActionKind.HostsCommentOut;

    /// <summary>Same type, same modified time and (files) same size as at scan time. Only checked when the scanner recorded them.</summary>
    private static bool ChangedSinceScan(PlanItem item, FileSystemInfo now)
    {
        if (IsInPlaceEdit(item.Kind))
            return now is DirectoryInfo || item.ExpectedLastWriteUtc is { } t
                && now.LastWriteTimeUtc.Ticks != (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc)).Ticks;
        if (item.IsDirectory != now is DirectoryInfo && (item.ExpectedLastWriteUtc.HasValue || item.ExpectedSize > 0)) return true;
        if (item.ExpectedLastWriteUtc is { } expected)
        {
            var want = expected.Kind == DateTimeKind.Local ? expected.ToUniversalTime() : DateTime.SpecifyKind(expected, DateTimeKind.Utc);
            if (now.LastWriteTimeUtc.Ticks != want.Ticks) return true;
            if (now is FileInfo f && f.Length != item.ExpectedSize) return true;
        }
        else if (now is FileInfo f && item.ExpectedSize > 0 && f.Length != item.ExpectedSize) return true;
        return false;
    }

    // ------------------------------------------------------------------ helpers

    internal IActionHandler? FindHandler(PlanItem item) => _handlers.FirstOrDefault(h => SafeCanHandle(h, item));

    private static bool SafeCanHandle(IActionHandler h, PlanItem item)
    {
        try { return h.CanHandle(item); } catch (Exception) { return false; }
    }

    internal HandlerContext Context(string runId, ExecutionMode mode) => new()
    {
        RunId = runId,
        Locations = _locations,
        Safety = _safety,
        InUse = _inUse,
        Quarantine = _quarantine,
        Settings = _settings,
        Mode = mode,
        IsElevated = IsElevated,
    };

    internal static StepRecord Skip(StepRecord s, string reason)
        => s with { Status = StepStatus.Skipped, Message = reason, FinishedUtc = DateTime.UtcNow };

    internal static StepRecord Fail(StepRecord s, string reason)
        => s with { Status = StepStatus.Failed, Message = reason, FinishedUtc = DateTime.UtcNow };

    /// <summary>Keeps a handler's result consistent with the step it was given (index, item, timestamps).</summary>
    internal static StepRecord Finish(StepRecord result, StepRecord started)
    {
        var r = result with
        {
            Index = started.Index,
            Item = started.Item,
            StartedUtc = started.StartedUtc ?? result.StartedUtc,
            FinishedUtc = result.FinishedUtc ?? DateTime.UtcNow,
            UndoData = FileHandlerBase.Merge(started.UndoData, result.UndoData),
        };
        return r.Status is StepStatus.Pending or StepStatus.Started ? Fail(r, "The step didn't report a result.") : r;
    }

    private static string Pretty(string app)
    {
        var n = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        return n.Length == 0 ? n : char.ToUpperInvariant(n[0]) + n[1..];
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var l = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return l.Count <= 1 ? string.Concat(l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
    }

    private static IReadOnlySet<string> CurrentProcesses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName.ToLowerInvariant()); }
            catch (InvalidOperationException) { /* exited */ }
            finally { p.Dispose(); }
        }
        return set;
    }

    private static bool IsProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
