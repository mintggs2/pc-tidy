using PcTidy.Features.Measure.Compare;

namespace PcTidy.Features.Measure.Storage;

/// <summary>One recorded condition: was it the same for every run, and what was it (free text).</summary>
public sealed class ConditionEntry
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Ticked: the user says this was the same for every run.</summary>
    public bool SameForAllRuns { get; set; }
    /// <summary>Free text, e.g. "1440p", "Balanced", "capped at 141", "on AC".</summary>
    public string Note { get; set; } = "";
}

/// <summary>The conditions list shown in Step 3 and stored with each comparison.</summary>
public static class Conditions
{
    public static readonly IReadOnlyList<(string Key, string Label, string Hint)> Known = new[]
    {
        ("scene", "Same scene or benchmark", "e.g. built-in benchmark, same save"),
        ("length", "Same run length", "e.g. 90 s each"),
        ("resolution", "Same resolution and settings", "e.g. 1440p, High preset"),
        ("powerplan", "Same power plan", "e.g. Balanced"),
        ("fpscap", "Same FPS cap (or none)", "e.g. uncapped, or 141"),
        ("vrr", "Same VRR / V-sync setting", "e.g. G-Sync on, V-sync off"),
        ("driver", "Same graphics driver version", "e.g. the version shown in the driver's app"),
        ("battery", "Plugged in (not on battery)", "laptops: on AC for every run"),
    };

    public static List<ConditionEntry> NewList()
        => Known.Select(k => new ConditionEntry { Key = k.Key, Label = k.Label }).ToList();

    /// <summary>Plain notes for conditions the user did NOT tick (shown with the result, they don't change the verdict).</summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<ConditionEntry> list)
    {
        var open = list.Where(c => !c.SameForAllRuns).Select(c => c.Label).ToList();
        if (open.Count == 0) return Array.Empty<string>();
        return new[]
        {
            $"Not ticked as the same for every run: {string.Join("; ", open)}. If one of these isn't the change you're testing, the result may come from it.",
        };
    }
}

/// <summary>A saved comparison: stored as JSON under the Measure data folder. Numbers only, never frame data or folder paths.</summary>
public sealed class SavedComparison
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; }
    /// <summary>What was tested, in the user's words (e.g. "Game Mode on").</summary>
    public string Title { get; set; } = "";
    /// <summary>Optional History run (journal RunId) this comparison checks.</summary>
    public string? LinkedRunId { get; set; }
    public List<RunSummary> Before { get; set; } = new();
    public List<RunSummary> After { get; set; } = new();
    public List<RunSummary> BeforeAgain { get; set; } = new();
    public List<ConditionEntry> Conditions { get; set; } = Storage.Conditions.NewList();
    /// <summary>Verdict and headline when saved (the rule may change in later versions; the runs allow recomputing).</summary>
    public Verdict VerdictAtSave { get; set; }
    public string HeadlineAtSave { get; set; } = "";

    public ComparisonResult Recompute() => Comparer.Compare(Before, After, BeforeAgain.Count > 0 ? BeforeAgain : null);
}
