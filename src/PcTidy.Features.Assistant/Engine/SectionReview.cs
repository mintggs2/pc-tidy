using PcTidy.UI.Flow;

namespace PcTidy.Features.Assistant.Engine;

/// <summary>
/// "Open this section's preview with these items pre-selected." That's all a suggestion can ever ask for:
/// nothing runs, the user still sees the normal preview and confirm.
/// </summary>
/// <param name="FeatureId">The scanner's feature id (ScanResult.FeatureId). The shell maps it to the module that owns that scanner.</param>
/// <param name="ItemIds">ScanItem.Id values from that section's latest scan.</param>
/// <param name="ScanFinishedUtc">Which scan the ids belong to (ScanResult.FinishedUtc), to spot a rescan.</param>
public sealed record SectionReviewRequest(string FeatureId, string SectionTitle, IReadOnlyList<string> ItemIds, DateTime ScanFinishedUtc);

/// <summary>
/// Implemented by the App shell and registered as a service. The assistant page calls it when the user presses
/// "Review in Clean →". The shell navigates to the section and pre-selects the items in its preview (for a
/// <see cref="StandardFlowPage"/>: <see cref="SectionReview.TryApply(StandardFlowPage, SectionReviewRequest)"/>).
/// Return false if the section can't show those items (e.g. it was rescanned): the page then says so.
/// </summary>
public interface ISectionReviewHost
{
    bool OpenForReview(SectionReviewRequest request);
}

/// <summary>Helpers the shell can use to carry out a <see cref="SectionReviewRequest"/> on the shared flow pages.</summary>
public static class SectionReview
{
    /// <summary>Pre-selects the items on a standard page that is showing the same scan's preview.</summary>
    public static bool TryApply(StandardFlowPage page, SectionReviewRequest request)
        => page.State == FlowState.Preview && TryApply(page.Selection, request) > 0;

    /// <summary>
    /// Ticks exactly the requested items of that section (and nothing else), opens their groups, and returns how many
    /// were found. 0 means the preview shows a different scan: nothing is changed then. Rows that can't be ticked,
    /// and Permanent / not-undoable rows, are never ticked this way (they keep their own second confirmation).
    /// </summary>
    public static int TryApply(SelectionModel model, SectionReviewRequest request)
    {
        var ids = request.ItemIds.ToHashSet(StringComparer.Ordinal);
        var section = model.Sections.FirstOrDefault(s => string.Equals(s.FeatureId, request.FeatureId, StringComparison.OrdinalIgnoreCase));
        if (section is null) return 0;
        bool Wanted(ItemRow i) => i.IsSelectable && !i.IsPermanent && ids.Contains(i.Item.Id);
        var found = section.Groups.SelectMany(g => g.Items).Count(Wanted);
        if (found == 0) return 0;

        model.Retick((s, _, i) => s == section && Wanted(i));
        foreach (var g in section.Groups.Where(g => g.Items.Any(i => i.IsChecked)))
            g.IsExpanded = true;
        return found;
    }
}
