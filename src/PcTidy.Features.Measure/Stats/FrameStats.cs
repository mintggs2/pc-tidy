namespace PcTidy.Features.Measure.Stats;

/// <summary>Numbers for one run. Every definition is shown on the page (<see cref="Definitions"/>).</summary>
public sealed record RunStats
{
    public int Frames { get; init; }
    /// <summary>Sum of all frame times, in seconds.</summary>
    public double Seconds { get; init; }
    /// <summary>Frames ÷ total time (not the mean of per-frame FPS, which short frames inflate).</summary>
    public double AverageFps { get; init; }
    /// <summary>1000 ÷ the average frame time of the slowest 1% of frames.</summary>
    public double OnePercentLowFps { get; init; }
    /// <summary>Same for the slowest 0.1%. Shown only when <see cref="PointOnePercentLowIsReliable"/>; never a verdict input.</summary>
    public double PointOnePercentLowFps { get; init; }
    public double MedianFrameMs { get; init; }
    /// <summary>99th percentile frame time (nearest rank): 99% of frames were this fast or faster.</summary>
    public double P99FrameMs { get; init; }
    /// <summary>Frames longer than 2 × this run's median and longer than <see cref="FrameStats.StutterMinMs"/>.</summary>
    public int Stutters { get; init; }

    public double StuttersPerMinute => Seconds > 0 ? Stutters / (Seconds / 60.0) : 0;
    public bool PointOnePercentLowIsReliable => Frames >= FrameStats.PointOnePercentMinFrames;
}

/// <summary>
/// The statistics engine. Definitions (also in <see cref="Definitions"/> for the page):
///   Average FPS = frames ÷ total time.
///   x% low = 1000 ÷ mean frame time of the slowest x% of frames (at least one frame); "x% low average" in CapFrameX's words.
///   P99 frame time = nearest-rank 99th percentile of frame times.
///   Stutter = a frame longer than 2 × the run's median frame time AND longer than 25 ms.
/// </summary>
public static class FrameStats
{
    public const double StutterMedianFactor = 2.0;
    public const double StutterMinMs = 25.0;
    /// <summary>Below this many frames the slowest 0.1% is only a handful of frames: too few to trust.</summary>
    public const int PointOnePercentMinFrames = 10_000;
    /// <summary>
    /// Shortest frame time a reader keeps, in ms (100,000 FPS). Anything shorter is a damaged row: it would round to 0 as a
    /// float and make the FPS infinite.
    /// </summary>
    public const double MinFrameMs = 0.01;
    /// <summary>Longest frame time a reader keeps, in ms (a minute): above that is a broken row or a paused capture.</summary>
    public const double MaxFrameMs = 60_000;

    /// <summary>A frame time a reader may keep: finite, and between <see cref="MinFrameMs"/> and <see cref="MaxFrameMs"/> after the float cast.</summary>
    public static bool IsUsableFrameMs(double ms)
    {
        float f = (float)ms;
        return float.IsFinite(f) && f >= (float)MinFrameMs && f <= (float)MaxFrameMs;
    }

    public static class Definitions
    {
        public const string AverageFps = "Average FPS: frames ÷ total time of the run.";
        public const string OnePercentLow = "1% low: the slowest 1% of frames, as FPS (1000 ÷ their average frame time).";
        public const string PointOnePercentLow = "0.1% low: the same for the slowest 0.1%. Shown only for runs of 10,000 frames or more, and never used for the verdict.";
        public const string P99 = "P99 frame time: 99% of frames took this long or less.";
        public static readonly string Stutter = $"Stutter: a frame that took more than {StutterMedianFactor:0}× the run's median frame time and more than {StutterMinMs:0} ms.";
        public const string Spread = "Spread: how much the runs of one set differ from each other (standard deviation of their averages, as % of the mean).";
    }

    public static RunStats Compute(ReadOnlySpan<float> frameMs)
    {
        int n = frameMs.Length;
        if (n == 0) return new RunStats();
        var sorted = frameMs.ToArray();
        Array.Sort(sorted);
        double total = 0;
        foreach (var f in sorted) total += f;
        // Never infinite or NaN numbers (they mean nothing and can't be saved as JSON): no usable time, no stats.
        if (!(total > 0) || !double.IsFinite(total)) return new RunStats();

        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + (double)sorted[n / 2]) / 2.0;
        double stutterLine = Math.Max(StutterMedianFactor * median, StutterMinMs);
        int stutters = 0;
        for (int i = n - 1; i >= 0 && sorted[i] > stutterLine; i--) stutters++;

        return new RunStats
        {
            Frames = n,
            Seconds = total / 1000.0,
            AverageFps = n * 1000.0 / total,
            OnePercentLowFps = SlowestFps(sorted, 0.01),
            PointOnePercentLowFps = SlowestFps(sorted, 0.001),
            MedianFrameMs = median,
            P99FrameMs = Percentile(sorted, 0.99),
            Stutters = stutters,
        };
    }

    /// <summary>Nearest rank: the smallest value with at least p of the frames at or below it.</summary>
    public static double Percentile(float[] sortedAscending, double p)
    {
        int n = sortedAscending.Length;
        if (n == 0) return 0;
        int rank = (int)Math.Ceiling(p * n);
        return sortedAscending[Math.Clamp(rank, 1, n) - 1];
    }

    /// <summary>1000 ÷ mean of the slowest <paramref name="fraction"/> of frames (at least one frame).</summary>
    public static double SlowestFps(float[] sortedAscending, double fraction)
    {
        int n = sortedAscending.Length;
        if (n == 0) return 0;
        int k = Math.Max(1, (int)Math.Floor(n * fraction));
        double sum = 0;
        for (int i = n - k; i < n; i++) sum += sortedAscending[i];
        return sum > 0 && double.IsFinite(sum) ? k * 1000.0 / sum : 0;
    }

    /// <summary>Sample standard deviation (n − 1).</summary>
    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        double ss = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(ss / (values.Count - 1));
    }

    /// <summary>Run-to-run spread: standard deviation of the run values as % of their mean.</summary>
    public static double SpreadPercent(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        return mean > 0 ? StdDev(values) / mean * 100.0 : 0;
    }
}
