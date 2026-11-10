using PcTidy.Core.Model;

namespace PcTidy.Features.PcCare.Planning;

/// <summary>The plan for the picked rows, or a plain reason it can't be built yet.</summary>
public sealed record PcCarePlan(ActionPlan? Plan, string? Problem);

/// <summary>Choices made on the page itself (outside the rows).</summary>
public sealed record PcCarePageChoices
{
    /// <summary>"I accept the source and package agreements" (App updates tab).</summary>
    public bool AcceptAgreements { get; init; }
    /// <summary>"I understand shredded files can't be recovered" (Shredder tab).</summary>
    public bool ShredAcknowledged { get; init; }
}

/// <summary>
/// Turns the ticked rows of one tab into a plan: drops duplicates (the same tool or page twice), keeps a safe order
/// (Winsock reset last, DISM before SFC), adds the agreement flag only when the user accepted, and refuses shredding
/// until the page's "I understand" box is ticked (the confirm dialog then asks a second time).
/// </summary>
public static class PcCarePlanBuilder
{
    public static PcCarePlan Build(string title, IEnumerable<ScanItem> picked, PcCarePageChoices choices)
    {
        var items = PlanBuilder.FromSelection(PcCareActions.FeatureId, title, picked).Items;
        if (items.Count == 0) return new PcCarePlan(null, "Tick something first.");

        if (items.Any(i => i.Kind == ActionKind.ShredFile || i.ToolId == ToolIds.CipherWipeFreeSpace) && !choices.ShredAcknowledged)
            return new PcCarePlan(null, $"Tick \"{ShredderPlanner.PageAcknowledgement}\" first.");
        if (items.Count(i => i.ToolId == ToolIds.ShadowStorageResize) > 1)
            return new PcCarePlan(null, "Pick one restore point space size.");

        var unique = new List<PlanItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
            if (seen.Add(Key(i))) unique.Add(i);

        IReadOnlyList<PlanItem> ordered = unique.OrderBy(Rank).ToList(); // OrderBy is stable: same-rank items keep their order
        if (choices.AcceptAgreements) ordered = PcCareActions.WithAgreementsAccepted(ordered);
        return new PcCarePlan(new ActionPlan { FeatureId = PcCareActions.FeatureId, Title = title, Items = ordered }, null);
    }

    /// <summary>Same tool with the same arguments, same page, same file: one step.</summary>
    private static string Key(PlanItem i) => i.Kind switch
    {
        ActionKind.RunSystemTool => "tool|" + i.ToolId + "|" + string.Join(";", (i.Args ?? new()).OrderBy(a => a.Key).Select(a => a.Key + "=" + a.Value)),
        ActionKind.OpenExternal => "open|" + i.Args?.GetValueOrDefault("target"),
        ActionKind.ShredFile => "shred|" + i.SourcePath,
        _ => i.Id,
    };

    private static int Rank(PlanItem i) => i.ToolId switch
    {
        ToolIds.RestorePointCreate => 0,
        ToolIds.DismRestoreHealth => 1,
        ToolIds.SfcScan => 2,
        ToolIds.WinsockReset => 9,
        _ => i.Kind == ActionKind.OpenExternal ? 8 : 5,
    };

    /// <summary>Button text that says what the picked rows do.</summary>
    public static string VerbFor(IReadOnlyList<PlanItem> items)
    {
        if (items.Count == 0) return "Nothing selected";
        int updates = items.Count(i => i.ToolId == ToolIds.WingetUpgrade);
        int shreds = items.Count(i => i.Kind == ActionKind.ShredFile);
        int opens = items.Count(i => i.Kind == ActionKind.OpenExternal);
        int reports = items.Count(i => i.Kind == ActionKind.WriteTextFile);
        if (updates == items.Count) return $"Update {updates} app{(updates == 1 ? "" : "s")}";
        if (shreds == items.Count) return $"Shred {shreds} file{(shreds == 1 ? "" : "s")}";
        if (reports == items.Count) return reports == 1 ? "Save report" : "Save reports";
        if (opens == items.Count) return opens == 1 ? "Open page" : $"Open {opens} pages";
        if (items.All(i => i.ToolId is ToolIds.DismRestoreHealth or ToolIds.SfcScan)) return "Repair Windows";
        if (items.All(i => i.ToolId == ToolIds.RestorePointCreate)) return "Make restore point";
        if (items.All(i => i.Kind == ActionKind.RestorePointDelete)) return $"Delete {items.Count} restore point{(items.Count == 1 ? "" : "s")}";
        int changes = items.Count - opens;
        return changes == 1 ? "Apply 1 change" : $"Apply {changes} changes";
    }
}
