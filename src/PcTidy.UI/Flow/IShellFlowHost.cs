using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>What the user ended up doing in the confirm step.</summary>
public enum FlowOutcomeKind { Cancelled, Ran, Simulated }

public sealed record FlowOutcome(FlowOutcomeKind Kind, IReadOnlyList<RunRecord> Records)
{
    public static readonly FlowOutcome Cancelled = new(FlowOutcomeKind.Cancelled, Array.Empty<RunRecord>());

    /// <summary>Before/after lines measured around a real run ("Free space on C: 41.2 GB → 47.5 GB"), for the Done card.</summary>
    public IReadOnlyList<string> BeforeAfter { get; init; } = Array.Empty<string>();
}

/// <summary>Options for a run started from a flow page.</summary>
public sealed record FlowOptions
{
    /// <summary>Title of the dialog / run ("Clean", "Optimize &amp; Organize everything").</summary>
    public string Title { get; init; } = "Ready?";
    /// <summary>False when the calling page shows the Done card / simulation report itself (standard flow pages).</summary>
    public bool ShowResult { get; init; } = true;
    /// <summary>Plan item ids that need a restart to take effect (from <see cref="ScanItem.NeedsRestart"/>).</summary>
    public IReadOnlySet<string> RestartItemIds { get; init; } = new HashSet<string>();
    /// <summary>Set for "Optimize everything": every section plan shares it and Undo undoes them all.</summary>
    public string? ParentRunId { get; init; }
    /// <summary>
    /// Confirm button text. Leave null (recommended): the dialog names the action from what will run
    /// ("Clean 6.3 GB", "Move 214 files", "Apply 9 changes").
    /// </summary>
    public string? PrimaryVerb { get; init; }
    /// <summary>
    /// Groups the preview offered and whether each was ticked (<see cref="SelectionModel.Decisions"/>). After a real
    /// run the shell passes them to <see cref="IFlowLearning"/>; simulations and cancelled confirms record nothing.
    /// </summary>
    public IReadOnlyList<GroupDecision> OfferedGroups { get; init; } = Array.Empty<GroupDecision>();
}

/// <summary>
/// The shell's richer flow host (implemented by PcTidy.App alongside <see cref="IFlowHost"/>).
/// Standard flow pages use it so they can show the Done card inline and run several section plans
/// behind one confirm. Custom feature pages can keep using plain <see cref="IFlowHost"/>.
/// </summary>
public interface IShellFlowHost : IFlowHost
{
    /// <summary>One confirm for all plans → runs them in the given order → (optionally) Done card.</summary>
    Task<FlowOutcome> ReviewAndRunAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct);

    /// <summary>"What would happen" for all plans, changing nothing.</summary>
    Task<IReadOnlyList<RunRecord>> SimulateAsync(IReadOnlyList<ActionPlan> plans, FlowOptions options, CancellationToken ct);

    /// <summary>Asks, then undoes the given runs (newest first). Returns true when everything was put back.</summary>
    Task<bool> UndoRunsAsync(IReadOnlyList<RunRecord> runs, CancellationToken ct);

    /// <summary>Navigates the shell: "home", "history", "quarantine", "settings", "optimize" or a module id.</summary>
    void NavigateTo(string pageKey);

    /// <summary>
    /// Navigates and hands the page a parameter (an <see cref="INavigationTarget"/> gets it), for example a
    /// <see cref="HistoryOpenRequest"/> for "history". Hosts that can't pass one just navigate.
    /// </summary>
    void NavigateTo(string pageKey, object? parameter) => NavigateTo(pageKey);

    /// <summary>Days quarantined files stay restorable (from settings).</summary>
    int QuarantineDays { get; }
}
