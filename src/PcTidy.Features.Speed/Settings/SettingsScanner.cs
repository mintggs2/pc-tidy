using System.Diagnostics;
using PcTidy.Catalog.Tweaks;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Settings;

/// <summary>
/// The Settings cards (Plan §8 Speed): power plan, Game Mode, GPU scheduling and visual effects, each with its
/// current state and an honest recommendation (Tweaks.md verdicts). Nothing changes until you tick and confirm.
/// Laptops on battery are never offered High performance or Ultimate.
/// </summary>
public sealed class SettingsScanner : IFeatureScanner
{
    public const string ScannerId = "speed.settings";
    public const string GroupId = "speed-settings";
    public const string GameModeItemId = "speed.settings.game-mode";

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public SettingsScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "Settings";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var env = _environment(ctx);
            var items = new List<ScanItem>();
            items.AddRange(PowerPlanItems(env, ctx.System));
            ct.ThrowIfCancellationRequested();
            items.Add(GameModeItem(env));
            if (GpuSchedulingItem(env) is { } gpu) items.Add(gpu);
            items.Add(VisualEffectsItem(env));
            return new ScanResult
            {
                FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed,
                Groups = new[]
                {
                    new ScanGroup
                    {
                        Id = GroupId, Title = "Speed settings",
                        Description = "Each card shows how it's set now and what we recommend. Tick only what you want changed; every change can be undone.",
                        Badge = Badge.Check, Items = items,
                    },
                },
            };
        }, ct);

    // ------------------------------------------------------------------ power plan

    /// <summary>
    /// True when a laptop might be running on battery (on battery now, or Windows can't tell): High performance and
    /// Ultimate are never offered then (Research §4: don't push them on laptops on battery).
    /// </summary>
    public static bool IsLaptopOnBattery(ISystemInfo? system, bool? onBattery)
        => (system?.IsLaptop == true || system?.HasBattery == true) && onBattery != false;

    internal static IEnumerable<ScanItem> PowerPlanItems(SpeedEnvironment env, ISystemInfo? system)
    {
        var schemes = env.Power.Schemes();
        var active = env.Power.ActiveScheme();
        var current = schemes.FirstOrDefault(s => s.Id == active);
        var battery = IsLaptopOnBattery(system, env.Power.OnBattery());
        var laptop = system?.IsLaptop == true || system?.HasBattery == true;
        // Dual-CCD Ryzen X3D: the game is steered onto the cache cores only on Balanced (Community tweaks §9.6).
        var x3d = CpuKinds.Classify(env.CpuBrand()) == CpuKind.DualCcdX3D;

        if (schemes.Count == 0 || current is null)
        {
            yield return new ScanItem
            {
                Id = "speed.settings.power", Title = "Power plan", Reason = "Windows didn't say which power plan is active, so there's nothing to suggest.",
                CurrentState = "Couldn't check", DisabledReason = "Couldn't read the power plans on this PC.",
            };
            yield break;
        }

        yield return new ScanItem
        {
            Id = "speed.settings.power",
            Title = "Power plan",
            Reason = x3d && current.Personality != PowerPersonality.Balanced
                ? CpuKinds.DualCcdX3DAdvice
                : PowerRecommendation(current.Personality, laptop, battery) + (x3d ? " " + CpuKinds.DualCcdX3DAdvice : ""),
            CurrentState = current.Name,
            Details = new Dictionary<string, string> { ["Active plan"] = current.Name, ["On battery now"] = !laptop ? "No battery" : battery ? "Yes, or Windows can't tell" : "No, plugged in" },
            DisabledReason = "This card shows the current plan. Pick a plan below to switch.",
        };

        foreach (var s in schemes.Where(s => s.Id != current.Id).OrderBy(s => s.Personality == PowerPersonality.Balanced ? 0 : 1).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string? disabled = null;
            if (s.Personality == PowerPersonality.HighPerformance && x3d)
                disabled = "Not on this processor: " + CpuKinds.DualCcdX3DAdvice;
            else if (s.Personality == PowerPersonality.Unknown && x3d)
                disabled = "Not on this processor: keep Balanced. " + CpuKinds.DualCcdX3DAdvice;
            else if (s.Personality == PowerPersonality.HighPerformance && battery)
                disabled = "This laptop is (or may be) on battery: this plan drains it fast and isn't faster for everyday use.";
            else if (s.Personality == PowerPersonality.Unknown)
                disabled = "PC Tidy can't tell what this plan is tuned for, so it doesn't switch to it. You can pick it in Windows' power settings.";
            var recommended = s.Personality == PowerPersonality.Balanced && current.Personality != PowerPersonality.Balanced
                              && !(current.Personality == PowerPersonality.PowerSaver && battery);
            yield return new ScanItem
            {
                Id = "speed.settings.power:" + s.Id.ToString("D"),
                Title = "Switch to " + s.Name,
                Reason = s.Personality switch
                {
                    PowerPersonality.Balanced => "Microsoft's recommended plan: the processor still speeds up fully when you need it, and saves power when you don't."
                                                 + (recommended ? " Recommended." : ""),
                    PowerPersonality.HighPerformance => "Keeps the processor ready at higher speed. Uses more power; for games it makes little or no difference.",
                    PowerPersonality.PowerSaver => "Slows the PC down to save power. Only worth it to stretch a laptop battery.",
                    _ => "A custom plan.",
                },
                Badge = Badge.Check,
                CurrentState = "Not active",
                Details = new Dictionary<string, string> { ["Plan"] = s.Name, ["Tuned for"] = s.Personality.ToString() },
                Actions = disabled is null ? new[] { SpeedPlanItems.PowerPlan(s.Name, s.Id) } : Array.Empty<PlanItem>(),
                DisabledReason = disabled,
            };
        }
    }

    private static string PowerRecommendation(PowerPersonality current, bool laptop, bool battery) => current switch
    {
        PowerPersonality.Balanced => "Balanced is what Microsoft recommends: full speed when you need it. Nothing to change.",
        PowerPersonality.HighPerformance when battery => "On battery this plan drains it quickly. Balanced is just as quick for everyday use.",
        PowerPersonality.HighPerformance => "Uses more power for little or no gain, even in games. Balanced is recommended, but this is your call.",
        PowerPersonality.PowerSaver when laptop && battery => "Good for stretching the battery. Switch to Balanced when you plug in.",
        PowerPersonality.PowerSaver => "This slows the PC down to save power. Balanced is recommended.",
        _ => "A custom plan. Balanced is what Microsoft recommends.",
    };

    // ------------------------------------------------------------------ Game Mode

    /// <summary>Game Mode: on unless AutoGameModeEnabled is 0 (no value = on, Windows' default). Turning it on is Safe.</summary>
    internal static ScanItem GameModeItem(SpeedEnvironment env)
    {
        var value = env.Registry.With(RegistryHive.CurrentUser, SpeedRegistryAllowances.GameBarKey, k => k.Dword(SpeedRegistryAllowances.GameModeValue), null);
        var on = value != 0;
        return new ScanItem
        {
            Id = GameModeItemId,
            Title = on ? "Game Mode is on" : "Turn Game Mode on",
            Reason = on
                ? "Game Mode gives your game priority and holds back Windows Update installs and restart prompts while you play. Keep it on."
                : "Gives your game priority and holds back Windows Update installs and restart prompts while you play. Turning it off doesn't make games faster.",
            Badge = Badge.Safe,
            DefaultSelected = !on,
            CurrentState = on ? "On" : "Off",
            Details = new Dictionary<string, string>
            {
                ["Registry key"] = env.Registry.Describe(RegistryHive.CurrentUser, SpeedRegistryAllowances.GameBarKey),
                ["Value"] = SpeedRegistryAllowances.GameModeValue + " = " + (value?.ToString() ?? "not set (on)"),
            },
            Actions = on ? Array.Empty<PlanItem>() : new[]
            {
                SpeedPlanItems.Registry("Turn Game Mode on", RegistryHive.CurrentUser, SpeedRegistryAllowances.GameBarKey,
                    SpeedRegistryAllowances.GameModeValue, SpeedRegistryAllowances.GameModeOn),
            },
        };
    }

    // ------------------------------------------------------------------ GPU scheduling

    /// <summary>Hardware-accelerated GPU scheduling: only shown when the graphics driver supports it. Recommendation: leave it on.</summary>
    internal static ScanItem? GpuSchedulingItem(SpeedEnvironment env)
    {
        var support = env.Gpu.Read();
        if (support is not { Supported: true }) return null;
        var value = env.Registry.With(RegistryHive.LocalMachine, SpeedRegistryAllowances.GraphicsDriversKey, k => k.Dword(SpeedRegistryAllowances.GpuSchedulingValue), null);
        // The driver's view is what's active now; the registry value is what applies after the next restart.
        var on = value is { } v ? v == 2 : support.EnabledNow;
        var pendingRestart = value is { } v2 && (v2 == 2) != support.EnabledNow;
        return new ScanItem
        {
            Id = "speed.settings.gpu-scheduling",
            Title = on ? "GPU scheduling is on" : "Turn GPU scheduling on",
            Reason = on
                ? "Lets the graphics card manage its own memory. Needed for frame generation in newer games; otherwise it changes little. Leave it on."
                : "Lets the graphics card manage its own memory; needed for frame generation in newer games. Recommended on; turn it off again only to test a stutter.",
            Badge = Badge.Check,
            NeedsAdmin = !on,
            NeedsRestart = !on,
            CurrentState = (on ? "On" : "Off") + (pendingRestart ? " (after the next restart)" : ""),
            Details = new Dictionary<string, string>
            {
                ["Registry key"] = env.Registry.Describe(RegistryHive.LocalMachine, SpeedRegistryAllowances.GraphicsDriversKey),
                ["Value"] = SpeedRegistryAllowances.GpuSchedulingValue + " = " + (value?.ToString() ?? "not set"),
                ["Supported by your graphics driver"] = "Yes",
            },
            Actions = on ? Array.Empty<PlanItem>() : new[]
            {
                SpeedPlanItems.Registry("Turn hardware-accelerated GPU scheduling on (takes effect after a restart)", RegistryHive.LocalMachine,
                    SpeedRegistryAllowances.GraphicsDriversKey, SpeedRegistryAllowances.GpuSchedulingValue, SpeedRegistryAllowances.GpuSchedulingOn),
            },
        };
    }

    // ------------------------------------------------------------------ visual effects

    /// <summary>
    /// Visual effects (VisualFXSetting: 0 let Windows choose, 1 best appearance, 2 best performance, 3 custom).
    /// Writing the value alone doesn't change what you see: the handler must also apply it with SystemParametersInfo.
    /// </summary>
    internal static ScanItem VisualEffectsItem(SpeedEnvironment env)
    {
        var value = env.Registry.With(RegistryHive.CurrentUser, SpeedRegistryAllowances.VisualEffectsKey, k => k.Dword(SpeedRegistryAllowances.VisualEffectsValue), null) ?? 0;
        var state = value switch { 1 => "Best appearance", 2 => "Best performance", 3 => "Custom", _ => "Let Windows choose" };
        var actions = new List<PlanItem>();
        if (value != 2)
            actions.Add(SpeedPlanItems.Registry("Set visual effects to 'Adjust for best performance'", RegistryHive.CurrentUser,
                SpeedRegistryAllowances.VisualEffectsKey, SpeedRegistryAllowances.VisualEffectsValue, SpeedRegistryAllowances.VisualEffectsBestPerformance));
        else
            actions.Add(SpeedPlanItems.Registry("Set visual effects back to 'Let Windows choose'", RegistryHive.CurrentUser,
                SpeedRegistryAllowances.VisualEffectsKey, SpeedRegistryAllowances.VisualEffectsValue, SpeedRegistryAllowances.VisualEffectsLetWindowsChoose));
        return new ScanItem
        {
            Id = "speed.settings.visual-effects",
            Title = value == 2 ? "Visual effects: back to Let Windows choose" : "Visual effects: best performance",
            Reason = value == 2
                ? "Animations and shadows are off for speed. Let Windows choose turns the ones your PC handles easily back on."
                : "Turns off animations, shadows and smooth fonts. Helps only on older or slow PCs, and Windows looks plainer. Your call.",
            Badge = Badge.Check,
            CurrentState = state,
            Details = new Dictionary<string, string>
            {
                ["Registry key"] = env.Registry.Describe(RegistryHive.CurrentUser, SpeedRegistryAllowances.VisualEffectsKey),
                ["Value"] = SpeedRegistryAllowances.VisualEffectsValue + " = " + value,
            },
            Actions = actions,
        };
    }
}
