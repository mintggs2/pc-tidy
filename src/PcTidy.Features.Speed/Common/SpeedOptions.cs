using PcTidy.UI.Flow;

namespace PcTidy.Features.Speed.Common;

/// <summary>Per-scan options the Speed page passes in <see cref="ScanContext.Options"/>.</summary>
public static class SpeedOptions
{
    /// <summary>bool: hide Microsoft's own entries (default true).</summary>
    public const string HideMicrosoft = "speed.hideMicrosoft";

    public static bool HidesMicrosoft(ScanContext ctx)
        => !ctx.Options.TryGetValue(HideMicrosoft, out var v) || v is not bool b || b;

    /// <summary>"Recommended" and "Gamer" (Optimize everything) pre-tick high-impact, non-Microsoft startup apps.</summary>
    public static bool PresetTicksStartup(ScanContext ctx)
        => string.Equals(ctx.Preset, OptimizePresets.Recommended, StringComparison.OrdinalIgnoreCase)
           || string.Equals(ctx.Preset, OptimizePresets.Gamer, StringComparison.OrdinalIgnoreCase);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
}
