using PcTidy.Core.Model;

namespace PcTidy.Features.Organize.Planning;

/// <summary>
/// Builds the frozen <see cref="ActionPlan"/> from what the user kept ticked. Each planned file carries the
/// CreateDirectory items it needs (so unticking the first file of a folder can't leave the others without one);
/// here they are de-duplicated and put first, parents before children.
/// </summary>
public static class PlanAssembler
{
    public static ActionPlan Build(string title, IEnumerable<PlannedAction> actions, string? parentRunId = null)
        => new()
        {
            FeatureId = OrganizeModule.FeatureId,
            Title = title,
            ParentRunId = parentRunId,
            Items = Order(actions.SelectMany(a => a.Items)),
        };

    public static ActionPlan FromScanItems(string title, IEnumerable<ScanItem> selected, string? parentRunId = null)
        => new()
        {
            FeatureId = OrganizeModule.FeatureId,
            Title = title,
            ParentRunId = parentRunId,
            Items = Order(selected.Where(s => s.DisabledReason is null).SelectMany(s => s.Actions)),
        };

    public static IReadOnlyList<PlanItem> Order(IEnumerable<PlanItem> items)
    {
        var list = items.DistinctBy(i => i.Id).ToList();
        var folders = list.Where(i => i.Kind == ActionKind.CreateDirectory && i.DestinationPath is not null)
            .DistinctBy(i => i.DestinationPath!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.DestinationPath!.Length)
            .ThenBy(i => i.DestinationPath, StringComparer.OrdinalIgnoreCase);
        return folders.Concat(list.Where(i => i.Kind != ActionKind.CreateDirectory)).ToList();
    }
}
