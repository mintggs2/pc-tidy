using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

/// <summary>
/// 3.1.2: the admin helper keeps its own delete cap (it never takes the app's "the user confirmed the size"). A request
/// it refuses for that cap changed nothing, so when the user confirmed the size of the run the executor sends it again in
/// halves; only a step that is over the limit by itself stays refused. Without that confirmation the refusal stands.
/// </summary>
public class AdminCapSplitTests
{
    private const string CapMessage = DeleteCap.StoppedPrefix + " more than 50,000 files, the safety limit. Test.";

    /// <summary>A helper that refuses every request holding more than <paramref name="maxSteps"/> steps, like a cap would.</summary>
    private sealed class CappedHelper(int maxSteps) : IElevationService
    {
        public bool IsElevated => false;
        public List<int> Requests { get; } = new();

        public Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
        {
            Requests.Add(steps.Count);
            return Task.FromResult<IReadOnlyList<StepRecord>>(steps.Count > maxSteps
                ? steps.Select(s => s with { Status = StepStatus.Skipped, Message = CapMessage }).ToList()
                : steps.Select(s => s with { Status = StepStatus.Done, Message = "Done by the helper." }).ToList());
        }
    }

    private sealed class AdminHandler : IActionHandler
    {
        public bool CanHandle(PlanItem item) => item.Kind == ActionKind.PowerPlanSet;
        public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Done });
        public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Simulated });
        public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step with { Status = StepStatus.Undone });
        public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(false);
    }

    /// <param name="confirmed">The user said yes to the size of this run (the app's cap override).</param>
    private static PlanItem AdminItem(bool confirmed = true) => new()
    {
        Kind = ActionKind.PowerPlanSet, FeatureId = "test", Description = "Power plan", NeedsAdmin = true,
        Args = confirmed
            ? new() { ["schemeGuid"] = Guid.NewGuid().ToString(), [EngineSettings.CapOverrideArg] = "true" }
            : new() { ["schemeGuid"] = Guid.NewGuid().ToString() },
    };

    [Fact]
    public async Task A_request_the_helper_refuses_for_its_cap_is_split_until_each_part_fits()
    {
        using var sb = new TestSandbox();
        var helper = new CappedHelper(maxSteps: 1);
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { new AdminHandler() });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem(), AdminItem(), AdminItem(), AdminItem()), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal(new[] { 4, 2, 1, 1, 2, 1, 1 }, helper.Requests);
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task A_single_step_over_the_helper_cap_stays_refused_and_nothing_is_retried_forever()
    {
        using var sb = new TestSandbox();
        var helper = new CappedHelper(maxSteps: 0);
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { new AdminHandler() });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem(), AdminItem()), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.All(run.Steps, s => Assert.Equal(CapMessage, s.Message));
        Assert.Equal(new[] { 2, 1, 1 }, helper.Requests);
    }

    [Fact]
    public async Task Without_the_users_size_confirmation_a_capped_admin_request_is_not_split()
    {
        using var sb = new TestSandbox();
        var helper = new CappedHelper(maxSteps: 1);
        var engine = sb.Engine(elevation: helper, locations: new NonSandboxLocations(sb.Locations), extraHandlers: new[] { new AdminHandler() });

        var run = await engine.Executor.ExecuteAsync(Plan(AdminItem(confirmed: false), AdminItem(confirmed: false)), Real, null, default);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.Equal(new[] { 2 }, helper.Requests);
    }

    [Fact]
    public async Task The_apps_size_confirmation_lets_an_oversized_plan_run_and_without_it_nothing_changes()
    {
        using var sb = new TestSandbox();
        var folder = Path.Combine(sb.Work(), "big");
        for (int i = 0; i < 5; i++) WriteFile(Path.Combine(folder, $"f{i}.tmp"), 10, i);
        sb.Settings.SetFeature(EngineSettings.FeatureId, new EngineSettings { MaxRemoveItemsPerRun = 3 });
        var engine = sb.Engine();
        var plan = Plan(Item(ActionKind.QuarantinePath, folder, isDir: true));
        var measure = new RemovalMeasure(sb.Settings);

        var size = measure.Measure(plan, default);
        Assert.True(size.OverLimit);
        Assert.Equal(5, size.Files);
        Assert.True(size.QuarantineOnly);

        var refused = await engine.Executor.ExecuteAsync(plan, Real, null, default);
        Assert.Equal(StepStatus.Skipped, refused.Steps[0].Status);
        Assert.True(DeleteCap.IsCapMessage(refused.Steps[0].Message));
        Assert.True(Exists(folder));

        var ran = await engine.Executor.ExecuteAsync(measure.WithSizeConfirmed(plan with { RunId = ActionPlan.NewRunId() }), Real, null, default);
        Assert.Equal(StepStatus.Done, ran.Steps[0].Status);
        Assert.False(Exists(folder));
    }
}
