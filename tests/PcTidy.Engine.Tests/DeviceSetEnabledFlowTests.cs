using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Platform;
using PcTidy.Safety.Rules;
using Xunit;

namespace PcTidy.Engine.Tests;

/// <summary>
/// DeviceSetEnabled through the whole chain: plan → executor → admin helper (its own SafetyGuard and ledger) → History
/// undo. The device is a fake; nothing on this PC changes.
/// </summary>
public class DeviceSetEnabledFlowTests
{
    private const string Id = @"USB\VID_1234&PID_ABCD&MI_00\6&1A2B3C4D&0&0000";

    private sealed class Devices : IDeviceControl
    {
        public bool TouchesRealSystem => false;
        public int WindowsBuild => 22631;
        public DeviceFacts Now { get; set; } = new(true, true, DeviceFacts.ProblemDisabled, "Camera", null);
        public List<string> Ran { get; } = new();
        public DeviceFacts? Read(string instanceId) => instanceId == Id ? Now : null;

        public Task<ProcessResult> RunPnputilAsync(IReadOnlyList<string> arguments, string? logPath, CancellationToken ct)
        {
            Ran.Add(string.Join(' ', arguments));
            Now = arguments[0] == "/enable-device" ? Now with { HasProblem = false, ProblemCode = 0 } : Now with { HasProblem = true, ProblemCode = 22 };
            return Task.FromResult(new ProcessResult(0, false, ""));
        }
    }

    private sealed class Classes(string cls) : IDeviceClassSource
    {
        public DeviceClassInfo Find(string instanceId) => new(DeviceClassState.Found, cls);
    }

    private sealed class UnusedQuarantine : IQuarantineStore
    {
        public Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuarantineEntry>>(Array.Empty<QuarantineEntry>());
        public Task PurgeRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<long> TotalBytesAsync(CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class InProcessHelper(ElevatedBatchExecutor helper) : IElevationService
    {
        public bool IsElevated => false;
        public Task<IReadOnlyList<StepRecord>> ExecuteElevatedAsync(string runId, IReadOnlyList<StepRecord> steps, ExecutionMode mode, bool undo, CancellationToken ct)
            => helper.RunAsync(runId, steps, mode, undo, ct);
    }

    private static PlanItem Item(bool enable = true) => new()
    {
        Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "Turn the camera back on", Enable = enable,
        NeedsAdmin = true, IsSystemChange = true, Args = new() { ["instanceId"] = Id },
    };

    private static (EngineServices Engine, Devices Devices, FakeRestorePoints Restore) Build(TestSandbox sb, string cls = "Camera")
    {
        var devices = new Devices();
        var adminRoot = Path.Combine(sb.Root, "ProgramData-Admin");
        Directory.CreateDirectory(adminRoot);
        var realLike = new NonSandboxLocations(sb.Locations);
        var guard = new PcTidy.Safety.SafetyGuard(sb.Locations, sb.Settings, deviceClasses: new Classes(cls));
        var helper = new ElevatedBatchExecutor(new IActionHandler[] { new DeviceSetEnabledHandler(devices) }, () => new HandlerContext
        {
            RunId = "", Locations = realLike, Safety = guard, InUse = sb.InUse, Quarantine = new UnusedQuarantine(),
            Settings = sb.Settings, Mode = ExecutionMode.Real, IsElevated = true,
        }, policy: new ElevatedHelperPolicy { Ledger = new HelperLedger(adminRoot, () => { }) });
        var restore = new FakeRestorePoints();
        var engine = sb.Engine(elevation: new InProcessHelper(helper), restore: restore, locations: realLike,
            extraHandlers: new IActionHandler[] { new DeviceSetEnabledHandler(devices) });
        return (engine, devices, restore);
    }

    private static ActionPlan Plan(params PlanItem[] items) => new() { FeatureId = "pccare", Title = "Turn devices back on", Items = items };

    [Fact]
    public async Task Enable_runs_in_the_admin_helper_after_a_restore_point_and_history_undo_turns_it_off_again()
    {
        using var sb = new TestSandbox();
        var (engine, devices, restore) = Build(sb);

        var run = await engine.Executor.ExecuteAsync(Plan(Item()), new ExecuteOptions { Mode = ExecutionMode.Real }, null, default);
        Assert.True(run.Steps[0].Status == StepStatus.Done, run.Steps[0].Status + ": " + run.Steps[0].Message);
        Assert.Equal(1, restore.Calls);
        Assert.False(devices.Now.Disabled);
        Assert.Equal(new[] { "/enable-device " + Id }, devices.Ran);

        var undo = await engine.Undo.UndoRunAsync(run.RunId, null, default);
        Assert.True(undo.Complete, string.Join("; ", undo.Messages));
        Assert.True(devices.Now.Disabled);
        Assert.Equal("/disable-device " + Id, devices.Ran.Last());
    }

    [Fact]
    public async Task A_plan_that_asks_to_turn_a_device_off_is_refused_by_the_helpers_own_guard()
    {
        using var sb = new TestSandbox();
        var (engine, devices, _) = Build(sb);
        devices.Now = devices.Now with { HasProblem = false, ProblemCode = 0 };

        var run = await engine.Executor.ExecuteAsync(Plan(Item(enable: false)), new ExecuteOptions { Mode = ExecutionMode.Real }, null, default);
        Assert.NotEqual(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(devices.Ran);
        Assert.False(devices.Now.Disabled);
    }

    [Fact]
    public async Task A_boot_critical_device_is_refused_by_the_helpers_own_guard()
    {
        using var sb = new TestSandbox();
        var (engine, devices, _) = Build(sb, cls: "SCSIAdapter");

        var run = await engine.Executor.ExecuteAsync(Plan(Item()), new ExecuteOptions { Mode = ExecutionMode.Real }, null, default);
        Assert.NotEqual(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(devices.Ran);
    }

    [Fact]
    public void The_guard_and_the_handler_protect_the_same_device_classes()
    {
        Assert.Equal(ProtectedDeviceClasses.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            DeviceSetEnabledHandler.ProtectedClasses.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.True(ProtectedDeviceClasses.Guids.ToHashSet().SetEquals(DeviceSetEnabledHandler.ProtectedClassGuids));
        Assert.Contains("SecurityAccelerator", ProtectedDeviceClasses.Names);
    }

    [Fact]
    public async Task A_security_accelerator_is_refused_by_the_helpers_own_guard()
    {
        using var sb = new TestSandbox();
        var (engine, devices, _) = Build(sb, cls: "SecurityAccelerator");
        var run = await engine.Executor.ExecuteAsync(Plan(Item()), new ExecuteOptions { Mode = ExecutionMode.Real }, null, default);
        Assert.NotEqual(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(devices.Ran);
    }
}
