using System.Diagnostics;
using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Scanning;

namespace PcTidy.Features.Services;

/// <summary>
/// The Services section's part of "Optimize &amp; Organize everything" and the standard flow. Read-only.
/// Groups: one per profile (ids contain "profile-gaming", "profile-laptop-battery", "profile-privacy" so presets can
/// match them; only the preset's own profile is pre-ticked, and only Safe rows), then other catalog services that can
/// change, then (outside Optimize-all) third-party services that start with Windows (Manual only, never ticked).
/// Every service appears in one group at most, so counts match the plan.
/// </summary>
public sealed class ServicesScanner : IFeatureScanner
{
    public const string ThirdPartyGroup = "services.third-party";

    private readonly Func<ScanContext, ServicesEnvironment> _environment;

    public ServicesScanner(Func<ScanContext, ServicesEnvironment>? environment = null) => _environment = environment ?? ServicesEnvironment.For;

    public string FeatureId => ServicesIds.Module;
    public string Title => "Services";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => true;

    /// <summary>The full inventory (the page uses it directly). Situational entries unlock only in Expert mode outside presets.</summary>
    public Task<ServiceInventoryResult> InventoryAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => ServiceInventory.ReadAsync(_environment(ctx), ctx.Locations, ctx.Settings.ExpertMode && ctx.Preset is null, progress, ct);

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var inventory = await InventoryAsync(ctx, progress, ct).ConfigureAwait(false);
        return new ScanResult
        {
            FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
            Groups = Groups(inventory, ctx), Notes = inventory.Notes,
        };
    }

    public static IReadOnlyList<ScanGroup> Groups(ServiceInventoryResult inventory, ScanContext ctx)
    {
        var groups = new List<ScanGroup>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presetProfile = ServiceProfileBuilder.ForPreset(ctx.Preset);
        // The preset's own profile first, so a service in two profiles is ticked by the preset that wants it.
        var profiles = ServiceProfileBuilder.All.OrderBy(p => p.Id == presetProfile ? 0 : 1).ToList();
        foreach (var profile in profiles)
        {
            var state = ServiceProfileBuilder.State(profile, inventory.Services, ctx.System);
            if (!state.AppliesHere) continue;
            var items = state.Changes.Where(c => used.Add(c.Service.Name))
                .Select(c => ServicePlanItems.Row(ServicesIds.Module, c.Service, c.Option, defaultSelected: profile.Id == presetProfile))
                .ToList();
            if (items.Count == 0)
                items.Add(ServicePlanItems.Info($"{ServicesIds.GroupFor(profile.Id)}.none", $"{profile.Title}: nothing to do", state.Status));
            groups.Add(new ScanGroup
            {
                Id = ServicesIds.GroupFor(profile.Id), Title = $"{profile.Title} profile",
                Description = $"{profile.Description} {state.Status}",
                Badge = items.All(i => i.Badge == Badge.Safe) ? Badge.Safe : Badge.Check, Items = items,
            });
        }

        var more = inventory.Services
            .Where(s => s.Entry is not null && s.Assessment.CanChange && !used.Contains(s.Name))
            .Select(s => ServicePlanItems.Row(ServicesIds.Module, s, s.Assessment.Recommended!, defaultSelected: false))
            .ToList();
        if (more.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = ServicesIds.OtherGroup, Title = "More Windows services", Badge = Badge.Check, Items = more,
                Description = "Other services PC Tidy has proven advice for. Each says what might stop working. Nothing is ticked.",
            });

        // Speed's startup list already offers these in Optimize-all; here they're only listed outside a preset run.
        if (ctx.Preset is null)
        {
            var thirdParty = inventory.Services
                .Where(s => s.Entry is null && s.Assessment.Verdict == ServiceVerdictKind.ThirdParty && s.Assessment.CanChange)
                .Select(s => ServicePlanItems.Row(ServicesIds.Module, s, s.Assessment.Recommended!, defaultSelected: false))
                .ToList();
            if (thirdParty.Count > 0)
                groups.Add(new ScanGroup
                {
                    Id = ThirdPartyGroup, Title = "Other companies' services that start with Windows", Badge = Badge.Check, Items = thirdParty,
                    Description = "Set to Manual, they start when their app needs them instead of with Windows. Never Disabled.",
                });
        }
        return groups;
    }
}
