using System.Globalization;

namespace PcTidy.Features.Schedule.Metrics;

/// <summary>How a metric reads and which direction is "better".</summary>
public enum MetricUnit { Bytes, Count, Percent, Megabytes, Seconds }

public sealed record MetricInfo(string Key, string Label, MetricUnit Unit, bool HigherIsBetter)
{
    public string Format(double value) => Unit switch
    {
        MetricUnit.Bytes => Sizes.Format((long)value),
        MetricUnit.Count => value.ToString("N0", CultureInfo.CurrentCulture),
        MetricUnit.Percent => value.ToString("0.#", CultureInfo.CurrentCulture) + " %",
        MetricUnit.Megabytes => Sizes.Format((long)(value * 1024 * 1024)),
        MetricUnit.Seconds => value < 90 ? value.ToString("0.#", CultureInfo.CurrentCulture) + " s" : $"{(int)(value / 60)} min {(int)(value % 60)} s",
        _ => value.ToString(CultureInfo.CurrentCulture),
    };
}

/// <summary>
/// Names of the before/after numbers. A sample is a plain <c>Dictionary&lt;string,double&gt;</c> with these keys;
/// <see cref="Prefixed"/> adds "before." / "after." for <see cref="ExecuteOptions.MetricsBefore"/> and
/// <see cref="RunRecord.Metrics"/> (e.g. "before.freeC"). A key that couldn't be measured is simply absent.
/// </summary>
public static class MetricKeys
{
    public const string BeforePrefix = "before.";
    public const string AfterPrefix = "after.";

    /// <summary>"freeC", "freeD"… bytes free on each fixed drive.</summary>
    public const string FreePrefix = "free";
    public const string StartupItems = "startupItems";
    public const string Processes = "processes";
    public const string IdleCpuPercent = "idleCpuPercent";
    public const string AvailableMemoryMB = "availableMemoryMB";
    /// <summary>OS start → user sign-in, median of the last 3 boots of the same type (System log, no admin).</summary>
    public const string BootToSignInSeconds = "bootToSignInSec";
    /// <summary>Exact boot time (Diagnostics-Performance event 100), median of the last 3; only when readable (admin).</summary>
    public const string BootSeconds = "bootSec";
    /// <summary>0 = cold boot, 1 = Fast Startup, 2 = resume from hibernation (type of the boots the medians used).</summary>
    public const string BootType = "bootType";

    public static string FreeKey(char driveLetter) => FreePrefix + char.ToUpperInvariant(driveLetter);

    public static bool IsFreeKey(string key)
        => key.Length == FreePrefix.Length + 1 && key.StartsWith(FreePrefix, StringComparison.Ordinal) && char.IsLetter(key[^1]);

    public static MetricInfo? Describe(string key)
    {
        if (IsFreeKey(key)) return new MetricInfo(key, $"{key[^1]}: free space", MetricUnit.Bytes, HigherIsBetter: true);
        return key switch
        {
            StartupItems => new MetricInfo(key, "Startup items", MetricUnit.Count, false),
            Processes => new MetricInfo(key, "Background processes", MetricUnit.Count, false),
            IdleCpuPercent => new MetricInfo(key, "CPU when idle", MetricUnit.Percent, false),
            AvailableMemoryMB => new MetricInfo(key, "Free memory", MetricUnit.Megabytes, true),
            BootToSignInSeconds => new MetricInfo(key, "Start-up to sign-in", MetricUnit.Seconds, false),
            BootSeconds => new MetricInfo(key, "Boot time", MetricUnit.Seconds, false),
            _ => null,
        };
    }

    /// <summary>Keys in a sensible display order (drives first, then the rest), only those present.</summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> keys)
    {
        var order = new[] { BootSeconds, BootToSignInSeconds, StartupItems, Processes, IdleCpuPercent, AvailableMemoryMB };
        var list = keys.Distinct().ToList();
        return list.Where(IsFreeKey).OrderBy(k => k, StringComparer.Ordinal)
            .Concat(order.Where(list.Contains))
            .ToList();
    }

    public static Dictionary<string, double> Prefixed(IReadOnlyDictionary<string, double> sample, string prefix)
        => sample.ToDictionary(kv => prefix + kv.Key, kv => kv.Value);

    /// <summary>The un-prefixed values with that prefix (e.g. "before.*" of a run's metrics).</summary>
    public static Dictionary<string, double> Unprefixed(IReadOnlyDictionary<string, double>? metrics, string prefix)
        => metrics is null
            ? new Dictionary<string, double>()
            : metrics.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value);
}
