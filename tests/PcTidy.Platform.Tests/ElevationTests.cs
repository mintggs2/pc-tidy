using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>Message format and validation, in-process. Nothing here elevates or uses runas.</summary>
public class ElevationProtocolTests
{
    private static StepRecord RichStep() => new()
    {
        Index = 3,
        Status = StepStatus.Done,
        Message = "done",
        StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UndoData = new() { ["priorStart"] = "2" },
        RegistryPrior = new RegistryPriorState { KeyExisted = true, ValueExisted = true, Kind = RegistryKind.Binary, Data = "0102" },
        BytesAffected = 42,
        Item = new PlanItem
        {
            Kind = ActionKind.RegistrySetValue,
            FeatureId = "tweaks",
            Description = "Turn off tips",
            NeedsAdmin = true,
            IsSystemChange = true,
            Registry = new RegistryValueSpec { Hive = RegistryHive.LocalMachine, KeyPath = @"SOFTWARE\Contoso", ValueName = "Tips", Kind = RegistryKind.DWord, Data = "0", View64 = false },
            Args = new() { ["a"] = "b" },
            RequiresClosedApps = new[] { "contoso" },
            ServiceStartType = ServiceStart.AutomaticDelayed,
        },
    };

    [Fact]
    public void Steps_survive_serialization_exactly()
    {
        var step = RichStep();
        var json = System.Text.Encoding.UTF8.GetString(ElevationProtocol.Serialize(step));
        var back = ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(step));

        Assert.Contains("\"RegistrySetValue\"", json); // enums as names, stable across versions
        Assert.Equal(step.Item.Registry, back.Item.Registry);
        Assert.Equal(step.RegistryPrior, back.RegistryPrior);
        Assert.Equal(step.UndoData, back.UndoData);
        Assert.Equal(step.Item.Args, back.Item.Args);
        Assert.Equal(step.Item.RequiresClosedApps, back.Item.RequiresClosedApps);
        Assert.Equal(step.Item.Id, back.Item.Id);
        Assert.Equal(step.Item.ServiceStartType, back.Item.ServiceStartType);
        Assert.Equal(step.StartedUtc, back.StartedUtc);
        Assert.Equal(42, back.BytesAffected);
    }

    [Fact]
    public async Task Framed_messages_round_trip_over_a_stream()
    {
        using var stream = new MemoryStream();
        var request = new ElevatedRequest { Kind = ElevatedRequestKind.Execute, RunId = "r1", Mode = ExecutionMode.Simulate, Undo = true, Steps = new[] { RichStep() } };
        await ElevationProtocol.WriteAsync(stream, request, default);
        await ElevationProtocol.WriteAsync(stream, new ElevatedRequest { Kind = ElevatedRequestKind.Bye }, default);
        stream.Position = 0;

        var first = await ElevationProtocol.ReadAsync<ElevatedRequest>(stream, default);
        var second = await ElevationProtocol.ReadAsync<ElevatedRequest>(stream, default);
        var end = await ElevationProtocol.ReadAsync<ElevatedRequest>(stream, default);

        Assert.Equal(ElevatedRequestKind.Execute, first!.Kind);
        Assert.Equal(ExecutionMode.Simulate, first.Mode);
        Assert.True(first.Undo);
        Assert.Single(first.Steps!);
        Assert.Equal(ElevatedRequestKind.Bye, second!.Kind);
        Assert.Null(end);
    }

    [Fact]
    public async Task Bad_frames_are_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevationProtocol.ReadAsync<ElevatedRequest>(new MemoryStream(BitConverter.GetBytes(int.MaxValue)), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevationProtocol.ReadAsync<ElevatedRequest>(new MemoryStream(BitConverter.GetBytes(-5)), default));
        var truncated = BitConverter.GetBytes(100).Concat(new byte[10]).ToArray();
        await Assert.ThrowsAsync<EndOfStreamException>(() => ElevationProtocol.ReadAsync<ElevatedRequest>(new MemoryStream(truncated), default));
        var garbage = BitConverter.GetBytes(3).Concat("abc"u8.ToArray()).ToArray();
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => ElevationProtocol.ReadAsync<ElevatedRequest>(new MemoryStream(garbage), default));
    }

    [Fact]
    public void Pipe_names_and_helper_arguments_are_strict()
    {
        var name = ElevationProtocol.NewPipeName();
        Assert.True(ElevationProtocol.IsValidPipeName(name));
        Assert.False(ElevationProtocol.IsValidPipeName("PcTidy.Elevated.xyz"));
        Assert.False(ElevationProtocol.IsValidPipeName(@"..\pipe\other"));

        Assert.True(ElevatedHost.TryParseArgs(new[] { "--elevated", name, "1234" }, out var pipe, out var pid));
        Assert.Equal(name, pipe);
        Assert.Equal(1234, pid);
        Assert.False(ElevatedHost.TryParseArgs(new[] { "--elevated", name, "-1" }, out _, out _));
        Assert.False(ElevatedHost.TryParseArgs(new[] { "--elevated", "evil", "1234" }, out _, out _));
        Assert.False(ElevatedHost.TryParseArgs(new[] { "--elevated", name, "1234", "extra" }, out _, out _));
        Assert.True(ElevatedHost.IsElevatedInvocation(new[] { "--ELEVATED" }));
        Assert.False(ElevatedHost.IsElevatedInvocation(Array.Empty<string>()));
    }
}

public class ElevatedBatchExecutorTests
{
    private static PlanItem AdminItem(ActionKind kind = ActionKind.ServiceSetStartType) => new()
    {
        Kind = kind, FeatureId = "test", Description = "test", NeedsAdmin = true, ServiceName = "ContosoSync", ServiceStartType = ServiceStart.Manual,
    };

    [Fact]
    public async Task Valid_admin_steps_are_safety_checked_and_run_in_order()
    {
        using var sandbox = new TestSandbox();
        var safety = new FakeSafety();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(safety: safety));
        var steps = new[] { Steps.Of(AdminItem(), 0), Steps.Of(AdminItem(), 1) };

        var results = await executor.RunAsync("run-1", steps, ExecutionMode.Real, undo: false, default);

        Assert.Equal(new[] { 0, 1 }, results.Select(r => r.Index));
        Assert.All(results, r => Assert.Equal(StepStatus.Done, r.Status));
        Assert.All(results, r => Assert.NotNull(r.FinishedUtc));
        Assert.Equal(2, safety.Checked.Count);
        Assert.All(handler.Calls, c => Assert.Equal("run-1", c.Ctx.RunId));
    }

    [Fact]
    public async Task A_step_whose_earlier_need_in_the_request_was_refused_is_skipped()
    {
        using var sandbox = new TestSandbox();
        var safety = new FakeSafety { Verdict = i => i.ServiceName == "Blocked" ? SafetyVerdict.Block("Protected by PC Tidy.") : SafetyVerdict.Ok };
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(safety: safety));
        var backup = AdminItem() with { ServiceName = "Blocked" };
        var needsIt = AdminItem() with { DependsOn = new[] { backup.Id } };
        var free = AdminItem();

        var results = await executor.RunAsync("run-dep", new[] { Steps.Of(backup, 0), Steps.Of(needsIt, 1), Steps.Of(free, 2) }, ExecutionMode.Real, undo: false, default);

        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Contains("needs", results[1].Message);
        Assert.Equal(StepStatus.Done, results[2].Status);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_need_that_comes_later_in_the_request_blocks_the_step()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(safety: new FakeSafety()));
        var later = AdminItem();
        var first = AdminItem() with { DependsOn = new[] { later.Id } };

        var results = await executor.RunAsync("run-dep2", new[] { Steps.Of(first, 0), Steps.Of(later, 1) }, ExecutionMode.Real, undo: false, default);

        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal(StepStatus.Done, results[1].Status);
    }

    [Fact]
    public async Task Refusals_never_reach_a_handler()
    {
        using var sandbox = new TestSandbox();
        var safety = new FakeSafety { Verdict = i => i.ServiceName == "Blocked" ? SafetyVerdict.Block("Protected by PC Tidy.") : SafetyVerdict.Ok };
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType, ActionKind.OpenExternal, ActionKind.EmptyRecycleBin);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(safety: safety));
        var steps = new[]
        {
            Steps.Of(AdminItem() with { ServiceName = "Blocked" }),
            Steps.Of(AdminItem() with { NeedsAdmin = false }),
            Steps.Of(AdminItem(ActionKind.OpenExternal)),
            Steps.Of(AdminItem(ActionKind.EmptyRecycleBin)),
            Steps.Of(AdminItem(ActionKind.ScheduledTaskSetEnabled)), // no handler registered
            Steps.Of(AdminItem(), status: StepStatus.Done),          // already ran
        };

        var results = await executor.RunAsync("run-2", steps, ExecutionMode.Real, undo: false, default);

        Assert.All(results, r => Assert.Equal(StepStatus.Skipped, r.Status));
        Assert.Equal("Protected by PC Tidy.", results[0].Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Undo_only_accepts_done_steps_and_simulate_calls_simulate()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context());

        var undo = await executor.RunAsync("r", new[] { Steps.Of(AdminItem(), status: StepStatus.Done), Steps.Of(AdminItem()) }, ExecutionMode.Real, undo: true, default);
        var sim = await executor.RunAsync("r", new[] { Steps.Of(AdminItem()) }, ExecutionMode.Simulate, undo: false, default);

        Assert.Equal(StepStatus.Undone, undo[0].Status);
        Assert.Equal(StepStatus.UndoFailed, undo[1].Status);
        Assert.Equal(StepStatus.Simulated, sim[0].Status);
        Assert.Equal(new[] { "undo", "simulate" }, handler.Calls.Select(c => c.Op));
        Assert.Equal(ExecutionMode.Simulate, handler.Calls[1].Ctx.Mode);
    }

    [Fact]
    public async Task Different_admin_account_only_gets_machine_wide_steps()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.RegistrySetValue, ActionKind.QuarantinePath, ActionKind.ServiceSetStartType, ActionKind.StartupItemSetEnabled);
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context(), sameAccountAsApp: false);
        PlanItem Reg(RegistryHive hive) => Steps.RegSet(@"Software\Contoso", "x", RegistryKind.DWord, "1", hive) with { NeedsAdmin = true };
        PlanItem Startup(string hive) => new() { Kind = ActionKind.StartupItemSetEnabled, FeatureId = "t", Description = "t", NeedsAdmin = true, Enable = false, Args = new() { ["location"] = "Run", ["name"] = "x", ["hive"] = hive } };
        var steps = new[]
        {
            Steps.Of(Reg(RegistryHive.CurrentUser)),
            Steps.Of(new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "t", Description = "t", NeedsAdmin = true, SourcePath = sandbox.Locations.WindowsTemp }),
            Steps.Of(Startup("HKCU")),
            Steps.Of(Reg(RegistryHive.LocalMachine)),
            Steps.Of(AdminItem()),
            Steps.Of(Startup("HKLM")),
        };

        var results = await executor.RunAsync("r", steps, ExecutionMode.Real, undo: false, default);

        Assert.Equal(new[] { StepStatus.Skipped, StepStatus.Skipped, StepStatus.Skipped, StepStatus.Done, StepStatus.Done, StepStatus.Done }, results.Select(r => r.Status));
        Assert.Contains("different account", results[0].Message);
    }

    [Fact]
    public async Task Cancellation_stops_between_steps()
    {
        using var sandbox = new TestSandbox();
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType) { BeforeExecute = (_, _) => { cts.Cancel(); return Task.CompletedTask; } };
        var executor = new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context());

        var results = await executor.RunAsync("r", new[] { Steps.Of(AdminItem(), 0), Steps.Of(AdminItem(), 1) }, ExecutionMode.Real, false, cts.Token);

        Assert.Equal(StepStatus.Done, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Single(handler.Calls);
    }
}

/// <summary>
/// The whole app ↔ helper path over a real named pipe, with the helper running in this test process
/// (a fake launcher instead of runas). No UAC prompt, no elevation, only fake handlers.
/// </summary>
public class ElevationServiceTests
{
    private sealed class InProcessLauncher(IEnumerable<IActionHandler> handlers, Func<HandlerContext> ctx) : IHelperLauncher
    {
        public Task<int>? Helper { get; private set; }
        public int Launches { get; private set; }

        public HelperProcess Launch(string pipeName, int parentPid)
        {
            Launches++;
            var args = new[] { ElevationProtocol.ElevatedSwitch, pipeName, parentPid.ToString() };
            var task = Task.Run(() => ElevatedHost.RunAsync(args, handlers, ctx, null, ElevatedHost.StartupChecks.None));
            Helper = task;
            return new HelperProcess(Environment.ProcessId, () => task.IsCompleted);
        }
    }

    private sealed class DecliningLauncher : IHelperLauncher
    {
        public HelperProcess Launch(string pipeName, int parentPid) => throw new ElevationDeclinedException("You said no to the admin prompt");
    }

    private static PlanItem AdminItem() => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = "test", Description = "test", NeedsAdmin = true, ServiceName = "ContosoSync", ServiceStartType = ServiceStart.Manual,
    };

    [Fact]
    public async Task Steps_and_restore_point_go_through_one_helper_session()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var launcher = new InProcessLauncher(new[] { handler }, () => sandbox.Context());
        var service = new ElevationService(sandbox.Locations, null, launcher, allowLaunchInSandbox: true, TimeSpan.FromSeconds(20));

        var restorePoint = await service.EnsureRestorePointAsync("Before PC Tidy", default);
        var results = await service.ExecuteElevatedAsync("run-9", new[] { Steps.Of(AdminItem(), 0), Steps.Of(AdminItem() with { NeedsAdmin = false }, 1) }, ExecutionMode.Real, false, default);
        var undo = await service.ExecuteElevatedAsync("run-9", new[] { results[0] }, ExecutionMode.Real, true, default);
        await service.DisposeAsync();

        Assert.True(restorePoint.Created);
        Assert.Contains("(sandbox)", restorePoint.Description);
        Assert.Equal(StepStatus.Done, results[0].Status);
        Assert.Equal("v", results[0].UndoData!["k"]);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(StepStatus.Undone, undo[0].Status);
        Assert.Equal(1, launcher.Launches); // one prompt for the whole session
        Assert.Equal(ElevatedHost.ExitOk, await launcher.Helper!.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Declined_prompt_skips_steps_with_a_friendly_reason()
    {
        using var sandbox = new TestSandbox();
        var service = new ElevationService(sandbox.Locations, null, new DecliningLauncher(), allowLaunchInSandbox: true, TimeSpan.FromSeconds(5));

        var results = await service.ExecuteElevatedAsync("r", new[] { Steps.Of(AdminItem()) }, ExecutionMode.Real, false, default);
        var undo = await service.ExecuteElevatedAsync("r", new[] { Steps.Of(AdminItem(), status: StepStatus.Done) }, ExecutionMode.Real, true, default);
        var rp = await service.EnsureRestorePointAsync("x", default);

        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal("You said no to the admin prompt, so the steps that need admin rights were skipped.", results[0].Message);
        Assert.Equal(StepStatus.UndoFailed, undo[0].Status);
        Assert.Equal("You said no to the admin prompt, so no restore point was made.", rp.Problem);
    }

    [Fact]
    public async Task Sandbox_never_launches_and_uses_the_in_process_executor_or_simulates()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType);
        var withExecutor = new ElevationService(sandbox.Locations, new ElevatedBatchExecutor(new[] { handler }, () => sandbox.Context()));
        var without = new ElevationService(sandbox.Locations);

        var ran = await withExecutor.ExecuteElevatedAsync("r", new[] { Steps.Of(AdminItem()) }, ExecutionMode.Real, false, default);
        var simulated = await without.ExecuteElevatedAsync("r", new[] { Steps.Of(AdminItem()) }, ExecutionMode.Real, false, default);
        var restorePoint = await new AdminRestorePointService(without).EnsureAsync("x", default);

        Assert.Equal(StepStatus.Done, ran[0].Status);
        Assert.Equal(StepStatus.Simulated, simulated[0].Status);
        Assert.True(restorePoint.Created);
        Assert.Empty(await without.ExecuteElevatedAsync("r", Array.Empty<StepRecord>(), ExecutionMode.Real, false, default));
    }

    [Fact]
    public async Task Cancel_reaches_the_helper_and_the_answer_is_still_collected()
    {
        using var sandbox = new TestSandbox();
        using var cts = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource();
        var handler = new RecordingHandler(ActionKind.ServiceSetStartType)
        {
            BeforeExecute = async (step, ct) =>
            {
                if (step.Index != 0) return;
                firstStarted.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { }
            },
        };
        var launcher = new InProcessLauncher(new[] { handler }, () => sandbox.Context());
        await using var service = new ElevationService(sandbox.Locations, null, launcher, allowLaunchInSandbox: true, TimeSpan.FromSeconds(20));

        var run = service.ExecuteElevatedAsync("r", new[] { Steps.Of(AdminItem(), 0), Steps.Of(AdminItem(), 1) }, ExecutionMode.Real, false, cts.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var results = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, results.Count);
        Assert.Equal(StepStatus.Done, results[0].Status);    // the fake finished its step
        Assert.Equal(StepStatus.Skipped, results[1].Status); // the next one never started
    }
}
