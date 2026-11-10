using System.Text.Json;
using PcTidy.Features.Services.Scanning;
using PcTidy.Platform;

namespace PcTidy.Features.Services.Tests;

/// <summary>
/// The Platform service handler with a fake service controller (exact backup of start type, delayed flag and recovery
/// settings; undo restores all three), and a sandbox end-to-end run through the real engine: scan → plan → guard →
/// run → undo → every fake service exactly as before.
/// </summary>
public class HandlerAndEndToEndTests
{
    private static PlanItem Item(string name, ServiceStart target, string feature = "services") => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = "test", ServiceName = name, ServiceStartType = target,
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    private static StepRecord Step(PlanItem item) => new() { Index = 0, Item = item };

    [Fact]
    public async Task Disabled_is_only_allowed_for_the_Services_section_and_never_for_protected_services()
    {
        using var folder = new TestFolder("handler");
        var world = new FakeServiceWorld()
            .Add(Svc.Own("DiagTrack", 2))
            .Add(Svc.Own("ContosoSync", 2))
            .Add(Svc.Own("WinDefend", 2));
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), world);
        var handler = new ServiceStartTypeHandler(world);
        var ctx = host.HandlerContext();

        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Step(Item("DiagTrack", ServiceStart.Disabled)), ctx, default)).Status);
        Assert.Equal(4, world.States["DiagTrack"].Start);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Step(Item("ContosoSync", ServiceStart.Disabled, "speed")), ctx, default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Step(Item("ContosoSync", ServiceStart.Disabled) with { Reversible = false }), ctx, default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Step(Item("WinDefend", ServiceStart.Disabled)), ctx, default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Step(Item("WinDefend", ServiceStart.Manual)), ctx, default)).Status);
        Assert.Equal(2, world.States["ContosoSync"].Start);
        Assert.Equal(2, world.States["WinDefend"].Start);
    }

    [Fact]
    public async Task The_services_repair_may_switch_a_protected_service_back_to_its_exact_default()
    {
        using var folder = new TestFolder("handler");
        var world = new FakeServiceWorld().Add(Svc.Own("WinDefend", 4));
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), world);
        var handler = new ServiceStartTypeHandler(world);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Step(Item("WinDefend", ServiceStart.Manual, "services.repair")), host.HandlerContext(), default)).Status);
        Assert.Equal(StepStatus.Done, (await handler.ExecuteAsync(Step(Item("WinDefend", ServiceStart.Automatic, "services.repair")), host.HandlerContext(), default)).Status);
        Assert.Equal(2, world.States["WinDefend"].Start);
    }

    [Fact]
    public async Task Undo_restores_start_type_delayed_flag_and_recovery_settings_exactly()
    {
        using var folder = new TestFolder("handler");
        var custom = new ServiceRecoveryConfig(3600, "Restarting soon", null, new[] { new ServiceRecoveryAction(1, 5000) }, true);
        var world = new FakeServiceWorld().Add(Svc.Own("ContosoSync", 2, delayed: true), recovery: custom);
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), world);
        var before = world.Snapshot();
        var handler = new ServiceStartTypeHandler(world);

        var done = await handler.ExecuteAsync(Step(Item("ContosoSync", ServiceStart.Manual)), host.HandlerContext(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal("2", done.UndoData!["priorStart"]);
        Assert.Equal("1", done.UndoData["priorDelayed"]);
        Assert.True(custom.SameAs(ServiceRecoveryConfig.TryParse(done.UndoData["priorRecovery"])));

        // Something else changes the recovery settings meanwhile; undo puts them back too.
        world.SetRecovery("ContosoSync", FakeServiceWorld.DefaultRecovery);
        var writesBefore = world.RecoveryWrites;
        // The step goes to the elevated helper and back as JSON.
        var roundTripped = JsonSerializer.Deserialize<StepRecord>(JsonSerializer.Serialize(done))!;
        var undone = await handler.UndoAsync(roundTripped, host.HandlerContext(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(writesBefore + 1, world.RecoveryWrites);
        Assert.Equal(before, world.Snapshot());
    }

    [Fact]
    public async Task Undo_leaves_unchanged_recovery_settings_alone()
    {
        using var folder = new TestFolder("handler");
        var world = new FakeServiceWorld().Add(Svc.Own("ContosoSync", 2));
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), world);
        var before = world.Snapshot();
        var handler = new ServiceStartTypeHandler(world);
        var done = await handler.ExecuteAsync(Step(Item("ContosoSync", ServiceStart.Manual)), host.HandlerContext(), default);
        var undone = await handler.UndoAsync(done, host.HandlerContext(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(0, world.RecoveryWrites);
        Assert.Equal(before, world.Snapshot());
    }

    [Fact]
    public async Task Real_service_control_is_never_used_in_the_sandbox()
    {
        using var folder = new TestFolder("handler");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), new FakeServiceWorld());
        var real = new ServiceStartTypeHandler(); // the real Service Control Manager
        var result = await real.ExecuteAsync(Step(Item("DiagTrack", ServiceStart.Disabled)), host.HandlerContext(), default);
        Assert.Equal(StepStatus.Simulated, result.Status);
    }

    [Fact]
    public async Task Sandbox_run_through_the_engine_changes_fake_services_and_undo_puts_them_back_exactly()
    {
        using var folder = new TestFolder("e2e");
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), l => FakePc.Build(l, rpcStart: 3));
        var world = host.World;
        // The scan reads the same fake services the handler changes.
        var shared = new ServicesModule(_ => world.Environment());
        var before = world.Snapshot();
        var scan = await shared.Scanner.ScanAsync(host.Context(), null, CancellationToken.None);
        var repairScan = await shared.Repair.ScanAsync(host.Context(), null, CancellationToken.None);
        var diag = scan.AllItems.Single(i => i.Actions.Any(a => a.ServiceName == "DiagTrack"));
        var contoso = scan.AllItems.Single(i => i.Actions.Any(a => a.ServiceName == FakePc.ThirdPartyAuto));
        var rpc = repairScan.AllItems.Single(i => i.Actions.Any(a => a.ServiceName == "RpcSs"));

        var plans = new[]
        {
            PlanBuilder.FromSelection(ServicesIds.Module, "Services", new[] { diag, contoso }),
            PlanBuilder.FromSelection(ServicesIds.Repair, "Bad services repair", new[] { rpc }),
        };
        Assert.All(plans.SelectMany(p => p.Items), i => Assert.True(host.Safety.Check(i).Allowed, i.Description + " → " + host.Safety.Check(i).Reason));

        // Simulation first: nothing changes.
        foreach (var plan in plans)
        {
            var sim = await host.Engine.Executor.ExecuteAsync(plan with { RunId = ActionPlan.NewRunId() }, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
            Assert.Equal(RunStatus.Simulated, sim.Status);
        }
        Assert.Equal(before, world.Snapshot());

        var runs = new List<RunRecord>();
        foreach (var plan in plans)
        {
            var run = await host.Engine.Executor.ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None);
            Assert.True(run.Status == RunStatus.Completed, $"{run.Status}: {string.Join("; ", run.Steps.Select(s => s.Status + " " + s.Message))}");
            Assert.True(plan.RequiresRestorePoint && plan.RequiresAdmin); // the sandbox skips the restore point itself
            runs.Add(run);
        }
        Assert.Equal(4, world.States["DiagTrack"].Start);
        Assert.Equal(3, world.States[FakePc.ThirdPartyAuto].Start);
        Assert.True(world.States[FakePc.ThirdPartyAuto].Delayed); // Windows keeps the flag on a manual service
        Assert.Equal(2, world.States["RpcSs"].Start);

        // A fresh scan shows the new state: the repair is gone, DiagTrack offers its Windows default back.
        var again = await shared.Repair.ScanAsync(host.Context(), null, CancellationToken.None);
        Assert.Empty(again.AllItems);

        // Undo puts the Services section's changes back exactly. The repair's undo would set RpcSs back to Manual, weaker
        // than its Windows default, so it is refused and says so (UNDO-1): PC Tidy never switches protection off again.
        var repairUndo = await host.Engine.Undo.UndoRunAsync(runs[1].RunId, null, CancellationToken.None);
        Assert.Equal(1, repairUndo.Failed);
        Assert.Contains(repairUndo.Messages, m => m.Contains("didn't switch 'RpcSs' off again"));
        var sectionUndo = await host.Engine.Undo.UndoRunAsync(runs[0].RunId, null, CancellationToken.None);
        Assert.True(sectionUndo.Failed == 0 && sectionUndo.Restored == runs[0].DoneCount, string.Join("; ", sectionUndo.Messages));
        var expected = before.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        expected["RpcSs"] = world.Snapshot()["RpcSs"];
        Assert.Equal(2, world.States["RpcSs"].Start);
        Assert.Equal(expected, world.Snapshot());
    }

    /// <summary>
    /// ELV-1 + UNDO-1: undo data comes from a file the user's programs can change. Whatever it says, undo never puts a
    /// protected service back to Disabled (or weaker than its default), never uses a driver start type, and never writes
    /// a recovery action that runs a program.
    /// </summary>
    [Fact]
    public async Task Undo_never_weakens_a_protected_service_or_restores_a_run_a_program_recovery_from_a_tampered_journal()
    {
        using var folder = new TestFolder("handler");
        var world = new FakeServiceWorld().Add(Svc.Own("WinDefend", 4)).Add(Svc.Own("ContosoSync", 2)).Add(Svc.Own("MDCoreSvc", 4));
        using var host = new FakeServicesHost(Path.Combine(folder.Path, "pc"), world);
        var handler = new ServiceStartTypeHandler(world);
        var ctx = host.HandlerContext();

        // A real repair: WinDefend Disabled → Automatic. Its honest undo data says "was Disabled".
        var repaired = await handler.ExecuteAsync(Step(Item("WinDefend", ServiceStart.Automatic, "tweaks.badtweaks")), ctx, default);
        Assert.Equal(StepStatus.Done, repaired.Status);
        var undo = await handler.UndoAsync(repaired, ctx, default);
        Assert.Equal(StepStatus.UndoFailed, undo.Status);
        Assert.Contains("PC Tidy didn't switch 'WinDefend' off again", undo.Message);
        Assert.Equal(2, world.States["WinDefend"].Start);

        // Tampered: Manual for a service whose default is Automatic, a driver start type, and a name only the guard had before.
        foreach (var (service, prior) in new[] { ("WinDefend", "3"), ("WinDefend", "1"), ("MDCoreSvc", "4") })
        {
            world.States[service] = world.States[service] with { Start = 2 };
            var tampered = repaired with { Item = repaired.Item with { ServiceName = service }, UndoData = new Dictionary<string, string> { ["priorStart"] = prior, ["priorDelayed"] = "0" } };
            Assert.Equal(StepStatus.UndoFailed, (await handler.UndoAsync(tampered, ctx, default)).Status);
            Assert.Equal(2, world.States[service].Start);
        }

        // A third-party service: the start type comes back, but a planted "run a program" recovery is never written.
        var done = await handler.ExecuteAsync(Step(Item("ContosoSync", ServiceStart.Manual)), ctx, default);
        var planted = new ServiceRecoveryConfig(0, null, @"C:\Users\tester\x.exe", new[] { new ServiceRecoveryAction(3, 0) }, true);
        var evil = done with { UndoData = new Dictionary<string, string>(done.UndoData!) { ["priorRecovery"] = planted.Serialize() } };
        var writes = world.RecoveryWrites;
        var result = await handler.UndoAsync(evil, ctx, default);
        Assert.Equal(StepStatus.Undone, result.Status);
        Assert.Contains("running a program", result.Message);
        Assert.Equal(2, world.States["ContosoSync"].Start);
        Assert.Equal(writes, world.RecoveryWrites);
        Assert.False(world.States["ContosoSync"].Recovery.RunsCommand);
        // A type-3 action without a command line counts the same, and the real control refuses it before asking Windows.
        var typeOnly = new ServiceRecoveryConfig(0, null, null, new[] { new ServiceRecoveryAction(1, 1000), new ServiceRecoveryAction(3, 0) }, false);
        Assert.True(typeOnly.RunsCommand);
        Assert.Throws<NotSupportedException>(() => new WindowsServiceControl().SetRecovery("PCTidyTestNoSuchService", typeOnly));
    }
}
