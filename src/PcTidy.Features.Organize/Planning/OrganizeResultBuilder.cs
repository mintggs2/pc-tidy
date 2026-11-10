using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Planning;

/// <summary>Converts an <see cref="OrganizePreview"/> into the shell's standard groups (one per destination category).</summary>
public static class OrganizeResultBuilder
{
    public const string SkippedGroupId = "organize.skipped";

    /// <param name="tickMoves">False for Optimize-all presets that shouldn't sort anything by default.</param>
    public static ScanResult Build(OrganizePreview preview, bool tickMoves = true)
    {
        var roots = preview.Roots.Select(r => r.Root).ToList();
        var groups = new List<ScanGroup>();

        var categoryOrder = OrganizeCategories.All.Select(c => c.Id).ToList();
        foreach (var g in preview.Actions.Where(a => a.Kind is PlannedActionKind.Move or PlannedActionKind.MoveFolder)
                     .GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => categoryOrder.IndexOf(g.Key) is var i && i >= 0 ? i : int.MaxValue).ThenBy(g => g.Key))
        {
            groups.Add(new ScanGroup
            {
                Id = "organize.move." + g.Key.ToLowerInvariant(),
                Title = g.Key,
                Description = DescribeMoves(g, roots),
                Badge = Badge.Safe,
                Items = g.Select(a => ToItem(a, roots, tickMoves)).ToList(),
            });
        }

        AddGroup(groups, preview, roots, PlannedActionKind.Rename, "organize.rename", "Renamed", "Renamed in the same folder. Undoable.", Badge.Safe, tickMoves);
        AddGroup(groups, preview, roots, PlannedActionKind.DuplicateQuarantine, "organize.duplicates", "Already there (identical copies)",
            "An identical file is already in the destination. Your call: quarantine the extra copy.", Badge.Check, false);
        AddGroup(groups, preview, roots, PlannedActionKind.Quarantine, "organize.quarantine", "Quarantine by your rules",
            "Your call. Quarantined files can be restored.", Badge.Check, false);

        if (preview.Skipped.Count > 0)
        {
            groups.Add(new ScanGroup
            {
                Id = SkippedGroupId,
                Title = "Stays put",
                Description = "These aren't moved. Each one says why.",
                Badge = Badge.Safe,
                Items = preview.Skipped.Select(s => new ScanItem
                {
                    Title = s.Name,
                    Detail = DisplayPaths.Short(s.Path, roots),
                    Reason = s.Reason,
                    Badge = Badge.Safe,
                    DefaultSelected = false,
                    SizeBytes = s.Size,
                    FileCount = s.IsDirectory ? 0 : 1,
                    DisabledReason = s.Reason,
                }).ToList(),
            });
        }

        return new ScanResult
        {
            FeatureId = OrganizeModule.FeatureId,
            Title = "Organize files",
            Depth = preview.Depth,
            Duration = preview.Duration,
            Groups = groups,
            Notes = preview.Notes,
        };
    }

    /// <summary>"Moves to Downloads\Pictures, Downloads\Pictures\Screenshots. …" (first three folders).</summary>
    public static string DescribeMoves(IEnumerable<PlannedAction> moves, IReadOnlyList<string> roots)
    {
        var targets = moves.Where(a => a.DestinationPath is not null)
            .Select(a => DisplayPaths.Short(Path.GetDirectoryName(a.DestinationPath!)!, roots)).Distinct().ToList();
        var more = targets.Count > 3 ? $" and {targets.Count - 3} more" : "";
        return "Moves to " + string.Join(", ", targets.Take(3)) + more + ". Undoable from History.";
    }

    private static void AddGroup(List<ScanGroup> groups, OrganizePreview preview, IReadOnlyList<string> roots, PlannedActionKind kind,
        string id, string title, string description, Badge badge, bool tick)
    {
        var actions = preview.Actions.Where(a => a.Kind == kind).ToList();
        if (actions.Count == 0) return;
        groups.Add(new ScanGroup { Id = id, Title = title, Description = description, Badge = badge, Items = actions.Select(a => ToItem(a, roots, tick)).ToList() });
    }

    public static ScanItem ToItem(PlannedAction a, IReadOnlyList<string> roots, bool tick = true)
    {
        var from = DisplayPaths.Short(a.SourcePath, roots);
        var to = a.DestinationPath is null ? null : DisplayPaths.Short(a.DestinationPath, roots);
        var details = new Dictionary<string, string> { ["Rule"] = a.RuleName, ["From"] = from };
        if (to is not null) details["To"] = to;
        if (a.DuplicateOf is not null) details["Identical to"] = DisplayPaths.Short(a.DuplicateOf, roots);
        return new ScanItem
        {
            Id = "organize:" + a.SourcePath,
            Title = a.Name,
            Detail = to is null ? from : $"{from} → {to}",
            Reason = a.Reason,
            Badge = a.Badge,
            // Check/Permanent are never ticked by default (brief rule 8).
            DefaultSelected = a.Badge == Badge.Safe && a.DefaultSelected && tick,
            SizeBytes = a.Size,
            FileCount = a.IsDirectory ? 0 : 1,
            LastModifiedUtc = a.LastWriteUtc,
            PreviewPath = a.Kind == PlannedActionKind.Move && OrganizeCategories.Find(OrganizeCategories.Pictures)!.Extensions.Contains(OrganizeCategories.ExtensionOf(a.Name)) ? a.SourcePath : null,
            Details = details,
            Actions = a.Items,
        };
    }
}
