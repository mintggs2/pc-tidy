using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Engine;

/// <summary>
/// In-memory state of one run while the engine works on it. Every step change is journaled first:
/// intents (Started) durably, in batches; results reach the OS at once and the disk with the next durable write.
/// </summary>
internal sealed class RunSession
{
    private readonly IRunJournal _journal;
    private readonly StepRecord[] _steps;

    public RunSession(IRunJournal journal, RunRecord run)
    {
        _journal = journal;
        Run = run;
        _steps = run.Steps.ToArray();
    }

    public RunRecord Run { get; private set; }
    public string RunId => Run.RunId;
    public ExecutionMode Mode => Run.Mode;
    public IReadOnlyList<StepRecord> Steps => _steps;
    public StepRecord this[int index] => _steps[index];

    /// <summary>One record (a Started record is forced to the disk before this returns).</summary>
    public Task RecordAsync(StepRecord step) => RecordAsync(new[] { step }, durable: step.Status == StepStatus.Started);

    /// <summary>
    /// Several records in one journal write. <paramref name="durable"/>: on the disk before this returns (required for
    /// intents: Started records describe a change that is about to happen).
    /// </summary>
    public async Task RecordAsync(IReadOnlyList<StepRecord> steps, bool durable)
    {
        if (steps.Count == 0) return;
        if (_journal is IStepBatchJournal batch)
            await batch.SaveStepsAsync(RunId, steps, durable, CancellationToken.None).ConfigureAwait(false);
        else
            foreach (var s in steps) await _journal.SaveStepAsync(RunId, s, CancellationToken.None).ConfigureAwait(false);
        foreach (var s in steps) _steps[s.Index] = s;
    }

    public async Task SaveAsync(Func<RunRecord, RunRecord> change)
    {
        Run = change(Run) with { Steps = _steps.ToArray() };
        await _journal.SaveRunAsync(Run, CancellationToken.None).ConfigureAwait(false);
    }

    public RunRecord Snapshot() => Run with { Steps = _steps.ToArray() };

    /// <summary>
    /// Completed / CompletedWithSkips / Failed from the step states (Simulate → Simulated). A step still Started (the
    /// admin helper stopped and couldn't say whether it happened) makes the run Interrupted, so Resume / Roll back /
    /// Undo ask the helper again.
    /// </summary>
    public RunStatus FinalStatus()
    {
        if (Mode == ExecutionMode.Simulate) return RunStatus.Simulated;
        if (_steps.Any(s => s.Status == StepStatus.Started)) return RunStatus.Interrupted;
        int done = _steps.Count(s => s.Status == StepStatus.Done);
        int failed = _steps.Count(s => s.Status == StepStatus.Failed);
        if (failed > 0 && done == 0) return RunStatus.Failed;
        return done == _steps.Length ? RunStatus.Completed : RunStatus.CompletedWithSkips;
    }
}
