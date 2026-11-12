namespace PcTidy.Core.Model;

/// <summary>One thing a scan found, shown as a row with a checkbox.</summary>
public sealed record ScanItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Title { get; init; }
    /// <summary>Second line, usually the path.</summary>
    public string? Detail { get; init; }
    /// <summary>Plain-English "why it's safe / what happens" line. Required.</summary>
    public required string Reason { get; init; }
    public Badge Badge { get; init; } = Badge.Check;
    /// <summary>Ticked by default? Must be false for Check and Permanent.</summary>
    public bool DefaultSelected { get; init; }
    public long SizeBytes { get; init; }
    public long SizeOnDiskBytes { get; init; }
    public int FileCount { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool NeedsRestart { get; init; }
    public IReadOnlyList<string>? RequiresClosedApps { get; init; }
    public string? OwnerApp { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    /// <summary>Optional thumbnail/preview image path (clips, photos).</summary>
    public string? PreviewPath { get; init; }
    /// <summary>Current state, for settings/tweaks ("On", "Off", "27.4 GB").</summary>
    public string? CurrentState { get; init; }
    /// <summary>Extra key/value details for the details panel.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
    /// <summary>What will happen if this item is ticked and confirmed. Empty = information only.</summary>
    public IReadOnlyList<PlanItem> Actions { get; init; } = Array.Empty<PlanItem>();
    /// <summary>Shown greyed out with this explanation; cannot be ticked (placebo tweaks, protected things).</summary>
    public string? DisabledReason { get; init; }
    /// <summary>
    /// Alternatives in one group share this key (e.g. hibernation "Reduced" or "off"): the preview lets only one of them
    /// be ticked, so a plan never holds two changes that undo each other. Null = an ordinary row.
    /// </summary>
    public string? ChoiceOf { get; init; }
}

public sealed record ScanGroup
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public Badge Badge { get; init; } = Badge.Safe;
    public required IReadOnlyList<ScanItem> Items { get; init; }
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
}

public sealed record ScanResult
{
    public required string FeatureId { get; init; }
    public required string Title { get; init; }
    public ScanDepth Depth { get; init; }
    public DateTime FinishedUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; }
    public required IReadOnlyList<ScanGroup> Groups { get; init; }
    /// <summary>Plain-English notes (e.g. "Chrome is open: its cache was skipped").</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IEnumerable<ScanItem> AllItems => Groups.SelectMany(g => g.Items);
}

public sealed record ScanProgress(string Message, double? Fraction = null, long ItemsSeen = 0, long BytesSeen = 0);

/// <summary>Everything a scanner needs. Scanners are READ-ONLY.</summary>
public sealed class ScanContext
{
    public required IKnownLocations Locations { get; init; }
    public required ISafetyGuard Safety { get; init; }
    public required IInUseChecker InUse { get; init; }
    public required AppSettings Settings { get; init; }
    public required IFileWalker Walker { get; init; }
    public ScanDepth Depth { get; init; } = ScanDepth.Quick;
    /// <summary>Optional user-chosen roots (folders/drives) for features that take them.</summary>
    public IReadOnlyList<string>? Roots { get; init; }
    /// <summary>Optimize-all preset name if run from the big button ("Safe","Recommended","Gamer","Privacy").</summary>
    public string? Preset { get; init; }
    /// <summary>Running process names (lower-case, no .exe) at scan start; helper for "close app first".</summary>
    public IReadOnlySet<string> RunningProcesses { get; init; } = new HashSet<string>();
    /// <summary>Facts about this PC (GPU vendor, laptop, admin, drives). Null in older tests: treat as unknown.</summary>
    public ISystemInfo? System { get; init; }
    /// <summary>Per-feature extra options (e.g. organize rules) as typed objects.</summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } = new Dictionary<string, object>();
}

public static class PlanBuilder
{
    /// <summary>Builds the frozen plan from the ticked items.</summary>
    public static ActionPlan FromSelection(string featureId, string title, IEnumerable<ScanItem> selected, string? parentRunId = null)
        => new()
        {
            FeatureId = featureId,
            Title = title,
            ParentRunId = parentRunId,
            Items = Normalize(OneOfEachChoice(selected.Where(s => s.DisabledReason is null)).SelectMany(s => s.Actions)),
        };

    /// <summary>Alternatives (<see cref="ScanItem.ChoiceOf"/>): only the first ticked one of each goes into the plan.</summary>
    private static IEnumerable<ScanItem> OneOfEachChoice(IEnumerable<ScanItem> items)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item.ChoiceOf is null || taken.Add(item.ChoiceOf)) yield return item;
    }

    /// <summary>
    /// Removes duplicate CreateDirectory items (same destination) and runs them first, shallowest first,
    /// so parents exist before children and before anything is moved into them. Other items keep their order.
    /// </summary>
    public static IReadOnlyList<PlanItem> Normalize(IEnumerable<PlanItem> items)
    {
        var list = items.ToList();
        var groups = list.Where(i => i.Kind == ActionKind.CreateDirectory && i.DestinationPath is not null)
            .GroupBy(i => PathUtil.Normalize(i.DestinationPath!), StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A dropped duplicate's id now means the kept one (so DependsOn still points at a step in the plan).
        var remap = groups.SelectMany(g => g.Skip(1).Select(d => (d.Id, Kept: g.First().Id)))
            .GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().Kept, StringComparer.Ordinal);
        var dirs = groups.Select(g => g.First()).OrderBy(i => i.DestinationPath!.Count(c => c == '\\')).ToList();
        var rest = list.Where(i => !(i.Kind == ActionKind.CreateDirectory && i.DestinationPath is not null));
        return dirs.Concat(rest)
            .Select(i => remap.Count > 0 && i.DependsOn is { Count: > 0 } deps
                ? i with { DependsOn = deps.Select(d => remap.TryGetValue(d, out var k) ? k : d).Distinct(StringComparer.Ordinal).ToList() }
                : i)
            .ToList();
    }
}
