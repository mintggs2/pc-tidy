using System.Diagnostics;
using PcTidy.Features.Tweaks.Planning;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.Tweaks;

namespace PcTidy.Features.Tweaks;

/// <summary>The rows of one tweak scan (the page uses these directly; <see cref="TweaksScanner.ScanAsync"/> groups them).</summary>
public sealed record TweakScanOutcome(IReadOnlyList<TweakRow> Rows, IReadOnlyList<string> Notes);

/// <summary>
/// Tweaks (Plan §8 "12 · Tweaks"). Reads the CURRENT state of every applicable tweak, read-only, and builds
/// three-state rows (Available / Already applied / Differs). Placebo rows are shown greyed with "Doesn't help";
/// harmful rows are never shown. Expert-only rows need <see cref="AppSettings.ExpertMode"/> and never appear
/// ticked or in Optimize-all. Only proven rows of a preset start ticked.
/// </summary>
public sealed class TweaksScanner : IFeatureScanner
{
    private readonly Func<ScanContext, TweakEnvironment> _environment;

    /// <param name="environment">Readers to use; default = real PC, or the redirected sandbox when the context is a sandbox.</param>
    public TweaksScanner(Func<ScanContext, TweakEnvironment>? environment = null)
        => _environment = environment ?? TweakEnvironment.ForContext;

    /// <summary>
    /// Shown with every tweak scan (Community tweaks §9.6 corrections): updates move game speed more than any tweak, so
    /// PC Tidy never offers to block them. The 2024 AMD branch-prediction update is the measured example.
    /// </summary>
    public const string UpdatesNote =
        "Windows updates change game speed more than any tweak here: one 2024 update (KB5041587) gave Ryzen PCs about 4-5% in many games. "
        + "That's why PC Tidy never blocks or delays Windows Update.";

    public string FeatureId => TweakPlanItems.TweaksFeature;
    public string Title => "Tweaks";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => true;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outcome = await ScanRowsAsync(ctx, progress, ct).ConfigureAwait(false);
        return new ScanResult
        {
            FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
            Groups = Group(outcome.Rows), Notes = outcome.Notes,
        };
    }

    /// <summary>Reads every tweak's state. Read-only; runs off the UI thread.</summary>
    public Task<TweakScanOutcome> ScanRowsAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var env = _environment(ctx);
            var expertAllowed = ctx.Settings.ExpertMode && ctx.Preset is null;
            var notes = new List<string>();
            var rows = new List<TweakRow>();
            var shown = TweakCatalog.Shown.ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = shown[i];
                progress?.Report(new ScanProgress($"Checking {t.Title}", (double)i / shown.Count, i));
                if (!Applies(t, env)) continue;
                // Second wall behind the catalog tests: anything touching security, updates or anti-cheat is never shown.
                if (t.Changes.Any(c => TweakSafetyRules.WhyForbidden(c) is not null) && t.IsOffered) continue;
                TweakState state;
                try { state = TweakStateReader.Read(t, env); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    state = new TweakState(TweakStatus.Unknown, Array.Empty<ChangeState>(), null);
                }
                if (state.Status == TweakStatus.NotPresent) continue; // app or service not on this PC
                rows.Add(TweakRowBuilder.Build(t, state, env, expertAllowed));
            }
            if (ctx.Preset is null && !ctx.Settings.ExpertMode && rows.Any(r => r.Tweak.IsExpertOnly && r.Tweak.HasActions))
                notes.Add("Some advanced tweaks need Expert mode. They're shown greyed so you know they exist.");
            if (env.Probe.EditionId is { } edition && !edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) && !edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase))
                notes.Add("PC Tidy uses your own Settings switches where it can, because many privacy policies only work on Enterprise and Education.");
            notes.Add(UpdatesNote);
            if (env.Probe.CpuKind == CpuKind.DualCcdX3D) notes.Add(CpuKinds.DualCcdX3DAdvice);
            progress?.Report(new ScanProgress("Done", 1, rows.Count));
            return new TweakScanOutcome(rows, notes);
        }, ct);

    /// <summary>Build range, laptop/desktop, GPU vendor, CPU family, required key (e.g. Office installed).</summary>
    public static bool Applies(TweakDefinition t, TweakEnvironment env)
    {
        var a = t.Applies;
        if (!a.Matches(env.WindowsBuild, env.IsLaptop, env.Gpu)) return false;
        if (a.HarmfulOn != CpuKind.None && (a.HarmfulOn & env.Probe.CpuKind) != 0) return false;
        if (a.RequiresKey is { } key && (!RegistryNames.TrySplit(key, out var hive, out var path) || !env.Registry.KeyExists(hive, path))) return false;
        return true;
    }

    /// <summary>
    /// Preset groups first (ids contain "safe-gaming", "privacy-basic", "clean-explorer" so Optimize-all can pick
    /// them), then one group per section for everything else.
    /// </summary>
    public static IReadOnlyList<ScanGroup> Group(IReadOnlyList<TweakRow> rows)
    {
        var groups = new List<ScanGroup>();
        foreach (var p in TweakPresets.All)
        {
            var items = rows.Where(r => r.Tweak.Preset == p.Preset).Select(r => r.Item).ToList();
            if (items.Count == 0) continue;
            groups.Add(new ScanGroup
            {
                Id = $"tweaks.{p.Id}", Title = $"{p.Title} preset", Description = p.Description + " " + p.Excluded,
                Badge = items.Any(i => i.Badge != Badge.Safe && i.DisabledReason is null && i.Actions.Count > 0) ? Badge.Check : Badge.Safe,
                Items = items,
            });
        }
        foreach (var g in Enum.GetValues<TweakGroup>())
        {
            var items = rows.Where(r => r.Tweak.Preset == TweakPreset.None && r.Tweak.Group == g).Select(r => r.Item).ToList();
            if (items.Count == 0) continue;
            groups.Add(new ScanGroup { Id = "tweaks." + GroupId(g), Title = GroupTitle(g), Description = GroupDescription(g), Badge = Badge.Check, Items = items });
        }
        return groups;
    }

    public static string GroupId(TweakGroup g) => g switch
    {
        TweakGroup.UiExplorer => "ui-explorer",
        _ => g.ToString().ToLowerInvariant(),
    };

    public static string GroupTitle(TweakGroup g) => g switch
    {
        TweakGroup.Gaming => "Gaming",
        TweakGroup.Privacy => "Privacy",
        TweakGroup.Debloat => "Debloat",
        TweakGroup.UiExplorer => "UI and Explorer",
        TweakGroup.Network => "Network",
        _ => "Power",
    };

    public static string GroupDescription(TweakGroup g) => g switch
    {
        TweakGroup.Gaming => "Settings that really change how games run, and the popular ones that don't.",
        TweakGroup.Privacy => "Less data sent to Microsoft and fewer ads, using your own Settings switches where possible.",
        TweakGroup.Debloat => "Consumer Store apps you can remove for your account. Windows' own parts are never offered.",
        TweakGroup.UiExplorer => "File Explorer and taskbar preferences.",
        TweakGroup.Network => "Network settings with real effects, and the myths.",
        _ => "Power and startup settings.",
    };
}
