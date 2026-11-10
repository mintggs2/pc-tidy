using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.Handlers;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>Power cuts and crashes: the journal must let PC Tidy resume or roll back cleanly.</summary>
public class CrashRecoveryTests
{
    /// <summary>A journal that "loses power" at a chosen moment: after writing Started (before the change), or before writing Done (after it).</summary>
    private sealed class CrashingJournal : IRunJournal
    {
        public readonly RunJournal Inner;
        public int CrashAtIndex = -1;
        public bool AfterChange;
        public CrashingJournal(RunJournal inner) => Inner = inner;

        public Task<RunRecord> CreateAsync(ActionPlan plan, ExecutionMode mode, CancellationToken ct) => Inner.CreateAsync(plan, mode, ct);
        public async Task SaveStepAsync(string runId, StepRecord step, CancellationToken ct)
        {
            if (step.Index == CrashAtIndex && AfterChange && step.Status != StepStatus.Started) throw new PowerCut();
            await Inner.SaveStepAsync(runId, step, ct);
            if (step.Index == CrashAtIndex && !AfterChange && step.Status == StepStatus.Started) throw new PowerCut();
        }
        public Task SaveRunAsync(RunRecord run, CancellationToken ct) => Inner.SaveRunAsync(run, ct);
        public Task<RunRecord?> LoadAsync(string runId, CancellationToken ct) => Inner.LoadAsync(runId, ct);
        public Task<IReadOnlyList<RunRecord>> ListAsync(CancellationToken ct) => Inner.ListAsync(ct);
        public Task<IReadOnlyList<RunRecord>> FindUnfinishedAsync(CancellationToken ct) => Inner.FindUnfinishedAsync(ct);
    }

    private sealed class PowerCut : Exception { }

    private sealed record Scenario(TestSandbox Sb, string Work, List<string> Before, ActionPlan Plan, string Dest);

    private static Scenario Setup(TestSandbox sb)
    {
        var work = sb.Work("w");
        MakeTree(Path.Combine(work, "tree"), oddNames: true);
        WriteFile(Path.Combine(work, "f1.bin"), 3000, 1);
        WriteFile(Path.Combine(work, "f2.bin"), 3000, 2);
        WriteText(Path.Combine(work, "swap-a.txt"), "A");
        WriteText(Path.Combine(work, "swap-b.txt"), "B");
        var before = Snapshot(work, includeFolderTimes: false);
        var dest = Path.Combine(sb.Locations.Downloads, "Out");
        var plan = Plan(
            ScannedItem(ActionKind.MovePath, Path.Combine(work, "f1.bin"), Path.Combine(dest, "f1.bin")),
            ScannedItem(ActionKind.QuarantinePath, Path.Combine(work, "tree")),
            ScannedItem(ActionKind.MovePath, Path.Combine(work, "f2.bin"), Path.Combine(dest, "f2.bin")),
            ScannedItem(ActionKind.RenamePath, Path.Combine(work, "swap-a.txt"), Path.Combine(work, "swap-b.txt")),
            ScannedItem(ActionKind.RenamePath, Path.Combine(work, "swap-b.txt"), Path.Combine(work, "swap-a.txt")));
        return new Scenario(sb, work, before, plan, dest);
    }

    private static async Task<string> RunUntilCrash(TestSandbox sb, ActionPlan plan, int index, bool afterChange)
    {
        var inner = new RunJournal(sb.Settings.DataRoot);
        var journal = new CrashingJournal(inner) { CrashAtIndex = index, AfterChange = afterChange };
        var quarantine = new QuarantineStore(sb.Settings.DataRoot, sb.Locations, sb.Settings);
        var executor = new Executor(EngineFactory.CreateFileHandlers(), journal, sb.Safety, sb.InUse, quarantine, sb.Locations, sb.Settings);
        await Assert.ThrowsAsync<PowerCut>(() => executor.ExecuteAsync(plan, Real, null, default));
        inner.SimulateProcessDeath();
        return plan.RunId;
    }

    [Theory]
    [InlineData(1, false)]  // crash after "Started", before the quarantine happened
    [InlineData(1, true)]   // crash after the quarantine happened, before "Done" was written
    [InlineData(2, true)]
    public async Task Resume_after_a_crash_finishes_the_run_and_undo_still_restores_everything(int index, bool afterChange)
    {
        using var sb = new TestSandbox();
        var s = Setup(sb);
        var runId = await RunUntilCrash(sb, s.Plan, index, afterChange);

        var engine = sb.Engine();   // "next start of PC Tidy"
        var unfinished = Assert.Single(await engine.Journal.FindUnfinishedAsync(default));
        Assert.Equal(StepStatus.Started, unfinished.Steps[index].Status);

        var resumed = await engine.Undo.ResumeAsync(runId, null, default);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.All(resumed.Steps, st => Assert.Equal(StepStatus.Done, st.Status));
        Assert.Equal("B", File.ReadAllText(Path.Combine(s.Work, "swap-a.txt")));
        Assert.True(Exists(Path.Combine(s.Dest, "f2.bin")));
        Assert.Empty(await engine.Journal.FindUnfinishedAsync(default));

        var report = await engine.Undo.UndoRunAsync(runId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(s.Before, Snapshot(s.Work, includeFolderTimes: false));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Rollback_after_a_crash_puts_everything_back_byte_for_byte(int index, bool afterChange)
    {
        using var sb = new TestSandbox();
        var s = Setup(sb);
        var runId = await RunUntilCrash(sb, s.Plan, index, afterChange);

        var engine = sb.Engine();
        var report = await engine.Undo.RollbackAsync(runId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(s.Before, Snapshot(s.Work, includeFolderTimes: false));
        Assert.False(Exists(s.Dest));
        var run = (await engine.Journal.LoadAsync(runId, default))!;
        Assert.Equal(RunStatus.RolledBack, run.Status);
        Assert.Empty(await engine.Journal.FindUnfinishedAsync(default));
    }

    [Fact]
    public async Task Crash_in_the_middle_of_a_two_phase_rename_is_tidied_back()
    {
        using var sb = new TestSandbox();
        var s = Setup(sb);
        var runId = await RunUntilCrash(sb, s.Plan, 3, afterChange: false);

        // Emulate the crash landing between the two rename phases: the item is parked under its temp name.
        var engine = sb.Engine();
        var started = (await engine.Journal.LoadAsync(runId, default))!.Steps[3];
        var temp = started.UndoData![UndoKeys.TempPath];
        FsOps.Rename(Path.Combine(s.Work, "swap-a.txt"), temp);

        var report = await engine.Undo.RollbackAsync(runId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(s.Before, Snapshot(s.Work, includeFolderTimes: false));
    }

    [Fact]
    public async Task Torn_journal_line_after_a_crash_still_resumes_correctly()
    {
        using var sb = new TestSandbox();
        var s = Setup(sb);
        var runId = await RunUntilCrash(sb, s.Plan, 2, afterChange: true);
        var steps = Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), runId, "steps.jsonl");
        File.AppendAllText(steps, "{\"Seq\":999,\"Step\":{\"Index\":2,\"Item\":{\"Ki");   // half-written record

        var engine = sb.Engine();
        var resumed = await engine.Undo.ResumeAsync(runId, null, default);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        var report = await engine.Undo.UndoRunAsync(runId, null, default);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.Equal(s.Before, Snapshot(s.Work, includeFolderTimes: false));
    }

    [Fact]
    public async Task Resume_refuses_runs_that_are_not_interrupted()
    {
        using var sb = new TestSandbox();
        var engine = sb.Engine();
        var run = await engine.Executor.ExecuteAsync(Plan(Item(ActionKind.CreateDirectory, dest: Path.Combine(sb.Work(), "x"))), Real, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Undo.ResumeAsync(run.RunId, null, default));
        var sim = await engine.Executor.ExecuteAsync(Plan(Item(ActionKind.CreateDirectory, dest: Path.Combine(sb.Work(), "y"))), Simulate, null, default);
        var undo = await engine.Undo.UndoRunAsync(sim.RunId, null, default);
        Assert.Equal(0, undo.Restored);
    }
}
