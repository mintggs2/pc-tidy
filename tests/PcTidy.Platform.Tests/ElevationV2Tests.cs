using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>Helper additions: settling Started steps from the ledger, caps the app can't override, admin quarantine requests.</summary>
public class ElevationV2Tests
{
    private static PlanItem Service(string name = "ContosoSync") => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = "test", Description = "test", NeedsAdmin = true, ServiceName = name, ServiceStartType = ServiceStart.Manual,
    };

    private static PlanItem Quarantine(string path, bool capOverride = false) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = "test", NeedsAdmin = true, SourcePath = path, ExpectedSize = 100,
        Args = capOverride ? new() { ["capOverride"] = "true" } : null,
    };

    /// <summary>A handler that stops part-way (like a helper that died) and then reports whether the change happened.</summary>
    private sealed class StopsPartWay(bool happened) : IActionHandler
    {
        public bool CanHandle(PlanItem item) => item.Kind == ActionKind.ServiceSetStartType;
        public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => throw new OperationCanceledException();
        public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => throw new NotSupportedException();
        public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(step.Finish(StepStatus.Undone, "undone"));
        public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.FromResult(happened);
    }

    private static HelperLedger Ledger(TestSandbox sandbox) => new(Path.Combine(sandbox.Root, "Admin"), () => { });

    [Theory]
    [InlineData(true, StepStatus.Done)]
    [InlineData(false, StepStatus.Skipped)]
    public async Task A_step_the_helper_only_started_is_settled_from_its_ledger_and_recorded(bool happened, StepStatus expected)
    {
        using var sandbox = new TestSandbox();
        var ledger = Ledger(sandbox);
        var executor = new ElevatedBatchExecutor(new IActionHandler[] { new StopsPartWay(happened) }, () => sandbox.Context(), policy: new ElevatedHelperPolicy { Ledger = ledger });
        var item = Service();
        var ran = await executor.RunAsync("run-s1", new[] { Steps.Of(item, 0) }, ExecutionMode.Real, undo: false, default);
        Assert.Equal(StepStatus.Started, ran[0].Status);

        var settled = await executor.SettleAsync("run-s1", ran, default);
        Assert.Equal(expected, settled[0].Status);
        // Recorded as finished: asking again gives the same answer, and running it again is refused.
        Assert.Equal(expected, (await executor.SettleAsync("run-s1", ran, default))[0].Status);
        Assert.Equal(HelperLedger.Finished, ledger.Find("run-s1", 0)!.State);
    }

    [Fact]
    public async Task A_finished_step_whose_answer_was_lost_comes_back_with_the_helpers_own_result()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(), policy: new ElevatedHelperPolicy { Ledger = Ledger(sandbox) });
        var item = Service();
        await executor.RunAsync("run-s2", new[] { Steps.Of(item, 0) }, ExecutionMode.Real, undo: false, default);

        var settled = await executor.SettleAsync("run-s2", new[] { Steps.Of(item, 0, StepStatus.Started) }, default);
        Assert.Equal(StepStatus.Done, settled[0].Status);
        Assert.Equal("v", settled[0].UndoData!["k"]);

        var never = await executor.SettleAsync("run-s2", new[] { Steps.Of(item, 5, StepStatus.Started) }, default);
        Assert.Equal(StepStatus.Skipped, never[0].Status);
        var swapped = await executor.SettleAsync("run-s2", new[] { Steps.Of(Service("Other"), 0, StepStatus.Started) }, default);
        Assert.Equal(StepStatus.Skipped, swapped[0].Status);
        Assert.Single(handler.Calls); // settling never runs anything
    }

    [Fact]
    public async Task The_helper_ignores_cap_overrides_from_the_app()
    {
        using var sandbox = new TestSandbox();
        var seen = new List<PlanItem>();
        var executor = new ElevatedBatchExecutor(new[] { new RecordingHandler(ActionKind.QuarantinePath) }, () => sandbox.Context(),
            policy: new ElevatedHelperPolicy { Ledger = Ledger(sandbox), DeleteCap = (items, _) => { seen.AddRange(items); return "over the limit"; } });

        var results = await executor.RunAsync("run-c1", new[] { Steps.Of(Quarantine(@"C:\Windows\Temp\a.tmp", capOverride: true), 0) }, ExecutionMode.Real, undo: false, default);

        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal("over the limit", results[0].Message);
        Assert.All(seen, i => Assert.False(i.Args?.ContainsKey("capOverride") ?? false));
    }

    [Fact]
    public async Task The_per_run_cap_counts_what_earlier_requests_already_removed()
    {
        using var sandbox = new TestSandbox();
        var ledger = Ledger(sandbox);
        var earlier = Enumerable.Range(0, 3).Select(i => (HelperLedger.Finished, Steps.Of(Quarantine($@"C:\Windows\Temp\{i}.tmp"), i) with { Status = StepStatus.Done, BytesAffected = 100 }));
        ledger.Append("run-c2", earlier);
        Assert.Equal((3, 300L), ledger.RemovedSoFar("run-c2"));

        var handler = new RecordingHandler(ActionKind.QuarantinePath);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(), policy: new ElevatedHelperPolicy { Ledger = ledger, RunCap = (4, 10_000) });
        var next = new[] { Steps.Of(Quarantine(@"C:\Windows\Temp\x.tmp"), 10), Steps.Of(Quarantine(@"C:\Windows\Temp\y.tmp"), 11) };
        var results = await executor.RunAsync("run-c2", next, ExecutionMode.Real, undo: false, default);

        Assert.All(results, r => Assert.Equal(StepStatus.Skipped, r.Status));
        Assert.Contains("safety limit", results[0].Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Admin_quarantine_is_listed_and_purged_only_for_runs_the_helper_quarantined_for()
    {
        using var sandbox = new TestSandbox();
        var ledger = Ledger(sandbox);
        var store = new TestbedQuarantine(Path.Combine(sandbox.Root, "Q"));
        store.Entries.Add(new QuarantineEntry { Id = "q1", RunId = "run-q", OriginalPath = @"C:\Windows\Temp\a", StoredPath = @"X:\q1" });
        var executor = new ElevatedBatchExecutor(Array.Empty<IActionHandler>(), () => sandbox.Context().With(store), policy: new ElevatedHelperPolicy { Ledger = ledger });

        var list = await ElevatedHost.HandleAsync(new ElevatedRequest { Kind = ElevatedRequestKind.ListAdminQuarantine }, executor, () => sandbox.Context(), default);
        Assert.Equal("q1", list.Quarantine!.Single().Id);

        var refused = await ElevatedHost.HandleAsync(new ElevatedRequest { Kind = ElevatedRequestKind.PurgeAdminQuarantine, RunId = "run-q" }, executor, () => sandbox.Context(), default);
        Assert.False(refused.Purged);
        Assert.NotNull(refused.Error);
        var bad = await ElevatedHost.HandleAsync(new ElevatedRequest { Kind = ElevatedRequestKind.PurgeAdminQuarantine, RunId = @"..\..\x" }, executor, () => sandbox.Context(), default);
        Assert.False(bad.Purged);
        Assert.Empty(store.Purged);

        ledger.Append("run-q", new[] { (HelperLedger.Finished, Steps.Of(Quarantine(@"C:\Windows\Temp\a"), 0) with { Status = StepStatus.Done }) });
        var purged = await ElevatedHost.HandleAsync(new ElevatedRequest { Kind = ElevatedRequestKind.PurgeAdminQuarantine, RunId = "run-q" }, executor, () => sandbox.Context(), default);
        Assert.True(purged.Purged);
        Assert.Equal(new[] { "run-q" }, store.Purged);
    }

    [Fact]
    public async Task Without_a_ledger_there_is_no_separate_admin_quarantine_and_settle_uses_the_handler()
    {
        using var sandbox = new TestSandbox();
        var executor = new ElevatedBatchExecutor(new IActionHandler[] { new StopsPartWay(true) }, () => sandbox.Context());
        using var service = new ElevationService(sandbox.Locations, executor);

        Assert.Empty((await service.ListAdminQuarantineAsync(default)).Entries);
        Assert.NotNull(await service.PurgeAdminQuarantineAsync("run-x", default));
        var settled = await service.SettleElevatedAsync("run-x", new[] { Steps.Of(Service(), 0, StepStatus.Started) }, default);
        Assert.Equal(StepStatus.Done, settled[0].Status);
    }

    [Fact]
    public void Settle_and_admin_quarantine_messages_round_trip()
    {
        var response = new ElevatedResponse { Quarantine = new[] { new QuarantineEntry { Id = "a", RunId = "r", OriginalPath = "o", StoredPath = "s" } }, Purged = true };
        var back = ElevationProtocol.Deserialize<ElevatedResponse>(ElevationProtocol.Serialize(response));
        Assert.True(back.Purged);
        Assert.Equal("a", back.Quarantine!.Single().Id);
        var request = ElevationProtocol.Deserialize<ElevatedRequest>(ElevationProtocol.Serialize(new ElevatedRequest { Kind = ElevatedRequestKind.Settle, RunId = "r" }));
        Assert.Equal(ElevatedRequestKind.Settle, request.Kind);
    }
}
