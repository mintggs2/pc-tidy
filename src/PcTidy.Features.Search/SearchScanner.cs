using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Structure;

namespace PcTidy.Features.Search;

/// <summary>
/// The Search section's read-only scanner: the "Suggest a folder structure" proposal for the chosen preset
/// (searching itself needs no scan). Not part of "Optimize &amp; Organize everything": reorganizing whole folders is
/// always a deliberate choice. Options: <c>ctx.Options["search.preset"]</c> (a <see cref="StructurePreset"/>),
/// <c>ctx.Roots</c> = folders to suggest a structure for (default: every drive; Documents on the Windows drive).
/// </summary>
public sealed class SearchScanner : IFeatureScanner
{
    public const string PresetOption = "search.preset";

    private readonly TimeProvider _clock;

    public SearchScanner(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public string FeatureId => SearchIds.FeatureId;
    public string Title => "Folder structure";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var preset = ctx.Options.TryGetValue(PresetOption, out var o) && o is StructurePreset p ? p : SearchSettings.Load(ctx.Settings).Preset;
        var planner = new StructurePlanner(ctx.Locations, ctx.Safety, ctx.Walker, _clock);
        IReadOnlyList<StructureRoot>? roots = ctx.Roots is { Count: > 0 } r
            ? r.Where(Directory.Exists).Select(x => new StructureRoot(x, PathUtil.Normalize(x), IsDocuments(ctx.Locations, x))).ToList()
            : null;
        return Task.Run(() => planner.Plan(preset, roots, progress, ct), ct);
    }

    private static bool IsDocuments(IKnownLocations k, string path)
    {
        try { return string.Equals(PathUtil.Normalize(path), PathUtil.Normalize(k.Documents), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}
