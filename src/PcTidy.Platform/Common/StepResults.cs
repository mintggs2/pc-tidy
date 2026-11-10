using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Small helpers so every handler finishes a step the same way.</summary>
internal static class StepResults
{
    public static StepRecord Finish(this StepRecord step, StepStatus status, string message)
        => step with { Status = status, Message = message, FinishedUtc = DateTime.UtcNow };

    public static StepRecord Done(this StepRecord step, string message) => step.Finish(StepStatus.Done, message);
    public static StepRecord Skipped(this StepRecord step, string message) => step.Finish(StepStatus.Skipped, message);
    public static StepRecord Failed(this StepRecord step, string message) => step.Finish(StepStatus.Failed, message);
    public static StepRecord Simulated(this StepRecord step, string message) => step.Finish(StepStatus.Simulated, message);
    public static StepRecord Undone(this StepRecord step, string message) => step.Finish(StepStatus.Undone, message);
    public static StepRecord UndoFailed(this StepRecord step, string message) => step.Finish(StepStatus.UndoFailed, message);

    /// <summary>For steps that Windows can't reverse (emptying the bin, DISM...): undo explains and changes nothing.</summary>
    public static StepRecord CannotUndo(this StepRecord step)
        => step.Finish(StepStatus.Skipped, "This can't be undone. Nothing was changed.");

    public static StepRecord SandboxSimulated(this StepRecord step, string whatWouldHappen)
        => step.Simulated($"{whatWouldHappen} ({SandboxPolicy.NothingChanged})");

    public static bool IsElevated(this HandlerContext ctx) => ctx.IsElevated || ProcessElevation.IsElevated;

    public static string Arg(this PlanItem item, string key)
        => item.Args is { } a && a.TryGetValue(key, out var v) ? v : "";
}

internal static class StepUndoData
{
    /// <summary>A copy of the step's undo data with these entries added (or replaced).</summary>
    public static StepRecord WithUndo(this StepRecord step, params (string Key, string Value)[] entries)
    {
        var undo = step.UndoData is null ? new Dictionary<string, string>() : new Dictionary<string, string>(step.UndoData);
        foreach (var (k, v) in entries) undo[k] = v;
        return step with { UndoData = undo };
    }

    public static string? Undo(this StepRecord step, string key)
        => step.UndoData is { } u && u.TryGetValue(key, out var v) ? v : null;
}
