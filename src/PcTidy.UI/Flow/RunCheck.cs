using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>
/// What the Done card's "Check if this helped" hands to the Measure section (as the
/// <see cref="INavigationTarget.OnNavigatedTo"/> parameter): the run to compare before and after.
/// </summary>
/// <param name="RunId">The run's id (<see cref="RunRecord.RunId"/>), as History and the journal know it.</param>
/// <param name="ParentRunId">Set for one section of an Optimize-everything run.</param>
public sealed record RunCheckRequest(string RunId, string FeatureId, string Title, string? ParentRunId = null);

/// <summary>
/// Opens History on one run, already selected with its details showing (the <see cref="INavigationTarget.OnNavigatedTo"/>
/// parameter for "history"). Measure's "Open History" uses it for the run a comparison checks.
/// </summary>
/// <param name="RunId">The run's id, or the ParentRunId of an Optimize-everything run (either finds its row).</param>
public sealed record HistoryOpenRequest(string RunId);

/// <summary>
/// Opens the Measure section for a run. Registered by the shell; null (or <see cref="IsAvailable"/> false) when this
/// build has no Measure section, and then the Done card shows no link.
/// </summary>
public interface IRunCheckLauncher
{
    bool IsAvailable { get; }
    void Open(RunCheckRequest request);
}

/// <summary>When the Done card offers "Check if this helped" (layout spec §4.5, Measure).</summary>
public static class RunCheck
{
    /// <summary>The Measure section's module id.</summary>
    public const string MeasureModuleId = "measure";

    /// <summary>Runs from these sections change how the PC performs, so measuring before and after can tell.</summary>
    public static readonly IReadOnlySet<string> Sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tweaks", "games", "speed", "services" };

    public const string LinkText = "Check if this helped";
    public const string LinkCaption = "Measure your own game before and after, in Measure. Nothing is changed.";

    /// <summary>
    /// The run to measure, or null when the link isn't shown: no Measure section, a simulation, a run that was undone,
    /// nothing actually done, or none of the runs comes from Tweaks, Games, Speed or Services.
    /// </summary>
    public static RunCheckRequest? For(IReadOnlyList<RunRecord> runs, bool measureAvailable)
    {
        if (!measureAvailable || runs.Count == 0) return null;
        var run = runs.FirstOrDefault(r => r.Mode == ExecutionMode.Real
                                            && Sections.Contains(r.FeatureId)
                                            && r.Status is not (RunStatus.Undone or RunStatus.RolledBack or RunStatus.Simulated)
                                            && r.Steps.Any(s => s.Status == StepStatus.Done));
        return run is null ? null : new RunCheckRequest(run.RunId, run.FeatureId, run.Title, run.ParentRunId);
    }
}
