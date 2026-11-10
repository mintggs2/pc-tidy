using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Reads and switches scheduled tasks. Real: Task Scheduler COM. Tests: a fake.</summary>
public interface IScheduledTaskControl
{
    /// <summary>False for fakes, so handler logic can be tested in the sandbox.</summary>
    bool TouchesRealSystem { get; }
    /// <summary>Null when the task doesn't exist.</summary>
    bool? GetEnabled(string taskPath);
    void SetEnabled(string taskPath, bool enabled);
}

/// <summary>Task Scheduler 2.0 through its COM API (<c>Schedule.Service</c>): language-neutral, no schtasks text parsing.</summary>
public sealed class ComScheduledTaskControl : IScheduledTaskControl
{
    private const int NotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    public bool TouchesRealSystem => true;

    public bool? GetEnabled(string taskPath) => WithTask(taskPath, task => (bool?)(bool)task.Enabled, null);

    public void SetEnabled(string taskPath, bool enabled)
    {
        var found = WithTask(taskPath, task => { task.Enabled = enabled; return true; }, false);
        if (!found) throw new InvalidOperationException("The scheduled task isn't on this PC.");
    }

    private static T WithTask<T>(string taskPath, Func<dynamic, T> action, T whenMissing)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic service = Activator.CreateInstance(type)!;
        dynamic? task = null;
        try
        {
            service.Connect();
            var cut = taskPath.LastIndexOf('\\');
            var folderPath = cut <= 0 ? "\\" : taskPath[..cut];
            dynamic folder = service.GetFolder(folderPath);
            try
            {
                task = folder.GetTask(taskPath[(cut + 1)..]);
            }
            finally { Marshal.FinalReleaseComObject(folder); }
            return action(task);
        }
        catch (COMException ex) when (ex.HResult is NotFound or PathNotFound)
        {
            return whenMissing;
        }
        catch (COMException ex) when (ex.HResult == AccessDenied)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            Marshal.FinalReleaseComObject(service);
        }
    }
}

/// <summary>
/// ScheduledTaskSetEnabled: enables or disables (never deletes) a scheduled task. Records the prior state; undo restores it.
/// Windows' own tasks (\Microsoft\...) are never turned off; only the bad-tweak finder may turn one back on.
/// </summary>
public sealed class ScheduledTaskHandler : IActionHandler
{
    internal const string PriorEnabledData = "priorEnabled";

    private static readonly Regex ValidPath = new(@"^(\\[^\\/:*?""<>|\x00-\x1F]+)+$", RegexOptions.CultureInvariant);

    /// <summary>The feature allowed to switch one of Windows' own tasks back ON (same rule as the SafetyGuard).</summary>
    public const string RepairFeature = "tweaks.badtweaks";

    private readonly IScheduledTaskControl _control;

    public ScheduledTaskHandler() : this(new ComScheduledTaskControl()) { }
    public ScheduledTaskHandler(IScheduledTaskControl control) => _control = control;

    public bool CanHandle(PlanItem item) => item.Kind == ActionKind.ScheduledTaskSetEnabled;

    /// <summary>
    /// Windows' own tasks (everything under \Microsoft\) may only be turned back ON, and only by the bad-tweak finder
    /// repairing what an old "optimizer" switched off. They are never turned off.
    /// </summary>
    internal static string? Validate(PlanItem item)
    {
        var path = item.TaskPath;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || !ValidPath.IsMatch(path) || path.Contains("\\..")) return "The task path is not valid.";
        if (item.Enable is null) return "This step doesn't say whether to turn the task on or off.";
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
            && !(item.Enable == true && string.Equals(item.FeatureId, RepairFeature, StringComparison.OrdinalIgnoreCase)))
            return "This is one of Windows' own tasks, so PC Tidy never turns it off.";
        return null;
    }

    public Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (ctx.Mode == ExecutionMode.Simulate) return SimulateAsync(step, ctx, ct);
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.Skipped(problem));
        var path = step.Item.TaskPath!;
        var enable = step.Item.Enable!.Value;
        if (ctx.Locations.IsSandbox && _control.TouchesRealSystem)
            return Task.FromResult(step.SandboxSimulated(Describe(path, enable)));

        try
        {
            var before = _control.GetEnabled(path);
            if (before is null) return Task.FromResult(step.Skipped("This scheduled task isn't on this PC."));
            if (before == enable) return Task.FromResult(step.Skipped(enable ? "Already on. Nothing to change." : "Already off. Nothing to change."));

            _control.SetEnabled(path, enable);
            if (_control.GetEnabled(path) != enable)
            {
                _control.SetEnabled(path, before.Value);
                return Task.FromResult(step.Failed("Windows didn't keep this change. It was put back as it was."));
            }

            var undo = step.UndoData is null ? new Dictionary<string, string>() : new Dictionary<string, string>(step.UndoData);
            undo[PriorEnabledData] = before.Value ? "1" : "0";
            return Task.FromResult(step.Done(enable ? $"Turned on the task '{Name(path)}'." : $"Turned off the task '{Name(path)}'. It isn't deleted.") with { UndoData = undo });
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(step.Skipped("This task needs admin rights to change. Nothing was changed."));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return Task.FromResult(step.Failed("The task couldn't be changed: " + ex.Message));
        }
    }

    public Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Validate(step.Item) is { } problem) return Task.FromResult(step.Skipped(problem));
        return Task.FromResult(step.Simulated(Describe(step.Item.TaskPath!, step.Item.Enable!.Value) + "."));
    }

    public Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (step.Status != StepStatus.Done) return Task.FromResult(step.Skipped("Nothing to undo."));
        if (step.UndoData is not { } u || !u.TryGetValue(PriorEnabledData, out var p) || p is not ("0" or "1"))
            return Task.FromResult(step.UndoFailed("The saved original state is missing, so PC Tidy can't undo this."));
        var path = step.Item.TaskPath ?? "";
        if (!ValidPath.IsMatch(path)) return Task.FromResult(step.UndoFailed("The task path is not valid."));
        var prior = p == "1";
        if (ctx.Locations.IsSandbox && _control.TouchesRealSystem)
            return Task.FromResult(step.SandboxSimulated(Describe(path, prior)));

        try
        {
            if (_control.GetEnabled(path) is null) return Task.FromResult(step.UndoFailed("This scheduled task isn't on this PC any more."));
            _control.SetEnabled(path, prior);
            return Task.FromResult(_control.GetEnabled(path) == prior
                ? step.Undone($"Put the task '{Name(path)}' back {(prior ? "on" : "off")}.")
                : step.UndoFailed("Windows didn't keep the original state."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return Task.FromResult(step.UndoFailed("The task couldn't be put back: " + ex.Message));
        }
    }

    public Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        try
        {
            if (Validate(step.Item) is not null) return Task.FromResult(false);
            return Task.FromResult(_control.GetEnabled(step.Item.TaskPath!) == step.Item.Enable);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static string Name(string path) => path[(path.LastIndexOf('\\') + 1)..];
    private static string Describe(string path, bool enable) => enable ? $"Would turn on the task '{Name(path)}'" : $"Would turn off the task '{Name(path)}'";
}
