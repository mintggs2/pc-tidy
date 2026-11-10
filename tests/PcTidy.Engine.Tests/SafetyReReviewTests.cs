using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>Regression tests for the safety re-review (device paths, admin restore folders, unfinished helper steps).</summary>
public class SafetyReReviewTests
{
    // ------------------------------------------------------------------ (1) device / object-manager / UNC paths

    [Theory]
    [InlineData(@"\\.\C:\..\HarddiskVolume3\x.txt")]
    [InlineData(@"\\.\C:\x.txt")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\x.txt")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\x.txt")]
    [InlineData(@"\??\C:\x.txt")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"\\?\UNC\server\share\x.txt")]
    [InlineData(@"\\?\C:\a\..\x.txt")]
    [InlineData(@"C:\a\..\x.txt")]
    [InlineData(@"C:x.txt")]
    public void Literal_paths_refuse_device_object_manager_and_UNC_forms(string path)
        => Assert.Throws<ArgumentException>(() => LongPath.Io(path));

    [Theory]
    [InlineData(@"C:\Users\tester\x.txt", @"\\?\C:\Users\tester\x.txt")]
    [InlineData(@"\\?\C:\Users\tester\x.txt", @"\\?\C:\Users\tester\x.txt")]
    [InlineData(@"D:\trailing dot.", @"\\?\D:\trailing dot.")]
    [InlineData(@"D:\", @"\\?\D:\")]
    public void Plain_drive_paths_are_still_taken_literally(string path, string io)
        => Assert.Equal(io, LongPath.Io(path));

    [Fact]
    public async Task A_device_path_to_a_real_file_is_never_acted_on()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "victim.txt"), 100, 1);
        var engine = sb.Engine();

        var run = await engine.Executor.ExecuteAsync(Plan(Item(ActionKind.QuarantinePath, @"\\.\" + file)), Real, null, default);

        Assert.Equal(StepStatus.Skipped, run.Steps[0].Status);
        Assert.True(Exists(file));
        Assert.Empty(await engine.Quarantine.ListAsync(run.RunId, default));
    }

    // ------------------------------------------------------------------ (2) admin restores never create folders by name

    [Fact]
    public async Task Admin_store_restore_refuses_when_the_original_folder_is_gone_and_creates_nothing()
    {
        using var sb = new TestSandbox();
        var store = QuarantineStore.CreateAdminStore(Path.Combine(sb.Root, "Admin"), sb.Locations, sb.Settings, () => { });
        var folder = Path.Combine(sb.Work(), "gone later");
        var file = WriteFile(Path.Combine(folder, "f.txt"), 10, 1);
        var entry = await store.QuarantineAsync(file, "20260101-000000-adminp", default);
        Directory.Delete(LongPath.Io(folder));

        var ex = await Assert.ThrowsAsync<IOException>(() => store.RestoreToAsync(entry, file, default));

        Assert.Contains("folder it came from is gone", ex.Message);
        Assert.False(Exists(folder));
        Assert.False((await store.FindAsync("20260101-000000-adminp", entry.Id, default))!.Restored);
    }

    [Fact]
    public async Task Normal_store_restore_still_recreates_a_missing_folder()
    {
        using var sb = new TestSandbox();
        var engine = sb.Engine();
        var folder = Path.Combine(sb.Work(), "gone later");
        var file = WriteFile(Path.Combine(folder, "f.txt"), 10, 1);
        var entry = await engine.Quarantine.QuarantineAsync(file, "20260101-000000-normal", default);
        Directory.Delete(LongPath.Io(folder));

        Assert.Equal(file, await engine.Quarantine.RestoreToAsync(entry, file, default));
        Assert.True(Exists(file));
    }

    // ------------------------------------------------------------------ (3) M1: unfinished admin steps are settled by the helper

    /// <summary>A platform-kind admin step whose local check must never be consulted for helper steps.</summary>
    private sealed class SpyAdminHandler : IActionHandler
    {
        public int LocalChecks;
        public bool CanHandle(PlanItem item) => item.Kind == ActionKind.PowerPlanSet;
        public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Done });
        public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Simulated });
        public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Undone });
        public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        {
            LocalChecks++;
            return Task.FromResult(false); // the app can't see the helper's quarantine: it would wrongly say "didn't happen"
        }
    }


    /// <summary>An elevation service WITHOUT the settle request (it can't be asked; its steps must never be re-run).</summary>
    private sealed class HelperWithoutSettle(Func<bool, StepRecord, StepRecord> answer) : IElevationService
    {
        public bool IsElevated => false;
        public List<(bool Undo, IReadOnlyList<StepRecord> Steps)> Calls { get; } = new();

        public Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
        {
            Calls.Add((undo, steps));
            return Task.FromResult<IReadOnlyList<StepRecord>>(steps.Select(s => answer(undo, s)).ToList());
        }
    }

    private static PlanItem AdminItem() => new()
    {
        Kind = ActionKind.PowerPlanSet, FeatureId = "test", Description = "Power plan", NeedsAdmin = true,
        Args = new() { ["schemeGuid"] = Guid.NewGuid().ToString() },
    };

    private static StepRecord PartWay(StepRecord s) => s with { Status = StepStatus.Started, Message = "Stopped part-way." };

    [Fact]
    public async Task A_step_the_helper_left_Started_is_settled_from_its_ledger_and_the_answer_is_recorded()
    {
        using var sb = new TestSandbox();
        var spy = new SpyAdminHandler();
        var helper = new FakeElevation
        {
            Run = PartWay,
            Settle = s => s with { Status = StepStatus.Done, Message = "It finished before PC Tidy's admin helper stopped.", UndoData = new() { ["helper"] = "1" } },
        };
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { spy });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem()), Real, null, default);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("1", run.Steps[0].UndoData!["helper"]);
        Assert.Single(helper.Batches); // never run a second time to find out
        Assert.Equal(StepStatus.Started, Assert.Single(Assert.Single(helper.Settles)).Status);
        Assert.Equal(0, spy.LocalChecks);
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Without_an_answer_the_step_stays_Started_and_Undo_asks_the_helper_again_then_undoes_through_it()
    {
        using var sb = new TestSandbox();
        var spy = new SpyAdminHandler();
        var helper = new FakeElevation
        {
            Run = s => s.Status == StepStatus.Done || s.Status == StepStatus.Started && s.Message == Executor.UnsettledMessage
                ? s with { Status = StepStatus.Undone, Message = "Undone by the helper." } // the undo request
                : PartWay(s),
            Settle = null, // unreachable: steps come back unchanged
        };
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { spy });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem()), Real, null, default);

        Assert.Equal(StepStatus.Started, run.Steps[0].Status);
        Assert.Equal(Executor.UnsettledMessage, run.Steps[0].Message);
        Assert.Equal(RunStatus.Interrupted, run.Status); // Resume / Roll back / Undo are offered
        Assert.Equal(StepStatus.Started, (await engine.Journal.LoadAsync(run.RunId, default))!.Steps[0].Status);

        var report = await engine.Undo.UndoRunAsync(run.RunId, null, default);

        Assert.Equal(2, helper.Settles.Count); // asked during the run and again before the undo
        Assert.Equal(1, report.Restored);
        Assert.Equal(0, spy.LocalChecks); // never judged with the app's own context
        Assert.Equal(StepStatus.Undone, (await engine.Journal.LoadAsync(run.RunId, default))!.Steps[0].Status);
    }

    [Fact]
    public async Task A_lost_helper_isnt_asked_during_the_run_and_Resume_settles_once_it_answers()
    {
        using var sb = new TestSandbox();
        var spy = new SpyAdminHandler();
        var answerDone = false;
        var helper = new FakeElevation
        {
            Run = _ => throw new IOException("The helper stopped."),
            Settle = s => answerDone ? s with { Status = StepStatus.Done, Message = "It finished before PC Tidy's admin helper stopped." } : s,
        };
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { spy });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem()), Real, null, default);
        Assert.Equal(StepStatus.Started, run.Steps[0].Status);
        Assert.Empty(helper.Settles); // not relaunched for it mid-run

        // Still no answer: the step stays Started (not Pending, which would run it again; not Skipped, which would lose it).
        var resumed = await engine.Undo.ResumeAsync(run.RunId, null, default);
        Assert.Equal(StepStatus.Started, resumed.Steps[0].Status);
        Assert.Equal(RunStatus.Interrupted, resumed.Status);

        answerDone = true;
        resumed = await engine.Undo.ResumeAsync(run.RunId, null, default);
        Assert.Equal(StepStatus.Done, resumed.Steps[0].Status);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Single(helper.Batches); // the step was never sent to run again
        Assert.Equal(0, spy.LocalChecks);
    }

    [Fact]
    public async Task An_elevation_service_that_cant_settle_never_gets_the_step_to_run_again()
    {
        using var sb = new TestSandbox();
        var spy = new SpyAdminHandler();
        var helper = new HelperWithoutSettle((undo, s) => undo ? s with { Status = StepStatus.Undone } : PartWay(s));
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { spy });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem()), Real, null, default);
        var resumed = await engine.Undo.ResumeAsync(run.RunId, null, default);

        Assert.Equal(StepStatus.Started, resumed.Steps[0].Status);
        Assert.Single(helper.Calls); // only the original request: Resume didn't re-run it
        Assert.Equal(0, spy.LocalChecks);
    }

    [Fact]
    public void The_platform_helpers_settle_request_is_the_one_the_engine_uses()
    {
        using var sb = new TestSandbox();
        var inProcess = new PcTidy.Platform.ElevatedBatchExecutor(EngineFactory.CreateFileHandlers(), () => throw new InvalidOperationException("not used"));
        using var service = new PcTidy.Platform.ElevationService(sb.Locations, inProcess);
        Assert.NotNull(HelperSettlement.For(service));
        Assert.Null(HelperSettlement.For(new HelperWithoutSettle((_, s) => s)));
    }
}
