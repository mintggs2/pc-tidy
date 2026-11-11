using System.Collections.Concurrent;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>
/// The executor runs one change at a time (a real run, an undo, a resume or a roll back): a second one waits until the
/// first has finished, so their steps never interleave. Simulations don't wait. Only test files in a sandbox are used.
/// </summary>
public class OneChangeAtATimeTests
{
    private sealed class InlineProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_second_real_run_waits_until_the_first_has_finished_and_a_simulation_does_not()
    {
        using var sb = new TestSandbox();
        var a = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var b = WriteFile(Path.Combine(sb.Work(), "b.txt"), 10, 2);
        var c = WriteFile(Path.Combine(sb.Work(), "c.txt"), 10, 3);
        var engine = sb.Engine();
        using var hold = new ManualResetEventSlim(false);
        var firstReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var holdA = new InlineProgress(_ => { order.Enqueue("A"); firstReported.TrySetResult(); hold.Wait(Patience); });
        var seeB = new InlineProgress(_ => order.Enqueue("B"));

        var runA = Task.Run(() => engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, a)), Real, holdA, default));
        await firstReported.Task.WaitAsync(Patience);
        Assert.True(engine.Executor.IsChanging);

        var runB = Task.Run(() => engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, b)), Real, seeB, default));
        // A simulation changes nothing, so it doesn't wait for the run in progress.
        var sim = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, c)), Simulate, null, default).WaitAsync(Patience);
        Assert.Equal(RunStatus.Simulated, sim.Status);

        await Task.Delay(300);
        Assert.False(runB.IsCompleted);
        Assert.DoesNotContain("B", order);
        Assert.True(Exists(b)); // the second run hasn't touched anything yet

        hold.Set();
        var doneA = await runA.WaitAsync(Patience);
        var doneB = await runB.WaitAsync(Patience);
        Assert.Equal(StepStatus.Done, doneA.Steps[0].Status);
        Assert.Equal(StepStatus.Done, doneB.Steps[0].Status);
        Assert.False(engine.Executor.IsChanging);
        Assert.False(engine.Executor.IsRunning);

        // Every report of the first run came before any of the second's.
        var seen = order.ToList();
        Assert.True(seen.LastIndexOf("A") < seen.IndexOf("B"), string.Join(",", seen));
    }

    [Fact]
    public async Task An_undo_waits_for_the_run_in_progress_instead_of_failing()
    {
        using var sb = new TestSandbox();
        var first = WriteFile(Path.Combine(sb.Work(), "first.txt"), 10, 1);
        var second = WriteFile(Path.Combine(sb.Work(), "second.txt"), 10, 2);
        var engine = sb.Engine();
        var earlier = await engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, first)), Real, null, default);
        Assert.False(Exists(first));

        using var hold = new ManualResetEventSlim(false);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdRun = new InlineProgress(_ => { reported.TrySetResult(); hold.Wait(Patience); });
        var running = Task.Run(() => engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, second)), Real, holdRun, default));
        await reported.Task.WaitAsync(Patience);

        var undo = Task.Run(() => engine.Undo.UndoRunAsync(earlier.RunId, null, default));
        await Task.Delay(300);
        Assert.False(undo.IsCompleted);
        Assert.False(Exists(first));

        hold.Set();
        await running.WaitAsync(Patience);
        var report = await undo.WaitAsync(Patience);
        Assert.True(report.Complete, string.Join("; ", report.Messages));
        Assert.True(Exists(first));
    }

    [Fact]
    public async Task Cancelling_while_waiting_starts_nothing_and_journals_nothing()
    {
        using var sb = new TestSandbox();
        var a = WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1);
        var b = WriteFile(Path.Combine(sb.Work(), "b.txt"), 10, 2);
        var engine = sb.Engine();
        using var hold = new ManualResetEventSlim(false);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runA = Task.Run(() => engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, a)), Real,
            new InlineProgress(_ => { reported.TrySetResult(); hold.Wait(Patience); }), default));
        await reported.Task.WaitAsync(Patience);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Executor.ExecuteAsync(Plan(ScannedItem(ActionKind.QuarantinePath, b)), Real, null, cts.Token));
        hold.Set();
        await runA.WaitAsync(Patience);
        Assert.True(Exists(b));
        Assert.Single(await engine.Journal.ListAsync(default));
        Assert.False(engine.Executor.IsChanging);
    }
}
