using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>In-memory services. Never touches the real Service Control Manager.</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    public Dictionary<string, ServiceStartConfig> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IgnoreWrites { get; set; }
    public bool TouchesRealSystem => false;
    public ServiceStartConfig? Query(string serviceName) => Services.TryGetValue(serviceName, out var c) ? c : null;
    public void SetStart(string serviceName, int start, bool delayedAutostart)
    {
        if (IgnoreWrites) return;
        Services[serviceName] = Services[serviceName] with { Start = start, DelayedAutostart = delayedAutostart };
    }
}

/// <summary>In-memory scheduled tasks. Never touches the real Task Scheduler.</summary>
internal sealed class FakeTaskControl : IScheduledTaskControl
{
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool TouchesRealSystem => false;
    public bool? GetEnabled(string taskPath) => Tasks.TryGetValue(taskPath, out var e) ? e : null;
    public void SetEnabled(string taskPath, bool enabled) => Tasks[taskPath] = enabled;
}

public class ServiceStartTypeHandlerTests
{
    private static PlanItem Item(string name, ServiceStart target) => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = "test", Description = "test",
        ServiceName = name, ServiceStartType = target, NeedsAdmin = true, IsSystemChange = true,
    };

    [Theory]
    [InlineData(2, false, ServiceStart.Automatic)]
    [InlineData(2, true, ServiceStart.AutomaticDelayed)]
    [InlineData(3, false, ServiceStart.Manual)]
    [InlineData(4, false, ServiceStart.Disabled)]
    public void Raw_mapping_round_trips(int start, bool delayed, ServiceStart s)
    {
        Assert.Equal(s, ServiceStartMapping.FromRaw(start, delayed));
        Assert.Equal((start, delayed), ServiceStartMapping.ToRaw(s));
    }

    [Fact]
    public async Task Delayed_auto_to_manual_records_prior_and_undo_restores_it_after_serialization()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl();
        fake.Services["ContosoSync"] = new ServiceStartConfig(2, true, 0x10);
        var handler = new ServiceStartTypeHandler(fake);

        var done = await handler.ExecuteAsync(Steps.Of(Item("ContosoSync", ServiceStart.Manual)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Done, done.Status);
        Assert.Equal(3, fake.Services["ContosoSync"].Start);
        Assert.Equal("2", done.UndoData![ServiceStartTypeHandler.PriorStartData]);
        Assert.Equal("1", done.UndoData[ServiceStartTypeHandler.PriorDelayedData]);

        // The step travels to the elevated helper and back as JSON: undo must still work.
        var roundTripped = ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done));
        var undone = await handler.UndoAsync(roundTripped, sandbox.Context(), default);

        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.Equal(new ServiceStartConfig(2, true, 0x10), fake.Services["ContosoSync"]);
    }

    [Theory]
    [InlineData("WinDefend", ServiceStart.Manual)]
    [InlineData("wuauserv", ServiceStart.Manual)]
    [InlineData("ContosoSync", ServiceStart.Disabled)]
    [InlineData("ContosoSync", ServiceStart.Boot)]
    [InlineData("bad\\name", ServiceStart.Manual)]
    [InlineData("", ServiceStart.Manual)]
    public async Task Protected_services_bad_names_and_disabled_targets_are_refused(string name, ServiceStart target)
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl();
        fake.Services["ContosoSync"] = new ServiceStartConfig(2, false, 0x10);
        fake.Services["WinDefend"] = new ServiceStartConfig(2, false, 0x10);

        var result = await new ServiceStartTypeHandler(fake).ExecuteAsync(Steps.Of(Item(name, target)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal(2, fake.Services["ContosoSync"].Start);
    }

    [Fact]
    public async Task Drivers_missing_services_and_no_change_are_skipped()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl();
        fake.Services["contosodrv"] = new ServiceStartConfig(2, false, 0x1);
        fake.Services["Already"] = new ServiceStartConfig(3, false, 0x10);
        var handler = new ServiceStartTypeHandler(fake);

        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item("contosodrv", ServiceStart.Manual)), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item("Missing", ServiceStart.Manual)), sandbox.Context(), default)).Status);
        Assert.Equal(StepStatus.Skipped, (await handler.ExecuteAsync(Steps.Of(Item("Already", ServiceStart.Manual)), sandbox.Context(), default)).Status);
        Assert.Equal(2, fake.Services["contosodrv"].Start);
    }

    [Fact]
    public async Task Change_windows_did_not_keep_fails()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl { IgnoreWrites = true };
        fake.Services["Sticky"] = new ServiceStartConfig(2, false, 0x10);

        var result = await new ServiceStartTypeHandler(fake).ExecuteAsync(Steps.Of(Item("Sticky", ServiceStart.Manual)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Failed, result.Status);
    }

    /// <summary>Looks like the real Service Control Manager (and answers like this PC would): records whether it was asked.</summary>
    private sealed class RealLookingControl : IServiceControl
    {
        public bool Asked { get; private set; }
        public bool TouchesRealSystem => true;
        public ServiceStartConfig? Query(string serviceName) { Asked = true; return new ServiceStartConfig(3, false, 0x20); }
        public void SetStart(string serviceName, int start, bool delayedAutostart) => throw new InvalidOperationException("Never changes anything in a test.");
    }

    private static PlanItem Repair(string name, ServiceStart target, string? scanned) => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = ServiceStartTypeHandler.ServicesRepairFeature, Description = "test",
        ServiceName = name, ServiceStartType = target, NeedsAdmin = true, IsSystemChange = true, Reversible = true,
        Args = scanned is null ? null : new Dictionary<string, string> { [ServiceStartTypeHandler.CurrentStartArg] = scanned },
    };

    [Fact]
    public async Task Simulation_on_a_practice_PC_reports_the_scanned_start_type_and_never_asks_the_real_controller()
    {
        // UI3-18: the practice PC's BITS is Disabled; the real PC's is Manual. The simulation must say "now Disabled".
        using var sandbox = new TestSandbox();
        var real = new RealLookingControl();
        var result = await new ServiceStartTypeHandler(real).SimulateAsync(Steps.Of(Repair("BITS", ServiceStart.Manual, "Disabled")), sandbox.Context(), default);

        Assert.False(real.Asked);
        Assert.Equal(StepStatus.Simulated, result.Status);
        Assert.Contains("(now Disabled)", result.Message);
        Assert.DoesNotContain("Now: Manual", result.Message);
    }

    [Fact]
    public async Task Simulation_asks_a_fake_control_and_says_when_nothing_would_change()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeServiceControl();
        fake.Services["ContosoSync"] = new ServiceStartConfig(4, false, 0x10);
        var handler = new ServiceStartTypeHandler(fake);

        var changes = await handler.SimulateAsync(Steps.Of(Item("ContosoSync", ServiceStart.Manual)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Simulated, changes.Status);
        Assert.Contains("(now Disabled)", changes.Message);

        fake.Services["ContosoSync"] = new ServiceStartConfig(3, false, 0x10);
        var same = await handler.SimulateAsync(Steps.Of(Item("ContosoSync", ServiceStart.Manual)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Skipped, same.Status);
        Assert.Contains("Already Manual", same.Message);
    }

    [Fact]
    public async Task Real_service_control_is_never_used_in_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        var result = await new ServiceStartTypeHandler().ExecuteAsync(Steps.Of(Item("ContosoSync", ServiceStart.Manual)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Simulated, result.Status);
    }

    [Fact]
    public async Task Undo_without_saved_prior_fails_cleanly()
    {
        using var sandbox = new TestSandbox();
        var step = Steps.Of(Item("ContosoSync", ServiceStart.Manual), status: StepStatus.Done) with { UndoData = new() { [ServiceStartTypeHandler.PriorStartData] = "9" } };
        var result = await new ServiceStartTypeHandler(new FakeServiceControl()).UndoAsync(step, sandbox.Context(), default);
        Assert.Equal(StepStatus.UndoFailed, result.Status);
    }
}

public class ScheduledTaskHandlerTests
{
    private static PlanItem Item(string path, bool enable) => new()
    {
        Kind = ActionKind.ScheduledTaskSetEnabled, FeatureId = "test", Description = "test", TaskPath = path, Enable = enable,
    };

    [Fact]
    public async Task Disable_then_undo_restores_prior_state()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeTaskControl();
        fake.Tasks[@"\Contoso\Updater Logon"] = true;
        var handler = new ScheduledTaskHandler(fake);

        var done = await handler.ExecuteAsync(Steps.Of(Item(@"\Contoso\Updater Logon", false)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Done, done.Status);
        Assert.False(fake.Tasks[@"\Contoso\Updater Logon"]);
        Assert.True(await handler.WasCompletedAsync(done, sandbox.Context(), default));

        var undone = await handler.UndoAsync(ElevationProtocol.Deserialize<StepRecord>(ElevationProtocol.Serialize(done)), sandbox.Context(), default);
        Assert.Equal(StepStatus.Undone, undone.Status);
        Assert.True(fake.Tasks[@"\Contoso\Updater Logon"]);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start")]
    [InlineData(@"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan")]
    [InlineData(@"Contoso\NoLeadingSlash")]
    [InlineData(@"\Contoso\..\Microsoft")]
    [InlineData(@"\Contoso\Bad|Name")]
    public void Protected_and_malformed_paths_are_refused(string path)
        => Assert.NotNull(ScheduledTaskHandler.Validate(Item(path, false)));

    [Fact]
    public async Task Missing_task_is_skipped_and_real_scheduler_is_never_used_in_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        var missing = await new ScheduledTaskHandler(new FakeTaskControl()).ExecuteAsync(Steps.Of(Item(@"\Contoso\Gone", false)), sandbox.Context(), default);
        var real = await new ScheduledTaskHandler().ExecuteAsync(Steps.Of(Item(@"\Contoso\Gone", false)), sandbox.Context(), default);

        Assert.Equal(StepStatus.Skipped, missing.Status);
        Assert.Equal(StepStatus.Simulated, real.Status);
    }
}
