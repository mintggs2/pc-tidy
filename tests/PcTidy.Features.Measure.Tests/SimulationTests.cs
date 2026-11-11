using PcTidy.Features.Measure.Compare;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Measure.Tests;

/// <summary>
/// Seeded simulation of the verdict rule with invented frame data: each run is 6,000 frames (about 60 s at 100 FPS) with
/// per-frame jitter, a few hitches, and a run-to-run offset of 1.5% (the low end of the 1-3% run-to-run noise in the
/// research). 200 A-vs-A pairs from one distribution must rarely say Better/Worse; a real 10% speed-up must be found.
/// </summary>
public class SimulationTests
{
    private const int Pairs = 200;
    private const int RunsPerSet = 3;
    private const int FramesPerRun = 6000;
    private const double RunToRunSd = 0.015;
    private readonly ITestOutputHelper _out;

    public SimulationTests(ITestOutputHelper output) => _out = output;

    private static List<RunSummary> SimSet(Random rng, double meanMs)
    {
        var set = new List<RunSummary>();
        for (int i = 0; i < RunsPerSet; i++)
        {
            double runMean = meanMs * Math.Exp(RunToRunSd * Synth.Gaussian(rng));
            set.Add(Synth.FromFrames(Synth.Frames(FramesPerRun, runMean, rng.Next(), jitter: 0.10, hitchChance: 0.0002)));
        }
        return set;
    }

    [ThreadStatic] private static int AverageCalls;
    [ThreadStatic] private static int LowCalls;

    private static Dictionary<Verdict, int> Simulate(int seed, double afterSpeedUp)
    {
        var rng = new Random(seed);
        var counts = Enum.GetValues<Verdict>().ToDictionary(v => v, _ => 0);
        for (int p = 0; p < Pairs; p++)
        {
            var before = SimSet(rng, 10.0);
            var after = SimSet(rng, 10.0 / afterSpeedUp);
            var r = Comparer.Compare(before, after);
            counts[r.Verdict]++;
            if (r.Average!.Verdict != MetricVerdict.NoChange) AverageCalls++;
            if (r.OnePercentLow!.Verdict != MetricVerdict.NoChange) LowCalls++;
        }
        return counts;
    }

    [Theory]
    [InlineData(20261110)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_vs_A_null_pairs_give_a_false_Better_or_Worse_under_5_percent(int seed)
    {
        AverageCalls = LowCalls = 0;
        var counts = Simulate(seed, afterSpeedUp: 1.0);
        _out.WriteLine(string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine($"metric calls: average {AverageCalls}, 1% low {LowCalls}");
        int wrong = counts[Verdict.Better] + counts[Verdict.Worse];
        Assert.True(wrong < Pairs * 0.05, $"false Better/Worse in {wrong} of {Pairs} A-vs-A pairs");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void A_clear_10_percent_speed_up_is_found_at_least_90_percent_of_the_time(int seed)
    {
        var counts = Simulate(seed, afterSpeedUp: 1.10);
        _out.WriteLine(string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.True(counts[Verdict.Better] >= Pairs * 0.90, $"Better in only {counts[Verdict.Better]} of {Pairs}");
        Assert.Equal(0, counts[Verdict.Worse]);
    }

    [Fact]
    public void A_clear_10_percent_slow_down_is_found_as_Worse()
    {
        var counts = Simulate(seed: 11, afterSpeedUp: 1 / 1.10);
        Assert.True(counts[Verdict.Worse] >= Pairs * 0.90, $"Worse in only {counts[Verdict.Worse]} of {Pairs}");
    }
}
