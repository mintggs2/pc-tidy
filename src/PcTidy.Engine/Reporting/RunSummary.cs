using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Engine;

/// <summary>Plain-English summary and Markdown report of a run (Done card, History, exported reports).</summary>
public static class RunSummary
{
    private static readonly ActionKind[] MoveKinds = { ActionKind.MovePath, ActionKind.RenamePath, ActionKind.MoveFolderWithJunction };

    public static int MovedCount(RunRecord run)
        => run.Steps.Count(s => s.Status == StepStatus.Done && MoveKinds.Contains(s.Item.Kind));

    /// <summary>One or two short sentences, e.g. "Moved 1.2 GB to quarantine. Organized 14 items. 3 skipped."</summary>
    public static string Summarize(RunRecord run)
    {
        if (run.Mode == ExecutionMode.Simulate)
        {
            var wouldFree = run.Steps.Where(s => s.Status == StepStatus.Simulated && s.Item.Kind == ActionKind.QuarantinePath).Sum(s => s.BytesAffected);
            var would = run.Steps.Count(s => s.Status == StepStatus.Simulated);
            var wouldSkip = run.Steps.Count(s => s.Status == StepStatus.Skipped);
            var sb0 = new StringBuilder($"Simulation only, nothing was changed. {would} change(s) would be made");
            if (wouldFree > 0) sb0.Append($", freeing about {Sizes.Format(wouldFree)}");
            sb0.Append('.');
            if (wouldSkip > 0) sb0.Append($" {wouldSkip} would be skipped.");
            return sb0.ToString();
        }

        var parts = new List<string>();
        switch (run.Status)
        {
            case RunStatus.Undone: return "Undone. Everything was put back.";
            case RunStatus.RolledBack: return "Rolled back. Everything was put back as it was.";
            case RunStatus.Interrupted: return $"Interrupted after {run.DoneCount} of {run.Steps.Count} step(s). You can resume it or roll it back.";
            case RunStatus.Running: return $"Running: {run.DoneCount} of {run.Steps.Count} step(s) done.";
        }
        if (run.BytesFreed > 0) parts.Add($"Moved {Sizes.Format(run.BytesFreed)} to quarantine.");
        var moved = MovedCount(run);
        if (moved > 0) parts.Add($"Moved or renamed {moved} item(s).");
        var other = run.DoneCount - moved - run.Steps.Count(s => s.Status == StepStatus.Done && s.Item.Kind == ActionKind.QuarantinePath);
        if (other > 0) parts.Add($"{other} other change(s) made.");
        if (run.DoneCount == 0) parts.Add("Nothing was changed.");
        if (run.SkippedCount > 0) parts.Add($"{run.SkippedCount} skipped.");
        if (run.FailedCount > 0) parts.Add($"{run.FailedCount} couldn't be done.");
        var pending = run.Steps.Count(s => s.Status == StepStatus.Pending);
        if (pending > 0) parts.Add($"{pending} not started (stopped early).");
        if (run.Status == RunStatus.PartiallyUndone) parts.Add("Partly undone.");
        return string.Join(" ", parts);
    }

    /// <summary>Where it stands for undo, in one sentence.</summary>
    public static string UndoHint(RunRecord run, int quarantineDays)
    {
        if (run.Mode == ExecutionMode.Simulate) return "Nothing to undo: nothing was changed.";
        if (run.Status is RunStatus.Undone or RunStatus.RolledBack) return "Already undone.";
        if (run.DoneCount == 0) return "Nothing to undo.";
        var permanent = run.Steps.Count(s => s.Status == StepStatus.Done && !s.Item.Reversible);
        var hint = $"You can undo this from History. Quarantined files are kept for {quarantineDays} days.";
        if (permanent > 0) hint += $" {permanent} change(s) can't be undone.";
        return hint;
    }

    /// <summary>Full Markdown report: totals, skipped and failed items with reasons, and every step.</summary>
    public static string ToMarkdown(RunRecord run, int quarantineDays = 7)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Escape(run.Title)}");
        sb.AppendLine();
        sb.AppendLine($"- **When:** {run.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Mode:** {(run.Mode == ExecutionMode.Simulate ? "Simulation (nothing changed)" : "Real")}");
        sb.AppendLine($"- **Status:** {StatusText(run.Status)}");
        sb.AppendLine($"- **Run id:** `{run.RunId}`");
        if (run.RestorePoint is { } rp)
            sb.AppendLine($"- **Restore point:** {(rp.Created ? "made" : rp.Reused ? "reused a recent one" : "none")}{(rp.Problem is null ? "" : $" ({Escape(rp.Problem)})")}");
        sb.AppendLine();
        sb.AppendLine(Summarize(run));
        sb.AppendLine();
        sb.AppendLine($"_{UndoHint(run, quarantineDays)}_");

        AppendGroup(sb, "Skipped", run.Steps.Where(s => s.Status == StepStatus.Skipped));
        AppendGroup(sb, "Couldn't be done", run.Steps.Where(s => s.Status is StepStatus.Failed or StepStatus.UndoFailed));

        sb.AppendLine();
        sb.AppendLine("## All steps");
        sb.AppendLine();
        sb.AppendLine("| # | Status | What | Size | Note |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var s in run.Steps.OrderBy(s => s.Index))
        {
            var size = s.BytesAffected > 0 ? Sizes.Format(s.BytesAffected) : s.Item.ExpectedSize > 0 ? Sizes.Format(s.Item.ExpectedSize) : "";
            sb.AppendLine($"| {s.Index + 1} | {StepText(s.Status)} | {Cell(s.Item.Description)} | {size} | {Cell(s.Message ?? "")} |");
        }
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string title, IEnumerable<StepRecord> steps)
    {
        var list = steps.ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"## {title} ({list.Count})");
        sb.AppendLine();
        foreach (var g in list.GroupBy(s => s.Message ?? "No reason recorded."))
        {
            sb.AppendLine($"- **{Escape(g.Key)}** ({g.Count()})");
            foreach (var s in g.Take(20)) sb.AppendLine($"  - {Escape(s.Item.Description)}");
            if (g.Count() > 20) sb.AppendLine($"  - …and {g.Count() - 20} more");
        }
    }

    public static string StatusText(RunStatus s) => s switch
    {
        RunStatus.Planned => "Planned",
        RunStatus.Running => "Running",
        RunStatus.Completed => "Done",
        RunStatus.CompletedWithSkips => "Done, some items skipped",
        RunStatus.Failed => "Didn't complete",
        RunStatus.Interrupted => "Interrupted",
        RunStatus.RolledBack => "Rolled back",
        RunStatus.Undone => "Undone",
        RunStatus.PartiallyUndone => "Partly undone",
        RunStatus.Simulated => "Simulated",
        _ => s.ToString(),
    };

    private static string StepText(StepStatus s) => s switch
    {
        StepStatus.Done => "Done",
        StepStatus.Skipped => "Skipped",
        StepStatus.Failed => "Failed",
        StepStatus.Undone => "Undone",
        StepStatus.UndoFailed => "Not undone",
        StepStatus.Simulated => "Would do",
        StepStatus.Started => "Started",
        _ => "Not started",
    };

    private static string Escape(string s) => s.Replace("\r", " ").Replace("\n", " ");
    private static string Cell(string s) => Escape(s).Replace("|", "\\|");
}
