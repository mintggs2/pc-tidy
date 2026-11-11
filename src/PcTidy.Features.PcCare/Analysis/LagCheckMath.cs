namespace PcTidy.Features.PcCare.Analysis;

/// <summary>Latency of one phase of the lag check (idle, while downloading, while uploading).</summary>
/// <param name="MedianMs">Median answer time of the answered samples.</param>
/// <param name="JitterMs">Mean difference between consecutive answered samples (how much the delay wobbles).</param>
/// <param name="Answered">Samples that got an answer in time.</param>
/// <param name="Asked">Samples sent.</param>
public sealed record LatencyStats(double MedianMs, double JitterMs, int Answered, int Asked);

/// <summary>A grade band: PC Tidy's own words, not an industry standard.</summary>
public sealed record LagGrade(string Key, string Title, string Meaning);

/// <summary>Pure math for the lag check: median, jitter, added latency and PC Tidy's grade bands.</summary>
public static class LagCheckMath
{
    /// <summary>Fewer answered samples than this and a phase isn't graded.</summary>
    public const int MinSamples = 3;

    /// <summary>Stats over samples in the order taken; null entries are samples that got no answer. Null when too few answered.</summary>
    public static LatencyStats? Stats(IReadOnlyList<double?> samples)
    {
        var answered = samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (answered.Count < MinSamples) return null;
        double jitter = 0;
        for (int i = 1; i < answered.Count; i++) jitter += Math.Abs(answered[i] - answered[i - 1]);
        jitter /= answered.Count - 1;
        return new LatencyStats(Median(answered), jitter, answered.Count, samples.Count);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No values.", nameof(values));
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>How much the busier of the two loaded phases adds on top of idle (never below 0). Null when nothing to compare.</summary>
    public static double? AddedMs(LatencyStats? idle, LatencyStats? download, LatencyStats? upload)
    {
        if (idle is null) return null;
        var loaded = new[] { download?.MedianMs, upload?.MedianMs }.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (loaded.Count == 0) return null;
        return Math.Max(0, loaded.Max() - idle.MedianMs);
    }

    // PC Tidy's own grade bands for the delay a full line adds (the median under load minus the idle median).
    // They are NOT an industry standard and NOT copied from another test. The reasoning: under ~10 ms added is
    // within normal idle wobble for home links; up to ~30 ms is usually unnoticeable in calls and games; 30-100 ms
    // is enough to feel in fast games and video calls; over 100 ms is the kind of bufferbloat SQM/CAKE fixes.
    public const double VeryLittleBelowMs = 10;
    public const double SmallBelowMs = 30;
    public const double NoticeableBelowMs = 100;

    public static readonly LagGrade VeryLittle = new("very-little", "Very little lag", "A full line barely adds any delay. Calls and games shouldn't notice.");
    public static readonly LagGrade Small = new("small", "Small lag", "A full line adds a little delay. Most calls and games won't notice.");
    public static readonly LagGrade Noticeable = new("noticeable", "Noticeable lag", "A full line adds enough delay to feel in fast games and video calls. See the tips below.");
    public static readonly LagGrade Heavy = new("heavy", "Heavy lag", "A full line adds a lot of delay (bufferbloat). Router SQM or a wired link usually helps most.");
    public static readonly LagGrade Unknown = new("unknown", "Couldn't tell", "Too few answers under load to grade. Try again, ideally when nothing else is downloading.");

    public static LagGrade Grade(double? addedMs) => addedMs switch
    {
        null => Unknown,
        < VeryLittleBelowMs => VeryLittle,
        < SmallBelowMs => Small,
        < NoticeableBelowMs => Noticeable,
        _ => Heavy,
    };

    /// <summary>The bands in words, for the UI ("PC Tidy's own bands: …").</summary>
    public static string BandsText =>
        $"PC Tidy's own bands for the added delay: under {VeryLittleBelowMs:0} ms very little, {VeryLittleBelowMs:0}-{SmallBelowMs:0} ms small, "
        + $"{SmallBelowMs:0}-{NoticeableBelowMs:0} ms noticeable, {NoticeableBelowMs:0} ms or more heavy. Not an industry standard.";
}
