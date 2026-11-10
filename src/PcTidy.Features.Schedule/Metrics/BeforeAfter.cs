using System.Globalization;

namespace PcTidy.Features.Schedule.Metrics;

/// <summary>One "before → after" line for the Done card and the run report.</summary>
public sealed record MetricChange(string Key, string Label, double Before, double After, string Text, bool Improved, bool Unchanged);

/// <summary>
/// Compares a before and an after sample honestly: only numbers measured both times, with the direction that counts
/// as better for each. Boot numbers only change after a restart, so when they're identical they're described as
/// "shown after the next restart" instead of "no change".
/// </summary>
public static class BeforeAfter
{
    public static IReadOnlyList<MetricChange> Compare(IReadOnlyDictionary<string, double> before, IReadOnlyDictionary<string, double> after)
    {
        var changes = new List<MetricChange>();
        foreach (var key in MetricKeys.Ordered(before.Keys.Intersect(after.Keys)))
        {
            if (MetricKeys.Describe(key) is not { } info) continue;
            double b = before[key], a = after[key];
            bool unchanged = IsSame(info, b, a);
            bool improved = !unchanged && (info.HigherIsBetter ? a > b : a < b);
            string text = unchanged
                ? IsBoot(key) ? $"{info.Label}: {info.Format(b)} (the new number shows after the next restart)" : $"{info.Label}: {info.Format(b)} (no change)"
                : $"{info.Label}: {info.Format(b)} → {info.Format(a)} ({Delta(info, a - b)})";
            changes.Add(new MetricChange(key, info.Label, b, a, text, improved, unchanged));
        }
        return changes;
    }

    /// <summary>The before numbers a run recorded (keys "before.*" in <see cref="RunRecord.Metrics"/>).</summary>
    public static Dictionary<string, double> BeforeOf(RunRecord run) => MetricKeys.Unprefixed(run.Metrics, MetricKeys.BeforePrefix);

    private static bool IsBoot(string key) => key is MetricKeys.BootSeconds or MetricKeys.BootToSignInSeconds;

    /// <summary>Tiny wobbles (a few MB of free space, a fraction of a percent of CPU) aren't a change.</summary>
    private static bool IsSame(MetricInfo info, double b, double a) => info.Unit switch
    {
        MetricUnit.Bytes => Math.Abs(a - b) < 1024 * 1024,
        MetricUnit.Megabytes => Math.Abs(a - b) < 1,
        MetricUnit.Percent => Math.Abs(a - b) < 0.5,
        MetricUnit.Seconds => Math.Abs(a - b) < 0.05,
        _ => Math.Abs(a - b) < 0.5,
    };

    private static string Delta(MetricInfo info, double d)
    {
        var sign = d > 0 ? "+" : "−";
        var magnitude = Math.Abs(d);
        return info.Unit == MetricUnit.Count
            ? sign + magnitude.ToString("N0", CultureInfo.CurrentCulture)
            : sign + info.Format(magnitude);
    }
}
