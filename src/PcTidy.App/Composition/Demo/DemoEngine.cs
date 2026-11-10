using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition.Demo;

/// <summary>In-memory run journal with a few sample runs.</summary>
public sealed class DemoRunJournal : IRunJournal
{
    private readonly List<RunRecord> _runs = new();
    private readonly object _gate = new();

    public Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct)
    {
        var run = new RunRecord
        {
            RunId = plan.RunId, ParentRunId = plan.ParentRunId, FeatureId = plan.FeatureId, Title = plan.Title, Mode = mode,
            Status = RunStatus.Running, AppVersion = "1.0.0",
            Steps = plan.Items.Select((item, i) => new StepRecord { Index = i, Item = item }).ToList(),
        };
        lock (_gate) _runs.Add(run);
        return Task.FromResult(run);
    }

    public Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _runs.FindIndex(r => r.RunId == runId);
            if (i >= 0)
            {
                var steps = _runs[i].Steps.ToList();
                steps[step.Index] = step;
                _runs[i] = _runs[i] with { Steps = steps };
            }
        }
        return Task.CompletedTask;
    }

    public Task SaveRunAsync(RunRecord run, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _runs.FindIndex(r => r.RunId == run.RunId);
            if (i >= 0) _runs[i] = run; else _runs.Add(run);
        }
        return Task.CompletedTask;
    }

    public Task<RunRecord?> LoadAsync(string runId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_runs.FirstOrDefault(r => r.RunId == runId));
    }

    public Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<RunRecord>>(_runs.OrderByDescending(r => r.CreatedUtc).ToList());
    }

    public Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<RunRecord>>(_runs.Where(r => r.Status is RunStatus.Running or RunStatus.Interrupted && r.Mode == ExecutionMode.Real).ToList());
    }

    public void Seed(RunRecord run) { lock (_gate) _runs.Add(run); }
}

/// <summary>In-memory quarantine. "Restoring" and "purging" only change this list.</summary>
public sealed class DemoQuarantineStore : IQuarantineStore
{
    private readonly List<QuarantineEntry> _entries = new();
    private readonly object _gate = new();

    public Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct)
    {
        var e = new QuarantineEntry { Id = Guid.NewGuid().ToString("N"), RunId = runId, OriginalPath = path, StoredPath = @"(demo)\" + Path.GetFileName(path) };
        Add(e);
        return Task.FromResult(e);
    }

    public void Add(QuarantineEntry e) { lock (_gate) _entries.Add(e); }

    public async Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct)
    {
        await Task.Delay(15, ct);
        lock (_gate) _entries.RemoveAll(e => e.Id == entry.Id);
        return entry.OriginalPath;
    }

    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<QuarantineEntry>>(_entries.Where(e => runId == null || e.RunId == runId).ToList());
    }

    public Task PurgeRunAsync(string runId, CancellationToken ct)
    {
        lock (_gate) _entries.RemoveAll(e => e.RunId == runId);
        return Task.CompletedTask;
    }

    public Task<long> TotalBytesAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_entries.Sum(e => e.SizeBytes));
    }
}

/// <summary>
/// Demo executor: walks the plan step by step (journaled like the real one) but changes nothing.
/// Items with Args["demoSkip"] are reported as skipped with that reason.
/// </summary>
public sealed class DemoExecutor : IExecutor
{
    private readonly DemoRunJournal _journal;
    private readonly DemoQuarantineStore _quarantine;
    private readonly IRestorePointService _restorePoints;

    public DemoExecutor(DemoRunJournal journal, DemoQuarantineStore quarantine, IRestorePointService restorePoints)
    {
        _journal = journal;
        _quarantine = quarantine;
        _restorePoints = restorePoints;
    }

    public async Task<RunRecord> ExecuteAsync(ActionPlan plan, ExecuteOptions options, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var run = await _journal.CreateAsync(plan, options.Mode, ct);
        RestorePointInfo? rp = null;
        if (plan.RequiresRestorePoint && options.Mode == ExecutionMode.Real)
            rp = await _restorePoints.EnsureAsync("PC Tidy: " + plan.Title, ct);

        int n = plan.Items.Count;
        int delay = n == 0 ? 0 : Math.Clamp(1200 / n, 1, 40);
        var steps = new List<StepRecord>(n);
        var status = options.Mode == ExecutionMode.Simulate ? RunStatus.Simulated : RunStatus.Completed;
        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested)
            {
                steps.AddRange(run.Steps.Skip(i).Select(s => s with { Status = StepStatus.Skipped, Message = "You stopped the run before this step" }));
                break;
            }
            var item = plan.Items[i];
            progress?.Report(new RunProgress(i, n, item.Description));
            if (delay > 0 && i % Math.Max(1, n / 60) == 0) await Task.Delay(delay * Math.Max(1, n / 60), CancellationToken.None);
            steps.Add(Step(run.Steps[i], item, options.Mode, run.RunId));
        }
        if (options.Mode == ExecutionMode.Real && steps.Any(s => s.Status is StepStatus.Skipped or StepStatus.Failed)) status = RunStatus.CompletedWithSkips;
        var done = run with { Steps = steps, Status = status, FinishedUtc = DateTime.UtcNow, RestorePoint = rp };
        await _journal.SaveRunAsync(done, ct);
        return done;
    }

    private StepRecord Step(StepRecord step, PlanItem item, ExecutionMode mode, string runId)
    {
        var now = DateTime.UtcNow;
        string? skip = item.Args != null && item.Args.TryGetValue("demoSkip", out var why) ? why : null;
        if (mode == ExecutionMode.Simulate)
            return step with
            {
                Status = skip is null ? StepStatus.Simulated : StepStatus.Skipped,
                Message = skip is null ? WouldDo(item) : "Would be skipped: " + skip,
                StartedUtc = now, FinishedUtc = now,
            };
        if (skip != null) return step with { Status = StepStatus.Skipped, Message = skip, StartedUtc = now, FinishedUtc = now };
        if (item.Kind == ActionKind.QuarantinePath && item.SourcePath != null)
            _quarantine.Add(new QuarantineEntry
            {
                Id = Guid.NewGuid().ToString("N"), RunId = runId, OriginalPath = item.SourcePath,
                StoredPath = @"(demo)\" + Path.GetFileName(item.SourcePath), SizeBytes = item.ExpectedSize, FileCount = 1, IsDirectory = item.IsDirectory,
            });
        return step with { Status = StepStatus.Done, Message = DidDo(item), BytesAffected = item.ExpectedSize, StartedUtc = now, FinishedUtc = now };
    }

    private static string WouldDo(PlanItem i) => i.Kind switch
    {
        ActionKind.QuarantinePath => "Would move to quarantine (restorable)",
        ActionKind.MovePath or ActionKind.RenamePath => "Would move to " + (i.DestinationPath ?? "its new folder"),
        ActionKind.EmptyRecycleBin => "Would empty the Recycle Bin (can't be undone)",
        ActionKind.RunSystemTool => "Would ask Windows to do this with its own tool" + (i.Reversible ? "" : " (can't be undone)"),
        ActionKind.StartupItemSetEnabled => i.Enable == false ? "Would stop it starting with Windows" : "Would let it start with Windows",
        _ => "Would change this setting (the old value is saved first)",
    };

    private static string DidDo(PlanItem i) => i.Kind switch
    {
        ActionKind.QuarantinePath => "Moved to quarantine",
        ActionKind.MovePath or ActionKind.RenamePath => "Moved",
        ActionKind.EmptyRecycleBin => "Recycle Bin emptied",
        _ => "Changed (old value saved)",
    };
}

/// <summary>Demo undo / resume / roll back over the in-memory journal and quarantine.</summary>
public sealed class DemoUndoEngine : IUndoEngine
{
    private readonly DemoRunJournal _journal;
    private readonly DemoQuarantineStore _quarantine;

    public DemoUndoEngine(DemoRunJournal journal, DemoQuarantineStore quarantine)
    {
        _journal = journal;
        _quarantine = quarantine;
    }

    public async Task<UndoReport> UndoRunAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var run = await _journal.LoadAsync(runId, ct);
        if (run is null) return new UndoReport(runId, 0, 0, 0, new[] { "That run isn't in the journal any more." });
        int restored = 0, skipped = 0;
        var steps = run.Steps.ToList();
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            progress?.Report(new RunProgress(steps.Count - 1 - i, steps.Count, steps[i].Item.Description));
            if (steps[i].Status != StepStatus.Done) continue;
            if (!steps[i].Item.Reversible) { skipped++; continue; }
            steps[i] = steps[i] with { Status = StepStatus.Undone, Message = "Put back" };
            restored++;
        }
        await Task.Delay(Math.Min(900, 10 * restored), ct);
        await _quarantine.PurgeRunAsync(runId, ct); // demo: entries are "restored", so they leave quarantine
        await _journal.SaveRunAsync(run with { Steps = steps, Status = skipped > 0 ? RunStatus.PartiallyUndone : RunStatus.Undone }, ct);
        var messages = skipped > 0 ? new[] { $"{skipped} step{(skipped == 1 ? " was" : "s were")} permanent and can't be put back." } : Array.Empty<string>();
        return new UndoReport(runId, restored, skipped, 0, messages);
    }

    public async Task<RunRecord> ResumeAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var run = await _journal.LoadAsync(runId, ct) ?? throw new InvalidOperationException("That run isn't in the journal any more.");
        await Task.Delay(500, ct);
        var steps = run.Steps.Select(s => s.Status is StepStatus.Pending or StepStatus.Started ? s with { Status = StepStatus.Done, Message = "Finished after resume" } : s).ToList();
        var done = run with { Steps = steps, Status = RunStatus.Completed, FinishedUtc = DateTime.UtcNow };
        await _journal.SaveRunAsync(done, ct);
        return done;
    }

    public async Task<UndoReport> RollbackAsync(string runId, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var run = await _journal.LoadAsync(runId, ct) ?? throw new InvalidOperationException("That run isn't in the journal any more.");
        await Task.Delay(500, ct);
        int n = run.Steps.Count(s => s.Status == StepStatus.Done);
        var steps = run.Steps.Select(s => s.Status == StepStatus.Done ? s with { Status = StepStatus.Undone } : s).ToList();
        await _journal.SaveRunAsync(run with { Steps = steps, Status = RunStatus.RolledBack, FinishedUtc = DateTime.UtcNow }, ct);
        return new UndoReport(runId, n, 0, 0, Array.Empty<string>());
    }
}
