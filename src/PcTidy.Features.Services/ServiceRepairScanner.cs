using System.Diagnostics;
using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Rules;
using PcTidy.Features.Services.Scanning;

namespace PcTidy.Features.Services;

/// <summary>A service that something switched off (or weakened) and the exact Windows default to put back.</summary>
public sealed record ServiceRepair(ServiceInfo Service, ServiceStart Current, ServiceStart Target, string Why);

/// <summary>
/// "Bad services" repair (FeatureId "services.repair"): finds services the catalog marks Harmful or Protected, or
/// that the SafetyGuard's own table lists (security, updates, sign-in, restore points, anti-cheat...), that are set to
/// Disabled or to a weaker start than Windows ships them with, and offers to put the Windows default back. It only
/// ever restores a listed Windows default (never Disabled); the guard and the Platform handler check the same rule.
/// Read-only. Not part of Optimize-all (like the bad-tweak finder): it's reviewed item by item.
/// </summary>
public sealed class ServiceRepairScanner : IFeatureScanner
{
    private readonly Func<ScanContext, ServicesEnvironment> _environment;

    public ServiceRepairScanner(Func<ScanContext, ServicesEnvironment>? environment = null) => _environment = environment ?? ServicesEnvironment.For;

    public string FeatureId => ServicesIds.Repair;
    public string Title => "Bad services";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var inventory = await ServiceInventory.ReadAsync(_environment(ctx), ctx.Locations, expertAllowed: false, progress, ct).ConfigureAwait(false);
        var repairs = Find(inventory.Services);
        var groups = repairs.Count == 0 ? Array.Empty<ScanGroup>() : new[]
        {
            new ScanGroup
            {
                Id = ServicesIds.RepairGroup, Title = "Windows services switched off", Badge = Badge.Safe, Items = repairs.Select(Row).ToList(),
                Description = "Old optimizers and \"disable these\" lists switched these off. Putting back Windows' own setting fixes what they broke.",
            },
        };
        return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = inventory.Notes };
    }

    /// <summary>Every service that needs its Windows default back. Per-user copies are skipped (their template key is listed instead).</summary>
    public static IReadOnlyList<ServiceRepair> Find(IEnumerable<ServiceInfo> services)
    {
        var list = new List<ServiceRepair>();
        foreach (var s in services)
        {
            if (s.IsDriver || (s.Record.IsPerUser && !ServicePolicy.IsTemplate(s.Record))) continue;
            if (s.Record.StartType is not { } current) continue;
            var guarded = s.Entry?.Verdict is ServiceVerdict.Harmful or ServiceVerdict.Protected || WindowsDefaults.IsGuardListed(s.Name);
            if (!guarded) continue;
            var defaults = WindowsDefaults.For(s.Name, s.Entry);
            if (!WindowsDefaults.IsWeakerThanDefault(current, defaults)) continue;
            // The first listed default (Windows 11's when 10 and 11 differ) that is a real start type for a service.
            var target = defaults.Where(d => d is not (ServiceStart.Disabled or ServiceStart.Boot or ServiceStart.System))
                .Select(d => (ServiceStart?)d).FirstOrDefault();
            if (target is not { } t) continue;
            list.Add(new ServiceRepair(s, current, t, Why(s, current, t)));
        }
        return list.OrderBy(r => r.Service.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Why(ServiceInfo s, ServiceStart current, ServiceStart target)
    {
        var what = s.Entry is { } e
            ? (string.IsNullOrWhiteSpace(e.BreaksIf) ? e.Why : $"While it's {ServiceText.Start(current)}, this may not work: {e.BreaksIf!.TrimEnd('.')}.")
            : "Windows needs it for security, updates, sign-in or other core features.";
        return $"Windows ships it as {ServiceText.Start(target)}, but it's set to {ServiceText.Start(current)}. {what}";
    }

    public static ScanItem Row(ServiceRepair r) => new()
    {
        Id = $"{ServicesIds.Repair}.{r.Service.Name}",
        Title = r.Service.DisplayName,
        Detail = $"{r.Service.Name} · now {ServiceText.Start(r.Current)} → Windows default {ServiceText.Start(r.Target)}",
        Reason = r.Why,
        Badge = Badge.Safe,
        DefaultSelected = false,
        NeedsAdmin = true,
        CurrentState = ServiceText.Start(r.Current),
        Details = ServicePlanItems.Details(r.Service),
        Actions = new[] { ServicePlanItems.SetStart(ServicesIds.Repair, r.Service, r.Target) with
        {
            Description = $"Put '{r.Service.DisplayName}' ({r.Service.Name}) back to its Windows default, {ServiceText.Start(r.Target)} (now {ServiceText.Start(r.Current)}).",
        } },
    };
}
