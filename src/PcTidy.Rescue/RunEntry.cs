using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine;

namespace PcTidy.Rescue;

/// <summary>One run as Rescue shows it: the journal record plus what is still in quarantine for it.</summary>
public sealed class RunEntry
{
    public RunEntry(RunRecord run, int quarantineLeft, long quarantineBytes)
    {
        Run = run;
        QuarantineLeft = quarantineLeft;
        QuarantineBytes = quarantineBytes;
    }

    public RunRecord Run { get; }
    /// <summary>Quarantined items of this run that are neither restored nor deleted for good.</summary>
    public int QuarantineLeft { get; }
    public long QuarantineBytes { get; }

    public string RunId => Run.RunId;
    public string Title => Run.Title;
    public DateTime CreatedLocal => Run.CreatedUtc.ToLocalTime();
    public string When => CreatedLocal.ToString("g", CultureInfo.CurrentCulture);
    public int ItemCount => Run.Steps.Count;
    public bool IsReal => Run.Mode == ExecutionMode.Real;
    public bool IsInterrupted => IsReal && Run.Status == RunStatus.Interrupted;

    public string StatusText => IsReal ? RunSummary.StatusText(Run.Status) : "Simulation (nothing changed)";

    public string Summary
    {
        get
        {
            try { return RunSummary.Summarize(Run); }
            catch { return $"{Run.DoneCount} done, {Run.SkippedCount} skipped, {Run.FailedCount} failed."; }
        }
    }

    public string QuarantineText => QuarantineLeft == 0
        ? "Nothing from this run is in quarantine."
        : $"{QuarantineLeft} item(s) from this run are still in quarantine ({Sizes.Format(QuarantineBytes)}).";

    /// <summary>Steps that undo would reverse: finished (or a failed earlier undo) and reversible.</summary>
    public int UndoableSteps => Run.Steps.Count(s => s.Status is StepStatus.Done or StepStatus.UndoFailed && s.Item.Reversible);

    /// <summary>Finished runs with something to put back. Interrupted runs use Resume / Roll back instead.</summary>
    public bool CanUndo => IsReal && Run.Status is not (RunStatus.Interrupted or RunStatus.Running or RunStatus.Undone or RunStatus.RolledBack)
                           && UndoableSteps > 0;

    public bool CanResume => IsInterrupted;
    public bool CanRollback => IsInterrupted;
    public bool CanRestoreQuarantine => QuarantineLeft > 0;

    public IReadOnlyList<StepLine> Steps => Run.Steps.Select(s => new StepLine(s.Index + 1, s.Item.Description, StepText(s), s.Message ?? "")).ToList();

    private static string StepText(StepRecord s) => s.Status switch
    {
        StepStatus.Pending => "Not started",
        StepStatus.Started => "Was in progress",
        StepStatus.Done => s.Item.Reversible ? "Done" : "Done (permanent)",
        StepStatus.Skipped => "Skipped",
        StepStatus.Failed => "Failed",
        StepStatus.Undone => "Undone",
        StepStatus.UndoFailed => "Undo failed",
        StepStatus.Simulated => "Simulated",
        _ => s.Status.ToString(),
    };

    public override string ToString() => $"{RunId}  {When}  {StatusText}  {Title}";
}

/// <summary>One row of the step list in the details pane.</summary>
public sealed record StepLine(int Number, string Description, string Status, string Message);

/// <summary>Outcome of one Rescue action, in plain English.</summary>
public sealed record RescueResult(bool Ok, string Message, IReadOnlyList<string> Details)
{
    public static RescueResult Fail(string message) => new(false, message, Array.Empty<string>());
}
