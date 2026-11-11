using PcTidy.Core.Model;
using PcTidy.Hosting;

namespace PcTidy.Rescue.Tests;

/// <summary>
/// v3.1: a "turn the device back on" run can be undone from Rescue. Rescue uses the same handlers as the app, so its
/// undo reaches the device handler, which (on a practice PC) only says what it would do: nothing on this PC changes.
/// </summary>
[Collection("rescue")]
public class DeviceUndoTests
{
    private const string Id = @"USB\VID_1234&PID_ABCD\5&2B3C4D5E&0&1";

    [Fact]
    public async Task Rescue_lists_a_device_enable_run_as_undoable_and_undo_reaches_the_device_handler()
    {
        using var bed = new RescueBed("device");
        var item = new PlanItem
        {
            Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "Turn the camera back on", Enable = true,
            NeedsAdmin = true, IsSystemChange = true, Args = new() { ["instanceId"] = Id },
        };
        var plan = new ActionPlan { FeatureId = "pccare", Title = "Turn devices back on", Items = new[] { item } };

        // What the app's journal holds after the admin helper turned the device on (recorded before-state: disabled, 22).
        using (var host = PcTidyHost.CreateSandbox(bed.Root))
        {
            Assert.Single(host.Handlers, h => h.CanHandle(item));
            var run = await host.Journal.CreateAsync(plan, ExecutionMode.Real, CancellationToken.None);
            var done = run.Steps[0] with
            {
                Status = StepStatus.Done, Message = "Turned the device back on.", StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow,
                UndoData = new Dictionary<string, string> { ["priorState"] = "disabled", ["priorProblem"] = "22" },
            };
            await host.Journal.SaveStepAsync(run.RunId, done, CancellationToken.None);
            var saved = await host.Journal.LoadAsync(run.RunId, CancellationToken.None);
            await host.Journal.SaveRunAsync(saved! with { Status = RunStatus.Completed, FinishedUtc = DateTime.UtcNow }, CancellationToken.None);
            (host.Journal as IDisposable)?.Dispose();
        }

        using var rescue = bed.OpenRescue();
        var entry = Assert.Single(await rescue.ListRunsAsync());
        Assert.True(entry.CanUndo);

        var result = await rescue.UndoAsync(entry.RunId);
        var text = result.Message + " " + string.Join(" ", result.Details);
        // On a practice PC the handler reports the exact undo it would run, and changes nothing.
        Assert.Contains("/disable-device", text);
        Assert.Contains(Id, text);
    }
}
