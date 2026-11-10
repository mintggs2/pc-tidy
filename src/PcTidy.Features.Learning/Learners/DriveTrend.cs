using PcTidy.Core;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>How fast a drive fills, from daily free-space samples.</summary>
/// <param name="BytesPerDay">Robust growth of used space per day (negative = it is emptying).</param>
/// <param name="DaysUntilFull">"Full in about N days"; only with ≥14 samples and a real upward trend.</param>
public sealed record DriveForecast(string Drive, int Samples, double BytesPerDay, int? DaysUntilFull, long FreeBytes, DateTime FirstUtc, string Reason)
{
    public string Id => "drive:" + Drive.ToLowerInvariant();
}

/// <summary>
/// Drive fill trend: Theil–Sen slope (median of all pairwise slopes, so a few odd days can't swing it) of used space
/// over the last 90 days. Step changes across a PC Tidy run (a cleanup, a move to another drive) are taken out first,
/// so PC Tidy's own work never looks like the user's trend. Pure.
/// </summary>
public static class DriveTrend
{
    /// <summary>Below this growth a drive counts as steady (1 MB a day).</summary>
    public const double SteadyBytesPerDay = 1024 * 1024;
    /// <summary>Further out than this, "full in N days" means nothing.</summary>
    public const int MaxForecastDays = 3650;

    public static DriveForecast Evaluate(string drive, IEnumerable<FreeSpaceSample> samples, IEnumerable<RunMark> runs, DateTime nowUtc)
    {
        var from = nowUtc - LearningRules.DriveTrendWindow;
        var list = samples.Where(s => s.Utc >= from && s.Utc <= nowUtc && s.TotalBytes > 0).OrderBy(s => s.Utc).ToList();
        var label = drive.TrimEnd('\\');
        if (list.Count == 0)
            return new DriveForecast(drive, 0, 0, null, 0, nowUtc, $"No free-space readings for {label} yet.");
        var latest = list[^1];
        if (list.Count < LearningRules.MinDriveSamples)
            return new DriveForecast(drive, list.Count, 0, null, latest.FreeBytes, list[0].Utc,
                $"Still learning: {list.Count} of {LearningRules.MinDriveSamples} daily readings so far.");

        var adjusted = WithoutRunJumps(list, runs.Where(r => r.Touches(drive)).ToList());
        var x = list.Select(s => (s.Utc - list[0].Utc).TotalDays).ToArray();
        double slope = TheilSen(x, adjusted);

        int? days = null;
        string reason;
        if (slope > SteadyBytesPerDay)
        {
            var d = latest.FreeBytes / slope;
            if (d <= MaxForecastDays) days = Math.Max(1, (int)Math.Round(d));
            reason = days is { } n
                ? $"{label} is filling by about {Sizes.Format((long)slope)} a day: full in about {n} days at this rate."
                : $"{label} is filling slowly ({Sizes.Format((long)slope)} a day): no need to worry.";
        }
        else if (slope < -SteadyBytesPerDay) reason = $"{label} is slowly getting emptier.";
        else reason = $"{label} is steady: it isn't filling up.";
        return new DriveForecast(drive, list.Count, slope, days, latest.FreeBytes, list[0].Utc, reason);
    }

    /// <summary>
    /// Used space with PC Tidy's own jumps taken out: across every interval that contains a PC Tidy run, the change is
    /// replaced by the drive's typical daily change (median of the intervals without a run) for that length of time.
    /// </summary>
    public static double[] WithoutRunJumps(IReadOnlyList<FreeSpaceSample> ordered, IReadOnlyList<RunMark> runs)
    {
        bool HasRun(int i) => runs.Any(r => r.Utc > ordered[i - 1].Utc && r.Utc <= ordered[i].Utc);
        double Days(int i) => Math.Max(1e-6, (ordered[i].Utc - ordered[i - 1].Utc).TotalDays);
        double Step(int i) => ordered[i].UsedBytes - ordered[i - 1].UsedBytes;

        var cleanRates = Enumerable.Range(1, Math.Max(0, ordered.Count - 1)).Where(i => !HasRun(i)).Select(i => Step(i) / Days(i)).ToList();
        double typical = Median(cleanRates);
        var y = new double[ordered.Count];
        double offset = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && HasRun(i)) offset += Step(i) - typical * Days(i);
            y[i] = ordered[i].UsedBytes - offset;
        }
        return y;
    }

    /// <summary>Theil–Sen estimator: the median of the slopes between every pair of points with different x.</summary>
    public static double TheilSen(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var slopes = new List<double>(x.Count * (x.Count - 1) / 2);
        for (int i = 0; i < x.Count; i++)
            for (int j = i + 1; j < x.Count; j++)
            {
                var dx = x[j] - x[i];
                if (Math.Abs(dx) > 1e-9) slopes.Add((y[j] - y[i]) / dx);
            }
        return Median(slopes);
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
