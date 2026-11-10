using System.Diagnostics;
using PcTidy.Catalog.Privacy;
using PcTidy.Features.Privacy.Common;

namespace PcTidy.Features.Privacy.Notifications;

/// <summary>
/// Windows tips, suggestions and ads (Start, lock screen, Settings, pop-ups) as the user's own per-user switches from
/// <see cref="TipsCatalog"/>: 1 or missing = on, 0 = off, nothing that says "Managed by your organization".
/// On its own page nothing is ticked: a row turns a surface off (Safe) or back on (Check).
/// In "Optimize everything" it takes part only in the Privacy preset, and then only offers to turn things off, pre-ticked.
/// Values the Tweaks section's Privacy-basic preset already covers are left to Tweaks there, so one run never lists the
/// same switch twice.
/// </summary>
public sealed class TipsScanner : IFeatureScanner
{
    private readonly Func<ScanContext, NotificationsEnvironment> _environment;

    public TipsScanner(Func<ScanContext, NotificationsEnvironment>? environment = null)
        => _environment = environment ?? NotificationsEnvironment.ForContext;

    public string FeatureId => PrivacyIds.Notifications;
    public string Title => "Windows tips and ads";
    public Release Release => Release.V3;
    /// <summary>True so the Privacy preset can include it; every other preset gets an empty result.</summary>
    public bool IncludedInOptimizeAll => true;

    /// <summary>One switch as read: logical value, physical key, and whether it's on.</summary>
    public sealed record SwitchState(TipsValue Value, string PhysicalKey, bool On);

    public static IReadOnlyList<SwitchState> Read(UserRegistry registry, TipsSetting setting)
        => setting.Values
            .Select(v => (v, read: registry.ReadDword(v.KeyPath, v.ValueName)))
            .Where(x => !x.read.IsOtherType) // set some other way: left alone
            .Select(x => new SwitchState(x.v, registry.Map(x.v.KeyPath), !x.read.Exists || x.read.Value != 0))
            .ToList();

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var inPreset = ctx.Preset is not null;
            if (inPreset && !string.Equals(ctx.Preset, PrivacyIds.PrivacyPreset, StringComparison.OrdinalIgnoreCase))
                return Result(ctx, sw, Array.Empty<ScanItem>());

            var env = _environment(ctx);
            var sharedWithTweaks = inPreset
                ? PrivacyRegistryAllowances.SharedWithTweaks().ToHashSet()
                : new HashSet<TipsValue>();
            var items = new List<ScanItem>();
            foreach (var setting in TipsCatalog.All)
            {
                ct.ThrowIfCancellationRequested();
                var states = Read(env.Registry, setting).Where(s => !sharedWithTweaks.Contains(s.Value)).ToList();
                if (states.Count == 0) continue;
                var on = states.Where(s => s.On).ToList();
                if (on.Count > 0) items.Add(TurnOff(setting, on, states.Count, preselect: inPreset));
                else if (!inPreset) items.Add(TurnBackOn(setting, states));
            }
            progress?.Report(new ScanProgress("Done", 1, items.Count));
            return Result(ctx, sw, items);
        }, ct);

    private ScanResult Result(ScanContext ctx, Stopwatch sw, IReadOnlyList<ScanItem> items) => new()
    {
        FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
        Groups = items.Count == 0 ? Array.Empty<ScanGroup>() : new[]
        {
            new ScanGroup
            {
                Id = PrivacyIds.TipsGroup, Title = "Windows tips, suggestions and ads",
                Description = "Your own Settings switches, so nothing says \"Managed by your organization\". Undo puts each switch back exactly as it was.",
                Badge = Badge.Safe, Items = items,
            },
        },
    };

    private static ScanItem TurnOff(TipsSetting s, IReadOnlyList<SwitchState> on, int total, bool preselect) => new()
    {
        Title = "Turn off: " + s.Title,
        Detail = s.WhereYouSeeIt,
        Reason = s.WhatChanges,
        Badge = Badge.Safe,
        DefaultSelected = preselect,
        CurrentState = on.Count == total ? "On" : "Partly on",
        Details = Facts(s, on),
        Actions = on.Select(x => PrivacyPlanItems.SetUserDword(PrivacyIds.Notifications, x.PhysicalKey, x.Value.ValueName, 0,
            $"Turn off {s.Title} ({x.Value.ValueName})")).ToList(),
    };

    private static ScanItem TurnBackOn(TipsSetting s, IReadOnlyList<SwitchState> all) => new()
    {
        Title = "Turn back on: " + s.Title,
        Detail = s.WhereYouSeeIt,
        Reason = "It's off now. Ticking this lets Windows show it again.",
        Badge = Badge.Check,
        DefaultSelected = false,
        CurrentState = "Off",
        Details = Facts(s, all),
        Actions = all.Select(x => PrivacyPlanItems.SetUserDword(PrivacyIds.Notifications, x.PhysicalKey, x.Value.ValueName, 1,
            $"Turn back on {s.Title} ({x.Value.ValueName})")).ToList(),
    };

    private static IReadOnlyDictionary<string, string> Facts(TipsSetting s, IEnumerable<SwitchState> states) => new Dictionary<string, string>
    {
        ["Where"] = s.WhereYouSeeIt,
        ["Switches"] = string.Join(", ", states.Select(x => x.Value.ValueName)),
        ["Key"] = string.Join(", ", states.Select(x => @"HKCU\" + x.Value.KeyPath).Distinct()),
    };
}
