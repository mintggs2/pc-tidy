using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

public class RunJournalTests
{
    private static ActionPlan ThreeItems() => Plan(
        Item(ActionKind.QuarantinePath, @"X:\a.txt"),
        Item(ActionKind.QuarantinePath, @"X:\b.txt") with { Args = new() { ["k"] = "v" }, RequiresClosedApps = new[] { "chrome" } },
        Item(ActionKind.MovePath, @"X:\c.txt", @"X:\d\c.txt"));

    [Fact]
    public async Task Create_writes_header_and_load_replays_steps_last_record_wins()
    {
        using var sb = new TestSandbox();
        using var journal = new RunJournal(sb.Settings.DataRoot);
        var plan = ThreeItems();
        var run = await journal.CreateAsync(plan, ExecutionMode.Real, default);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.True(File.Exists(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), plan.RunId, "run.json")));

        await journal.SaveStepAsync(plan.RunId, run.Steps[0] with { Status = StepStatus.Started }, default);
        await journal.SaveStepAsync(plan.RunId, run.Steps[0] with { Status = StepStatus.Done, UndoData = new() { ["quarantineId"] = "q1" }, BytesAffected = 5 }, default);
        await journal.SaveStepAsync(plan.RunId, run.Steps[1] with { Status = StepStatus.Skipped, Message = "In use by Chrome." }, default);

        var loaded = (await journal.LoadAsync(plan.RunId, default))!;
        Assert.Equal(RunStatus.Running, loaded.Status); // this journal holds the lock
        Assert.Equal(StepStatus.Done, loaded.Steps[0].Status);
        Assert.Equal("q1", loaded.Steps[0].UndoData!["quarantineId"]);
        Assert.Equal(StepStatus.Skipped, loaded.Steps[1].Status);
        Assert.Equal("v", loaded.Steps[1].Item.Args!["k"]);
        Assert.Equal("chrome", loaded.Steps[1].Item.RequiresClosedApps![0]);
        Assert.Equal(StepStatus.Pending, loaded.Steps[2].Status);
        Assert.Equal(@"X:\d\c.txt", loaded.Steps[2].Item.DestinationPath);
    }

    [Fact]
    public async Task Torn_last_line_is_ignored_and_new_records_still_append_cleanly()
    {
        using var sb = new TestSandbox();
        var journal = new RunJournal(sb.Settings.DataRoot);
        var plan = ThreeItems();
        var run = await journal.CreateAsync(plan, ExecutionMode.Real, default);
        await journal.SaveStepAsync(plan.RunId, run.Steps[0] with { Status = StepStatus.Done }, default);
        await journal.SaveStepAsync(plan.RunId, run.Steps[1] with { Status = StepStatus.Done }, default);
        journal.SimulateProcessDeath();

        // Power cut in the middle of writing the second record: cut the file mid-line.
        var steps = Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), plan.RunId, "steps.jsonl");
        var bytes = File.ReadAllBytes(steps);
        var secondLineStart = Array.IndexOf(bytes, (byte)'\n') + 1;
        File.WriteAllBytes(steps, bytes[..(secondLineStart + 25)]);

        var fresh = new RunJournal(sb.Settings.DataRoot);
        var loaded = (await fresh.LoadAsync(plan.RunId, default))!;
        Assert.Equal(RunStatus.Interrupted, loaded.Status);
        Assert.Equal(StepStatus.Done, loaded.Steps[0].Status);
        Assert.Equal(StepStatus.Pending, loaded.Steps[1].Status); // the torn record is ignored

        await fresh.SaveStepAsync(plan.RunId, loaded.Steps[2] with { Status = StepStatus.Skipped, Message = "after crash" }, default);
        var again = (await new RunJournal(sb.Settings.DataRoot).LoadAsync(plan.RunId, default))!;
        Assert.Equal(StepStatus.Skipped, again.Steps[2].Status);
        Assert.Equal("after crash", again.Steps[2].Message);
        fresh.Dispose();
    }

    [Fact]
    public async Task Crash_leaves_run_interrupted_and_findable_but_simulations_never_are()
    {
        using var sb = new TestSandbox();
        var journal = new RunJournal(sb.Settings.DataRoot);
        var real = ThreeItems();
        var sim = ThreeItems();
        await journal.CreateAsync(real, ExecutionMode.Real, default);
        await journal.CreateAsync(sim, ExecutionMode.Simulate, default);

        // Another journal (another process) sees the live run as Running while the lock is held.
        Assert.Equal(RunStatus.Running, (await new RunJournal(sb.Settings.DataRoot).LoadAsync(real.RunId, default))!.Status);
        Assert.Empty(await new RunJournal(sb.Settings.DataRoot).FindUnfinishedAsync(default));

        journal.SimulateProcessDeath();
        var after = new RunJournal(sb.Settings.DataRoot);
        var unfinished = Assert.Single(await after.FindUnfinishedAsync(default));
        Assert.Equal(real.RunId, unfinished.RunId);
        Assert.Equal(RunStatus.Interrupted, unfinished.Status);
        Assert.Equal(RunStatus.Planned, (await after.LoadAsync(sim.RunId, default))!.Status);
    }

    [Fact]
    public async Task Header_save_supersedes_earlier_step_records()
    {
        using var sb = new TestSandbox();
        using var journal = new RunJournal(sb.Settings.DataRoot);
        var plan = ThreeItems();
        var run = await journal.CreateAsync(plan, ExecutionMode.Real, default);
        await journal.SaveStepAsync(plan.RunId, run.Steps[0] with { Status = StepStatus.Done }, default);
        var undone = run with
        {
            Status = RunStatus.Undone,
            Steps = new[] { run.Steps[0] with { Status = StepStatus.Undone }, run.Steps[1], run.Steps[2] },
        };
        await journal.SaveRunAsync(undone, default);

        var loaded = (await new RunJournal(sb.Settings.DataRoot).LoadAsync(plan.RunId, default))!;
        Assert.Equal(RunStatus.Undone, loaded.Status);
        Assert.Equal(StepStatus.Undone, loaded.Steps[0].Status);
        Assert.False(File.Exists(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), plan.RunId, "run.lock")));
    }

    [Fact]
    public async Task List_is_newest_first_and_skips_garbage_folders()
    {
        using var sb = new TestSandbox();
        using var journal = new RunJournal(sb.Settings.DataRoot);
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var p = ThreeItems() with { RunId = $"2026010{i + 1}-000000-run00{i}" };
            var r = await journal.CreateAsync(p, ExecutionMode.Simulate, default);
            await journal.SaveRunAsync(r with { CreatedUtc = new DateTime(2026, 1, i + 1, 0, 0, 0, DateTimeKind.Utc), Status = RunStatus.Simulated }, default);
            ids.Add(p.RunId);
        }
        Directory.CreateDirectory(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), "not a run"));
        File.WriteAllText(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), "not a run", "run.json"), "{ broken");

        var list = await journal.ListAsync(default);
        Assert.Equal(ids.AsEnumerable().Reverse(), list.Select(r => r.RunId));
    }

    [Fact]
    public async Task Step_records_are_one_flushed_json_line_each()
    {
        using var sb = new TestSandbox();
        using var journal = new RunJournal(sb.Settings.DataRoot);
        var plan = ThreeItems();
        var run = await journal.CreateAsync(plan, ExecutionMode.Real, default);
        foreach (var s in run.Steps) await journal.SaveStepAsync(plan.RunId, s with { Status = StepStatus.Started }, default);
        // The journal keeps steps.jsonl open while the run is live, so read it the way PC Tidy does (shared).
        using var fs = new FileStream(Path.Combine(DataPaths.Runs(sb.Settings.DataRoot), plan.RunId, "steps.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{\"Seq\":", l));
        Assert.EndsWith("\n", text);
    }
}
