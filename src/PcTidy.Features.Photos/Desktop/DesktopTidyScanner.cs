using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;

namespace PcTidy.Features.Photos.Desktop;

/// <summary>
/// Desktop tidy's read-only scanner. Desktop: <see cref="ScanContext.Roots"/>[0] when given, else the real Desktop.
/// Options: <c>ScanContext.Options["desktoptidy"]</c> (a <see cref="DesktopTidySettings"/>) or the saved settings.
/// Not part of "Optimize &amp; Organize everything" (Organize already offers the Desktop there).
/// </summary>
public sealed class DesktopTidyScanner : IFeatureScanner
{
    private readonly IClock _clock;

    public DesktopTidyScanner() : this(SystemClock.Instance) { }
    public DesktopTidyScanner(IClock clock) => _clock = clock;

    public string FeatureId => DesktopTidyModule.FeatureId;
    public string Title => "Desktop tidy";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var settings = ctx.Options.TryGetValue(DesktopTidySettings.FeatureKey, out var o) && o is DesktopTidySettings s ? s : DesktopTidySettings.Load(ctx.Settings);
        var desktop = ctx.Roots is { Count: > 0 } r ? r[0] : ctx.Locations.Desktop;
        var planner = new DesktopTidyPlanner(ctx.Safety, ctx.InUse, ctx.Walker, ctx.Settings, _clock);
        var preview = await planner.PlanAsync(DesktopTidyRequest.From(settings, desktop, ctx.Depth), progress, ct).ConfigureAwait(false);
        return DesktopTidyResults.Build(preview);
    }
}
