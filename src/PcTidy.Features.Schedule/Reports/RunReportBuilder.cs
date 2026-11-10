using System.Globalization;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Storage;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Schedule.Reports;

/// <summary>
/// The after-every-run summary (Plan §8 "Schedule and report"): space freed, items moved, startup items changed,
/// settings and tweaks applied, what was skipped and why, the restore point, before/after numbers and how to undo.
/// Built only from <see cref="RunRecord"/> (Core) with the shell's <see cref="RunSummary"/> counting, so the report
/// says exactly what the Done card and History say.
/// </summary>
public static class RunReportBuilder
{
    /// <summary>Longest step list in a report; the rest is summarised (History has every step).</summary>
    public const int MaxStepsListed = 300;

    public static ReportDocument Build(IReadOnlyList<RunRecord> runs, IReadOnlyList<MetricChange>? metrics = null, RunSummaryContext? context = null)
    {
        if (runs.Count == 0) throw new ArgumentException("A report needs at least one run.", nameof(runs));
        var summary = new RunSummary(runs, context);
        var first = runs.OrderBy(r => r.CreatedUtc).First();
        var title = runs.Count == 1 ? first.Title : "Optimize & Organize everything";
        var id = "run-" + (first.ParentRunId ?? first.RunId);
        var blocks = new List<ReportBlock>();

        var when = first.CreatedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        var mode = summary.IsSimulation ? "Simulation: nothing was changed." : summary.IsUndone ? "This run has been undone." : null;
        blocks.Add(new ParagraphBlock($"{summary.Headline}. {when}, took {summary.DurationText}." + (mode is null ? "" : " " + mode)));

        blocks.Add(new HeadingBlock("Summary"));
        var facts = new List<IReadOnlyList<string>>();
        var p = summary.Planned;
        if (summary.BytesFreed > 0) facts.Add(Row("Space freed", Sizes.Format(summary.BytesFreed)));
        if (summary.BytesHeld > 0) facts.Add(Row("Moved to quarantine (freed when emptied)", Sizes.Format(summary.BytesHeld)));
        if (p.Quarantine > 0) facts.Add(Row("Items cleaned", RunSummary.OfPlanned(summary.Quarantined, p.Quarantine)));
        if (p.Moves > 0) facts.Add(Row("Files moved", RunSummary.OfPlanned(summary.Moved, p.Moves)));
        if (p.Startup > 0) facts.Add(Row("Startup items changed", RunSummary.OfPlanned(summary.StartupChanged, p.Startup)));
        if (p.Settings > 0) facts.Add(Row("Settings and tweaks applied", RunSummary.OfPlanned(summary.SettingsChanged, p.Settings)));
        if (p.Permanent > 0) facts.Add(Row("Permanent actions", RunSummary.OfPlanned(summary.PermanentDone, p.Permanent)));
        if (summary.Simulated > 0) facts.Add(Row("Simulated steps", summary.Simulated.ToString("N0", CultureInfo.CurrentCulture)));
        if (summary.Skipped > 0) facts.Add(Row("Skipped", summary.Skipped.ToString("N0", CultureInfo.CurrentCulture)));
        facts.Add(Row("Restore point", summary.RestorePointLine));
        facts.Add(Row("Status", string.Join(", ", runs.Select(r => RunSummary.StatusText(r.Status)).Distinct())));
        blocks.Add(new TableBlock(new[] { "What", "Result" }, facts));
        if (summary.SpaceNote is { } note) blocks.Add(new ParagraphBlock(note));

        if (metrics is { Count: > 0 })
        {
            blocks.Add(new HeadingBlock("Before and after"));
            blocks.Add(new BulletsBlock(metrics.Select(m => m.Text).ToList()));
        }

        if (summary.SkipReasons.Count > 0)
        {
            blocks.Add(new HeadingBlock("Skipped, and why"));
            blocks.Add(new BulletsBlock(summary.SkipReasons.Select(r => $"{r.Reason} ({r.Count})").ToList()));
        }

        var steps = runs.SelectMany(r => r.Steps).Where(s => PlanFacts.Categorize(s.Item) != PlanCategory.Housekeeping).ToList();
        if (steps.Count > 0)
        {
            blocks.Add(new HeadingBlock("Every step"));
            var rows = steps.Take(MaxStepsListed)
                .Select(s => (IReadOnlyList<string>)new[] { StepStatusText(s.Status), s.Item.Description, s.BytesAffected > 0 ? Sizes.Format(s.BytesAffected) : "" })
                .ToList();
            blocks.Add(new TableBlock(new[] { "Result", "Step", "Size" }, rows));
            if (steps.Count > MaxStepsListed)
                blocks.Add(new ParagraphBlock($"…and {steps.Count - MaxStepsListed:N0} more steps. History in PC Tidy lists them all."));
        }

        blocks.Add(new HeadingBlock("Undo"));
        blocks.Add(new ParagraphBlock(summary.AnyReversible && !summary.IsUndone && !summary.IsSimulation
            ? $"Open PC Tidy → History and pick this run to undo it (run {first.ParentRunId ?? first.RunId}). Quarantined files can be put back until quarantine is emptied."
            : "Nothing in this run needs undoing."));

        return new ReportDocument
        {
            Id = id, Kind = ReportKind.Run, TimeUtc = first.CreatedUtc, Slug = title,
            Title = $"PC Tidy: {title}", Blocks = blocks,
        };
    }

    private static IReadOnlyList<string> Row(string a, string b) => new[] { a, b };

    internal static string StepStatusText(StepStatus s) => s switch
    {
        StepStatus.Done => "Done",
        StepStatus.Skipped => "Skipped",
        StepStatus.Failed => "Failed",
        StepStatus.Undone => "Undone",
        StepStatus.UndoFailed => "Undo failed",
        StepStatus.Simulated => "Simulated",
        StepStatus.Started => "Interrupted",
        _ => "Not run",
    };
}
