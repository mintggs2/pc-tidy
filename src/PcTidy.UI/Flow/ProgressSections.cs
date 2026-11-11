using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>Where one section of a run is (layout spec §4.3: the progress overlay's per-section states).</summary>
public enum SectionRunState { Waiting, Running, Done, Skipped, NeedsRestart }

public sealed record ProgressSection(string Title, SectionRunState State);

/// <summary>
/// The progress overlay's list of sections: every section starts Waiting, becomes Running when its plan starts, and
/// ends Done, Skipped (nothing was done: stopped before it, or every step skipped) or Needs restart (something done
/// only takes effect after a restart). The overlay also says <see cref="SavedLine"/>.
/// </summary>
public sealed class ProgressSections
{
    /// <summary>Under the list during a real run: each finished step is journaled at once.</summary>
    public const string SavedLine = "Everything done so far is already saved and can be undone.";

    private readonly List<ProgressSection> _items;

    public ProgressSections(IEnumerable<string> titles) => _items = titles.Select(t => new ProgressSection(t, SectionRunState.Waiting)).ToList();

    public IReadOnlyList<ProgressSection> Items => _items;

    /// <summary>Raised after every change (the overlay redraws).</summary>
    public event Action? Changed;

    public static string Label(SectionRunState s) => s switch
    {
        SectionRunState.Waiting => "Waiting",
        SectionRunState.Running => "Running",
        SectionRunState.Done => "Done",
        SectionRunState.Skipped => "Skipped",
        _ => "Needs restart",
    };

    /// <summary>Section <paramref name="index"/> starts. Only a waiting section can start.</summary>
    public void Start(int index)
    {
        if (!InRange(index) || _items[index].State != SectionRunState.Waiting) return;
        Set(index, SectionRunState.Running);
    }

    /// <summary>
    /// Section <paramref name="index"/> ended with <paramref name="record"/> (null = it didn't run).
    /// <paramref name="needsRestart"/>: a step that was done needs a restart to take effect.
    /// </summary>
    public void Finish(int index, RunRecord? record, bool needsRestart)
    {
        if (!InRange(index) || _items[index].State is not (SectionRunState.Waiting or SectionRunState.Running)) return;
        Set(index, StateFor(record, needsRestart));
    }

    /// <summary>The run stopped (Stop after this step, or an error): sections that never started are Skipped.</summary>
    public void StopRemaining()
    {
        bool any = false;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].State != SectionRunState.Waiting) continue;
            _items[i] = _items[i] with { State = SectionRunState.Skipped };
            any = true;
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>A finished section's state from its record.</summary>
    public static SectionRunState StateFor(RunRecord? record, bool needsRestart)
    {
        if (record is null || !record.Steps.Any(s => s.Status is StepStatus.Done or StepStatus.Simulated)) return SectionRunState.Skipped;
        return needsRestart ? SectionRunState.NeedsRestart : SectionRunState.Done;
    }

    /// <summary>
    /// Whether something done in <paramref name="record"/> needs a restart: its plan item was marked so in the preview
    /// (<paramref name="restartItemIds"/>) or carries <c>needsRestart=true</c>.
    /// </summary>
    public static bool NeedsRestart(RunRecord record, IReadOnlySet<string>? restartItemIds)
        => record.Steps.Any(s => s.Status == StepStatus.Done
                                 && ((restartItemIds?.Contains(s.Item.Id) ?? false)
                                     || (s.Item.Args is { } a && a.TryGetValue("needsRestart", out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))));

    private bool InRange(int index) => index >= 0 && index < _items.Count;

    private void Set(int index, SectionRunState state)
    {
        _items[index] = _items[index] with { State = state };
        Changed?.Invoke();
    }
}
