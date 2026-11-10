using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;

namespace PcTidy.Features.Photos.Photos;

/// <summary>
/// The photo organizer's read-only scanner. Roots: <see cref="ScanContext.Roots"/> when given, else the folders the
/// user picked (Pictures by default). Options: <c>ScanContext.Options["photos"]</c> (a <see cref="PhotoSettings"/>) or
/// the saved settings. Not part of "Optimize &amp; Organize everything": sorting a photo library is always a deliberate step.
/// </summary>
public sealed class PhotosScanner : IFeatureScanner
{
    private readonly IClock _clock;

    public PhotosScanner() : this(SystemClock.Instance) { }
    public PhotosScanner(IClock clock) => _clock = clock;

    public string FeatureId => PhotosModule.FeatureId;
    public string Title => "Photos";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var settings = ctx.Options.TryGetValue(PhotoSettings.FeatureKey, out var o) && o is PhotoSettings s ? s : PhotoSettings.Load(ctx.Settings);
        var roots = ctx.Roots is { Count: > 0 } r ? r : settings.FoldersOrDefault(ctx.Locations);
        var planner = new PhotoPlanner(ctx.Safety, ctx.InUse, ctx.Walker, ctx.Settings, _clock);
        var preview = await planner.PlanAsync(PhotoRequest.From(settings, roots, ctx.Depth), progress, ct).ConfigureAwait(false);
        return PhotoResultBuilder.Build(preview);
    }
}
