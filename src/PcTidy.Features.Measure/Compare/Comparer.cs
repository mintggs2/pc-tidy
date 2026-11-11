using System.Globalization;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Stats;

namespace PcTidy.Features.Measure.Compare;

/// <summary>
/// The "within noise" rule (first version, from the F8 research; not yet checked on many real PCs):
///  * At least <see cref="MinRuns"/> runs in Before and in After, else "Need at least 3 runs each".
///  * Either set's run-to-run spread above <see cref="MaxAverageSpreadPercent"/> (average FPS) or
///    <see cref="MaxLowSpreadPercent"/> (1% low) → "Too noisy to say".
///  * A metric is Better/Worse only when |After − Before| is bigger than BOTH 2 × the run-to-run spread AND a floor (2% of
///    Before for average FPS, 5% for the 1% low), and every pair moved the same way (each Before run against each After run).
///    The spread is the pooled standard deviation of the runs, but at least 1.5% of Before for average FPS and 4% for the 1% low
///    (F8 §3.3 uses s = max(pooled SD, 1.5%); lows are 2-4× noisier than averages, F8 §3.2). Three runs often look tighter than
///    the PC really is: without these floors, seeded A-vs-A pairs said Better/Worse in about 8% of cases, and with a 3% floor for lows about 5% (see SimulationTests).
///  * The verdict uses average FPS and the 1% low. Both moving opposite ways → "Too noisy to say". Otherwise any Better → Better,
///    any Worse → Worse, else "No measurable change".
///  * A "Before again" set (A/B/A) that differs from Before by more than the same margin → the PC drifted → "Too noisy to say".
///  * Unlike runs (lengths more than 25% apart, a different process) are flagged and never get Better/Worse.
/// </summary>
public static class Comparer
{
    public const int MinRuns = 3;
    public const double SpreadFactor = 2.0;
    /// <summary>Smallest run-to-run spread assumed for average FPS, as % of the Before mean.</summary>
    public const double MinAverageSpreadPercent = 1.5;
    /// <summary>Smallest run-to-run spread assumed for the 1% low, as % of the Before mean.</summary>
    public const double MinLowSpreadPercent = 4.0;
    public const double AverageFloorPercent = 2.0;
    public const double LowFloorPercent = 5.0;
    public const double MaxAverageSpreadPercent = 5.0;
    public const double MaxLowSpreadPercent = 10.0;
    /// <summary>Longest run may be at most this much longer than the shortest (1.25 = 25%).</summary>
    public const double MaxLengthRatio = 1.25;

    public const string FirstVersionNote = "This rule is a first version; it hasn't been checked on many real PCs yet.";

    public static string RuleText =>
        $"Better or Worse only when the change is bigger than both {SpreadFactor:0}× the run-to-run spread (taken as at least {MinAverageSpreadPercent:0.0}%, {MinLowSpreadPercent:0}% for the 1% low) and {AverageFloorPercent:0}% " +
        $"({LowFloorPercent:0}% for the 1% low), and every Before/After pair moved the same way. Otherwise: No measurable change, " +
        $"or Too noisy to say when runs of one set differ by more than {MaxAverageSpreadPercent:0}%.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ComparisonResult Compare(IReadOnlyList<RunSummary> before, IReadOnlyList<RunSummary> after, IReadOnlyList<RunSummary>? beforeAgain = null)
    {
        var flags = UnlikeFlags(before, after, beforeAgain);
        var all = before.Concat(after).Concat(beforeAgain ?? Array.Empty<RunSummary>()).ToList();
        var basis = BasisFor(all);
        if (before.Count < MinRuns || after.Count < MinRuns)
        {
            return new ComparisonResult
            {
                Verdict = Verdict.NeedMoreRuns,
                Headline = $"Need at least {MinRuns} runs each (Before has {before.Count}, After has {after.Count}).",
                Basis = basis, // the columns already use the basis the verdict will use
                Flags = flags,
                Suggestion = "Record more runs of the same scene, then import them.",
            };
        }

        var reasons = new List<string>();
        if (basis == FrameTimeBasis.Presents && all.Any(r => r.Display is not null))
            reasons.Add("Some files have no on-screen timing, so every run is compared on the time between Present calls.");

        var avg = CompareMetric("Average FPS", before, after, basis, s => s.AverageFps, AverageFloorPercent, MinAverageSpreadPercent);
        var low = CompareMetric("1% low", before, after, basis, s => s.OnePercentLowFps, LowFloorPercent, MinLowSpreadPercent);

        Verdict verdict;
        bool noisy = false;
        foreach (var (m, limit) in new[] { (avg, MaxAverageSpreadPercent), (low, MaxLowSpreadPercent) })
        {
            if (m.BeforeSpreadPercent > limit || m.AfterSpreadPercent > limit)
            {
                noisy = true;
                reasons.Add($"{m.Name}: runs differ by {Pct(Math.Max(m.BeforeSpreadPercent, m.AfterSpreadPercent))} within one set (more than {limit:0}%).");
            }
        }

        string? drift = null;
        if (beforeAgain is { Count: > 0 })
        {
            var again = CompareMetric("Average FPS", before, beforeAgain, basis, s => s.AverageFps, AverageFloorPercent, MinAverageSpreadPercent);
            if (Math.Abs(again.Difference) > again.Margin)
            {
                drift = $"Before and Before again differ by {Signed(again.DifferencePercent)}% (more than the margin of ±{again.Margin.ToString("0.0", Inv)} FPS): the PC drifted (heat, updates, shader cache). The result can't be trusted.";
                noisy = true;
                reasons.Add("The A/B/A check found drift.");
            }
            else
            {
                drift = $"Before and Before again agree ({Signed(again.DifferencePercent)}%, within ±{again.Margin.ToString("0.0", Inv)} FPS): no drift found.";
            }
        }

        if (noisy) verdict = Verdict.TooNoisy;
        else if ((avg.Verdict == MetricVerdict.Better && low.Verdict == MetricVerdict.Worse) || (avg.Verdict == MetricVerdict.Worse && low.Verdict == MetricVerdict.Better))
        {
            verdict = Verdict.TooNoisy;
            reasons.Add("Average FPS and the 1% low moved opposite ways.");
        }
        else if (avg.Verdict == MetricVerdict.Better || low.Verdict == MetricVerdict.Better) verdict = Verdict.Better;
        else if (avg.Verdict == MetricVerdict.Worse || low.Verdict == MetricVerdict.Worse) verdict = Verdict.Worse;
        else verdict = Verdict.NoMeasurableChange;

        if (flags.Count > 0 && verdict is Verdict.Better or Verdict.Worse)
        {
            reasons.Add("Some runs aren't alike (see the warnings), so PC Tidy won't call it Better or Worse.");
            verdict = Verdict.TooNoisy;
        }

        return new ComparisonResult
        {
            Verdict = verdict,
            Headline = Headline(verdict, avg, low),
            Reasons = reasons,
            Flags = flags,
            DriftNote = drift,
            Basis = basis,
            Average = avg,
            OnePercentLow = low,
            Suggestion = Suggest(verdict),
        };
    }

    /// <summary>
    /// The frame-time basis for a set of runs: on-screen timing only when every run has it, else the time between Present
    /// calls for all of them (never a mix). The verdict, the Before / After columns and every run line use it.
    /// </summary>
    public static FrameTimeBasis BasisFor(IEnumerable<RunSummary> runs)
    {
        var list = runs as IReadOnlyCollection<RunSummary> ?? runs.ToList();
        return list.Count > 0 && list.All(r => r.Display is not null) ? FrameTimeBasis.Display : FrameTimeBasis.Presents;
    }

    internal static MetricComparison CompareMetric(string name, IReadOnlyList<RunSummary> a, IReadOnlyList<RunSummary> b, FrameTimeBasis basis, Func<RunStats, double> pick, double floorPercent, double minSpreadPercent)
    {
        var va = a.Select(r => pick(r.StatsFor(basis))).ToList();
        var vb = b.Select(r => pick(r.StatsFor(basis))).ToList();
        double ma = va.Average(), mb = vb.Average();
        double sp = PooledSd(va, vb);
        double s = Math.Max(sp, minSpreadPercent / 100.0 * ma);
        double margin = Math.Max(SpreadFactor * s, floorPercent / 100.0 * ma);
        double d = mb - ma;
        // Every pair: each Before run against each After run (so every After run beats every Before run, or the reverse).
        bool allUp = true, allDown = true;
        foreach (var x in va)
            foreach (var y in vb)
            {
                double pd = y - x;
                if (!(pd > 0)) allUp = false;
                if (!(pd < 0)) allDown = false;
            }
        bool same = va.Count > 0 && vb.Count > 0 && (d > 0 ? allUp : allDown);
        var verdict = Math.Abs(d) > margin && same ? (d > 0 ? MetricVerdict.Better : MetricVerdict.Worse) : MetricVerdict.NoChange;
        return new MetricComparison
        {
            Name = name,
            BeforeMean = ma,
            AfterMean = mb,
            Margin = margin,
            FloorPercent = floorPercent,
            PooledSd = sp,
            BeforeSpreadPercent = FrameStats.SpreadPercent(va),
            AfterSpreadPercent = FrameStats.SpreadPercent(vb),
            SameDirectionInEveryPair = same,
            Verdict = verdict,
        };
    }

    /// <summary>Pooled sample standard deviation of two sets (sets of one run add nothing).</summary>
    internal static double PooledSd(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double ss = 0;
        int df = 0;
        foreach (var set in new[] { a, b })
        {
            if (set.Count < 2) continue;
            double m = set.Average();
            ss += set.Sum(v => (v - m) * (v - m));
            df += set.Count - 1;
        }
        return df > 0 ? Math.Sqrt(ss / df) : 0;
    }

    /// <summary>Runs that shouldn't be compared: lengths more than 25% apart, another process, another tool or version.</summary>
    public static IReadOnlyList<string> UnlikeFlags(IReadOnlyList<RunSummary> before, IReadOnlyList<RunSummary> after, IReadOnlyList<RunSummary>? again)
    {
        var flags = new List<string>();
        var all = before.Concat(after).Concat(again ?? Array.Empty<RunSummary>()).Where(r => r.Presents.Seconds > 0).ToList();
        if (all.Count < 2) return flags;

        double shortest = all.Min(r => r.Presents.Seconds), longest = all.Max(r => r.Presents.Seconds);
        if (longest > shortest * MaxLengthRatio)
            flags.Add($"Run lengths differ by more than {(MaxLengthRatio - 1) * 100:0}% ({shortest.ToString("0", Inv)} s to {longest.ToString("0", Inv)} s). Record runs of the same length.");

        var processes = all.Select(r => r.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (processes.Count > 1)
            flags.Add($"The runs come from different processes ({string.Join(", ", processes.Take(4))}). Compare the same game only.");

        var formats = all.Select(r => r.Format).Distinct().ToList();
        if (formats.Count > 1)
            flags.Add("The runs were recorded with different tools or tool versions. Their numbers may not match exactly.");
        return flags;
    }

    private static string Headline(Verdict v, MetricComparison avg, MetricComparison low)
    {
        string a = $"average FPS {Signed(avg.Difference)} ({Signed(avg.DifferencePercent)}%), margin ±{avg.Margin.ToString("0.0", Inv)}";
        string l = $"1% low {Signed(low.Difference)} ({Signed(low.DifferencePercent)}%), margin ±{low.Margin.ToString("0.0", Inv)}";
        return v switch
        {
            Verdict.Better => $"Better on this PC, in these runs: {a}; {l}.",
            Verdict.Worse => $"Worse on this PC, in these runs: {a}; {l}.",
            Verdict.NoMeasurableChange => $"No measurable change: {a}; {l}.",
            _ => $"Too noisy to say: {a}; {l}.",
        };
    }

    private static string Suggest(Verdict v) => v switch
    {
        Verdict.Better => "Keep: if you want to keep the change, do nothing. It stays in History, where you can undo it later.",
        Verdict.Worse => "Undo: open History and undo the run that made the change.",
        Verdict.NoMeasurableChange => "Nothing measurable was gained. If you'd rather not keep it, undo it from History. \"No change\" doesn't mean it's harmless.",
        Verdict.TooNoisy => "Record again: longer runs of the same repeatable scene, with a warm-up run first. Keep or undo in History as you prefer.",
        _ => "Record more runs of the same scene, then import them.",
    };

    internal static string Signed(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.0", Inv);
    private static string Pct(double v) => v.ToString("0.0", Inv) + "%";
}
