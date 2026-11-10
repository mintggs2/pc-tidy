using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Organize.Planning;

/// <summary>
/// Organize's read-only scanner for the standard flow and "Optimize &amp; Organize everything".
/// Roots: <see cref="ScanContext.Roots"/> when given; otherwise Downloads, plus Desktop only when Desktop
/// is not in a cloud folder or the user allowed it (Desktop is often redirected into OneDrive).
/// Rules: <c>ScanContext.Options["organize"]</c> (an <see cref="OrganizeSettings"/>) or the saved settings.
/// </summary>
public sealed class OrganizeScanner : IFeatureScanner
{
    private readonly IClock _clock;

    public OrganizeScanner() : this(SystemClock.Instance) { }
    public OrganizeScanner(IClock clock) => _clock = clock;

    public string FeatureId => OrganizeModule.FeatureId;
    public string Title => "Organize files";
    public Release Release => Release.V1;
    public bool IncludedInOptimizeAll => true;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var settings = ctx.Options.TryGetValue(OrganizeSettings.FeatureKey, out var o) && o is OrganizeSettings s ? s : OrganizeSettings.Load(ctx.Settings);
        var roots = ctx.Roots is { Count: > 0 } r ? r : DefaultRoots(ctx.Locations, ctx.Safety, ctx.Settings);
        var planner = new OrganizePlanner(ctx.Safety, ctx.InUse, ctx.Walker, ctx.Settings, _clock);
        var preview = await planner.PlanAsync(new OrganizeRequest { Roots = roots, Settings = settings, Depth = ctx.Depth }, progress, ct).ConfigureAwait(false);
        // "Safe" and "Privacy" presets only clean; nothing is sorted unless the user ticks it.
        bool tick = ctx.Preset is null or "Recommended" or "Gamer";
        return OrganizeResultBuilder.Build(preview, tick);
    }

    /// <summary>Default folders for Optimize-all: Downloads, and Desktop only when it's safe to sort without asking.</summary>
    public static IReadOnlyList<string> DefaultRoots(IKnownLocations locations, ISafetyGuard safety, AppSettings settings)
    {
        var roots = new List<string> { locations.Downloads };
        var desktop = locations.Desktop;
        bool desktopAllowed = !safety.IsInCloudRoot(desktop) || settings.AllowedAskFirstFolders.Any(f => PathUtil.IsUnder(desktop, f));
        if (desktopAllowed && !string.IsNullOrEmpty(desktop) && !PathUtil.IsUnder(desktop, locations.Downloads)) roots.Add(desktop);
        return roots;
    }
}
