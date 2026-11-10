using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>PlanItem.DependsOn: a step runs only if the earlier steps it needs finished.</summary>
public class DependsOnTests
{
    [Fact]
    public async Task A_step_is_skipped_when_the_step_it_needs_was_skipped()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var missing = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "gone.txt"), 10, 1));
        File.Delete(missing.SourcePath!); // changed since the scan: skipped
        var keep = WriteFile(Path.Combine(work, "keep.txt"), 10, 2);
        var dependent = ScannedItem(ActionKind.QuarantinePath, keep) with { DependsOn = new[] { missing.Id } };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(missing, dependent), Real, null, default);

        Assert.NotEqual(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status);
        Assert.Contains("needs", run.Steps[1].Message);
        Assert.True(Exists(keep));
    }

    [Fact]
    public async Task A_step_runs_when_the_step_it_needs_finished()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var dir = Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "New"));
        var file = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "a.txt"), 10, 1)) with { DependsOn = new[] { dir.Id } };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(dir, file), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
    }

    [Fact]
    public async Task A_rename_in_a_group_of_renames_is_skipped_when_the_step_it_needs_was_skipped()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var missing = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "gone.txt"), 10, 1));
        File.Delete(missing.SourcePath!); // skipped: it's no longer there
        var a = WriteFile(Path.Combine(work, "a.txt"), 10, 2);
        var b = WriteFile(Path.Combine(work, "b.txt"), 10, 3);
        // Two consecutive renames (run as one group); only the second depends on the skipped step.
        var free = ScannedItem(ActionKind.RenamePath, a) with { DestinationPath = Path.Combine(work, "a2.txt") };
        var needy = ScannedItem(ActionKind.RenamePath, b) with { DestinationPath = Path.Combine(work, "b2.txt"), DependsOn = new[] { missing.Id } };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(missing, free, needy), Real, null, default);

        Assert.Equal(StepStatus.Done, run.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, run.Steps[2].Status);
        Assert.Contains("needs", run.Steps[2].Message);
        Assert.True(Exists(b));
        Assert.False(Exists(Path.Combine(work, "b2.txt")));
    }

    [Fact]
    public async Task A_need_that_comes_later_or_is_missing_blocks_the_step()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var file = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "a.txt"), 10, 1)) with { DependsOn = new[] { "not-in-plan" } };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(file), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
    }

    [Fact]
    public async Task Simulation_follows_the_same_rule()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var dir = Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "New"));
        var ok = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "a.txt"), 10, 1)) with { DependsOn = new[] { dir.Id } };
        var blocked = ScannedItem(ActionKind.QuarantinePath, WriteFile(Path.Combine(work, "b.txt"), 10, 2)) with { DependsOn = new[] { "nope" } };

        var run = await sb.Engine().Executor.ExecuteAsync(Plan(dir, ok, blocked), Simulate, null, default);

        Assert.Equal(StepStatus.Simulated, run.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, run.Steps[2].Status);
    }

    [Fact]
    public async Task A_user_step_that_needs_an_admin_step_runs_after_it_and_only_if_it_succeeded()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var elevation = new FakeElevation { Run = s => s with { Status = StepStatus.Skipped, Message = "helper said no" } };
        var engine = sb.Engine(elevation: elevation, locations: new NonSandboxLocations(sb.Locations));
        var admin = Item(ActionKind.CreateDirectory, dest: Path.Combine(work, "AdminDir")) with { NeedsAdmin = true };
        var keep = WriteFile(Path.Combine(work, "keep.txt"), 10, 3);
        var user = ScannedItem(ActionKind.QuarantinePath, keep) with { DependsOn = new[] { admin.Id } };

        var run = await engine.Executor.ExecuteAsync(Plan(admin, user), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, run.Steps[1].Status);
        Assert.True(Exists(keep));
    }

    [Fact]
    public void Normalize_keeps_needs_pointing_at_the_kept_folder_step()
    {
        var a = new PlanItem { Kind = ActionKind.CreateDirectory, FeatureId = "t", Description = "d", DestinationPath = @"C:\x\New" };
        var b = a with { Id = Guid.NewGuid().ToString("N") };
        var user = new PlanItem { Kind = ActionKind.MovePath, FeatureId = "t", Description = "m", SourcePath = @"C:\x\f", DestinationPath = @"C:\x\New\f", DependsOn = new[] { b.Id } };

        var normalized = PlanBuilder.Normalize(new[] { a, b, user });

        Assert.Equal(2, normalized.Count);
        Assert.Equal(new[] { a.Id }, normalized[1].DependsOn);
    }
}
