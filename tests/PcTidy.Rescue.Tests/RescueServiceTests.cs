using PcTidy.Core.Model;

namespace PcTidy.Rescue.Tests;

[Collection("rescue")] // sequential: each test generates a few thousand files
public class RescueServiceTests
{
    [Fact]
    public async Task Undo_through_rescue_puts_quarantined_and_moved_files_back_exactly()
    {
        using var bed = new RescueBed("undo");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan());
        Assert.Equal(6, run.DoneCount);
        Assert.False(File.Exists(bed.Downloads("ChromeSetup.exe")));
        Assert.True(File.Exists(bed.Downloads(@"Pictures\DSC_0042.JPG")));

        using var rescue = bed.OpenRescue();
        var entry = Assert.Single(await rescue.ListRunsAsync());
        Assert.Equal(run.RunId, entry.RunId);
        Assert.True(entry.CanUndo);
        Assert.False(entry.CanResume);
        Assert.Equal(3, entry.QuarantineLeft);
        Assert.Equal(6, entry.ItemCount);

        var result = await rescue.UndoAsync(run.RunId);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Details));
        bed.AssertIdenticalToStart();

        var after = await rescue.FindAsync(run.RunId);
        Assert.Equal(RunStatus.Undone, after!.Run.Status);
        Assert.False(after.CanUndo);
        Assert.Equal(0, after.QuarantineLeft);
        Assert.False((await rescue.UndoAsync(run.RunId)).Ok); // nothing left: refused, not crashed
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_and_simulations_cant_be_undone()
    {
        using var bed = new RescueBed("list");
        var sim = await bed.RunAsApp(bed.QuarantineAndMovePlan(), ExecutionMode.Simulate);
        await Task.Delay(20);
        var real = await bed.RunAsApp(bed.QuarantineAndMovePlan(includeMoves: false));

        using var rescue = bed.OpenRescue();
        var runs = await rescue.ListRunsAsync();
        Assert.Equal(new[] { real.RunId, sim.RunId }, runs.Select(r => r.RunId));
        Assert.False(runs[1].CanUndo);
        Assert.Contains("Simulation", runs[1].StatusText);
        Assert.Equal(real.RunId, (await rescue.LastUndoableAsync())!.RunId);

        Assert.True((await rescue.UndoAsync(real.RunId)).Ok);
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Interrupted_run_can_be_resumed_then_undone()
    {
        using var bed = new RescueBed("resume");
        var runId = await bed.CrashMidRun(bed.QuarantineAndMovePlan(), finishFirst: false);
        Assert.True(File.Exists(bed.Downloads("ChromeSetup.exe"))); // the crash came before any change

        using var rescue = bed.OpenRescue();
        var entry = (await rescue.FindAsync(runId))!;
        Assert.True(entry.IsInterrupted);
        Assert.True(entry.CanResume);
        Assert.True(entry.CanRollback);
        Assert.False(entry.CanUndo);
        Assert.False((await rescue.UndoAsync(runId)).Ok); // interrupted: Undo points to Resume / Roll back

        var resumed = await rescue.ResumeAsync(runId);
        Assert.True(resumed.Ok, resumed.Message + string.Join("\n", resumed.Details));
        Assert.False(File.Exists(bed.Downloads("ChromeSetup.exe")));
        Assert.True(File.Exists(bed.Downloads(@"Music\Artist - Song.mp3")));

        Assert.True((await rescue.FindAsync(runId))!.CanUndo);
        Assert.True((await rescue.UndoAsync(runId)).Ok);
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Interrupted_run_can_be_rolled_back_exactly()
    {
        using var bed = new RescueBed("rollback");
        var runId = await bed.CrashMidRun(bed.QuarantineAndMovePlan(), finishFirst: true);
        Assert.False(File.Exists(bed.Downloads("ChromeSetup.exe")));

        using var rescue = bed.OpenRescue();
        Assert.True((await rescue.FindAsync(runId))!.IsInterrupted);
        var result = await rescue.RollbackAsync(runId);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Details));
        Assert.Equal(RunStatus.RolledBack, (await rescue.FindAsync(runId))!.Run.Status);
        bed.AssertIdenticalToStart();
    }

    [Fact]
    public async Task Restore_everything_in_quarantine_puts_each_item_back()
    {
        using var bed = new RescueBed("quarantine");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan(includeMoves: false));
        Assert.Equal(3, run.DoneCount);

        using var rescue = bed.OpenRescue();
        var result = await rescue.RestoreQuarantineAsync(run.RunId);
        Assert.True(result.Ok, result.Message + string.Join("\n", result.Details));
        Assert.Contains("3", result.Message);
        bed.AssertIdenticalToStart();
        Assert.False((await rescue.FindAsync(run.RunId))!.CanRestoreQuarantine);
        Assert.False((await rescue.RestoreQuarantineAsync(run.RunId)).Ok);
    }

    [Fact]
    public async Task Restore_quarantine_never_overwrites_a_file_that_came_back_meanwhile()
    {
        using var bed = new RescueBed("no-overwrite");
        var run = await bed.RunAsApp(bed.QuarantineAndMovePlan(includeMoves: false));
        var squatter = bed.Downloads("ChromeSetup.exe");
        await File.WriteAllTextAsync(squatter, "a new download with the same name");

        using var rescue = bed.OpenRescue();
        var result = await rescue.RestoreQuarantineAsync(run.RunId);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("a new download with the same name", await File.ReadAllTextAsync(squatter));
        Assert.Contains(result.Details, d => d.Contains("(restored)"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(squatter)!, "ChromeSetup (restored)*.exe"));
    }

    [Fact]
    public async Task Unknown_run_ids_and_bad_ids_are_answered_not_thrown()
    {
        using var bed = new RescueBed("unknown");
        using var rescue = bed.OpenRescue();
        Assert.Empty(await rescue.ListRunsAsync());
        Assert.Null(await rescue.FindAsync("20990101-000000-abcdef"));
        Assert.Null(await rescue.FindAsync(@"..\..\evil"));
        Assert.False((await rescue.UndoAsync("nope")).Ok);
        Assert.False((await rescue.ResumeAsync("nope")).Ok);
        Assert.False((await rescue.RollbackAsync("nope")).Ok);
        Assert.False((await rescue.RestoreQuarantineAsync("nope")).Ok);
        Assert.Null(await rescue.LastUndoableAsync());
    }

    [Fact]
    public void Missing_practice_folder_is_a_friendly_error()
    {
        var missing = Path.Combine(RescueBed.TestbedRoot, "does-not-exist-" + Guid.NewGuid().ToString("N")[..6]);
        var ex = Assert.Throws<DirectoryNotFoundException>(() => RescueService.Open(RescueTarget.ForSandbox(missing)));
        Assert.Contains("doesn't exist", ex.Message);
        Assert.False(Directory.Exists(missing));
    }
}

[CollectionDefinition("rescue", DisableParallelization = true)]
public class RescueCollection { }
