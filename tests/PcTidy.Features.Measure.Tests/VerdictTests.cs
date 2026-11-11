using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Import;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

public class VerdictTests
{
    private static List<RunSummary> Set(params (double Avg, double Low)[] runs) => runs.Select(r => Synth.Run(r.Avg, r.Low)).ToList();

    [Fact]
    public void Fewer_than_three_runs_each_asks_for_more()
    {
        var r = Comparer.Compare(Set((100, 70), (101, 71)), Set((110, 80), (111, 81), (112, 82)));
        Assert.Equal(Verdict.NeedMoreRuns, r.Verdict);
        Assert.Equal("Need at least 3 runs each", ComparisonResult.ChipText(r.Verdict));
        Assert.Contains("Need at least 3 runs each", r.Headline);
    }

    [Fact]
    public void A_clear_consistent_gain_is_Better()
    {
        var r = Comparer.Compare(Set((100, 70), (101, 71), (99, 69)), Set((110, 77), (111, 78), (109, 76)));
        Assert.Equal(Verdict.Better, r.Verdict);
        Assert.Equal(MetricVerdict.Better, r.Average!.Verdict);
        Assert.Contains("+10.0", r.Headline);
        Assert.Contains("on this PC", r.Headline);
        Assert.Contains("History", r.Suggestion);
    }

    [Fact]
    public void A_clear_consistent_loss_is_Worse()
    {
        var r = Comparer.Compare(Set((100, 70), (101, 71), (99, 69)), Set((90, 63), (91, 64), (89, 62)));
        Assert.Equal(Verdict.Worse, r.Verdict);
        Assert.Contains("Undo", r.Suggestion);
    }

    [Fact]
    public void A_change_under_the_two_percent_floor_is_no_measurable_change_with_the_numbers()
    {
        // Very tight runs (spread ~0.1%): the spread is taken as at least 1.5%, so the margin is 2 × 1.5% = 3%. +1.5% isn't enough.
        var r = Comparer.Compare(Set((100, 70), (100.1, 70), (99.9, 70)), Set((101.5, 70.5), (101.6, 70.5), (101.4, 70.5)));
        Assert.Equal(Verdict.NoMeasurableChange, r.Verdict);
        Assert.Contains("No measurable change", r.Headline);
        Assert.Contains("margin ±3.0", r.Headline);
    }

    [Fact]
    public void A_change_inside_twice_the_spread_is_no_measurable_change()
    {
        // Spread about 3 FPS: 2 × SD ≈ 6 FPS > the +4 FPS change, even though it is over 2%.
        var r = Comparer.Compare(Set((97, 70), (100, 70), (103, 70)), Set((101, 70), (104, 70), (107, 70)));
        Assert.Equal(MetricVerdict.NoChange, r.Average!.Verdict);
        Assert.True(r.Average.Margin > 4);
        Assert.Equal(Verdict.NoMeasurableChange, r.Verdict);
    }

    [Fact]
    public void Lows_need_more_than_five_percent_and_twice_their_spread_floor()
    {
        // Average flat; 1% low +4% (consistent, tight) → under the 5% floor.
        var r = Comparer.Compare(Set((100, 70), (100, 70.1), (100, 69.9)), Set((100, 72.8), (100, 72.9), (100, 72.7)));
        Assert.Equal(MetricVerdict.NoChange, r.OnePercentLow!.Verdict);
        // +7% is under 2 × the 4% spread floor for lows (8%); +9% is enough.
        Assert.Equal(MetricVerdict.NoChange, Comparer.Compare(Set((100, 70), (100, 70.1), (100, 69.9)), Set((100, 74.9), (100, 75), (100, 74.8))).OnePercentLow!.Verdict);
        var r2 = Comparer.Compare(Set((100, 70), (100, 70.1), (100, 69.9)), Set((100, 76.3), (100, 76.4), (100, 76.2)));
        Assert.Equal(MetricVerdict.Better, r2.OnePercentLow!.Verdict);
        Assert.Equal(Verdict.Better, r2.Verdict);
    }

    [Fact]
    public void One_pair_going_the_other_way_blocks_Better()
    {
        // Mean +8%, but run 3 After is below run 3 Before.
        var r = Comparer.Compare(Set((100, 70), (100, 70), (112, 78)), Set((115, 80), (114, 80), (111, 77)));
        Assert.False(r.Average!.SameDirectionInEveryPair);
        Assert.NotEqual(Verdict.Better, r.Verdict);
    }

    [Fact]
    public void A_set_with_runs_more_than_five_percent_apart_is_too_noisy()
    {
        var r = Comparer.Compare(Set((80, 60), (100, 70), (120, 80)), Set((130, 90), (131, 91), (129, 89)));
        Assert.Equal(Verdict.TooNoisy, r.Verdict);
        Assert.Equal("Too noisy to say", ComparisonResult.ChipText(r.Verdict));
        Assert.Contains(r.Reasons, x => x.Contains("more than 5%"));
    }

    [Fact]
    public void Average_and_lows_moving_opposite_ways_is_too_noisy()
    {
        var r = Comparer.Compare(Set((100, 70), (100.2, 70.1), (99.8, 69.9)), Set((110, 60), (110.2, 60.1), (109.8, 59.9)));
        Assert.Equal(Verdict.TooNoisy, r.Verdict);
        Assert.Contains(r.Reasons, x => x.Contains("opposite"));
    }

    [Fact]
    public void ABA_drift_makes_it_too_noisy_and_agreement_is_noted()
    {
        var before = Set((100, 70), (100.5, 70.2), (99.5, 69.8));
        var after = Set((110, 77), (110.5, 77.2), (109.5, 76.8));
        var drifted = Comparer.Compare(before, after, Set((108, 76), (108.5, 76.2), (107.5, 75.8)));
        Assert.Equal(Verdict.TooNoisy, drifted.Verdict);
        Assert.Contains("drifted", drifted.DriftNote);

        var steady = Comparer.Compare(before, after, Set((100.2, 70), (100.1, 70.1), (99.9, 69.9)));
        Assert.Equal(Verdict.Better, steady.Verdict);
        Assert.Contains("no drift", steady.DriftNote);

        Assert.Null(Comparer.Compare(before, after).DriftNote);
    }

    [Fact]
    public void Run_lengths_more_than_25_percent_apart_are_flagged_and_never_Better()
    {
        var before = new List<RunSummary> { Synth.Run(100, 70, 60), Synth.Run(100.5, 70.2, 60), Synth.Run(99.5, 69.8, 60) };
        var after = new List<RunSummary> { Synth.Run(110, 77, 90), Synth.Run(110.5, 77.2, 90), Synth.Run(109.5, 76.8, 90) };
        var r = Comparer.Compare(before, after);
        Assert.Contains(r.Flags, f => f.Contains("lengths differ"));
        Assert.Equal(Verdict.TooNoisy, r.Verdict);

        var ok = Comparer.UnlikeFlags(new[] { Synth.Run(100, 70, 80) }, new[] { Synth.Run(100, 70, 99) }, null);
        Assert.Empty(ok); // 99 / 80 = 1.24
    }

    [Fact]
    public void A_different_process_is_flagged()
    {
        var flags = Comparer.UnlikeFlags(new[] { Synth.Run(100, 70, process: "game.exe") }, new[] { Synth.Run(100, 70, process: "other.exe") }, null);
        Assert.Contains(flags, f => f.Contains("different processes"));
        // Case doesn't matter.
        Assert.Empty(Comparer.UnlikeFlags(new[] { Synth.Run(100, 70, process: "Game.exe") }, new[] { Synth.Run(100, 70, process: "game.EXE") }, null));
    }

    [Fact]
    public void Mixed_tools_are_noted()
    {
        var flags = Comparer.UnlikeFlags(new[] { Synth.Run(100, 70, format: RecordingFormat.PresentMonV1Csv) }, new[] { Synth.Run(100, 70, format: RecordingFormat.CapFrameXJson) }, null);
        Assert.Contains(flags, f => f.Contains("different tools"));
    }

    [Fact]
    public void The_rule_and_first_version_note_are_shown()
    {
        Assert.Equal("This rule is a first version; it hasn't been checked on many real PCs yet.", Comparer.FirstVersionNote);
        Assert.Contains("2× the run-to-run spread", Comparer.RuleText);
        Assert.Contains("5% for the 1% low", Comparer.RuleText);
    }

    [Fact]
    public void Display_timing_is_used_only_when_every_run_has_it()
    {
        var withDisplay = Synth.Run(100, 70) with { Display = Synth.Run(90, 60).Presents };
        var r = Comparer.Compare(new[] { withDisplay, withDisplay, withDisplay }, new[] { withDisplay, withDisplay, withDisplay });
        Assert.Equal(FrameTimeBasis.Display, r.Basis);
        Assert.Equal(90, r.Average!.BeforeMean, 6);

        var mixed = Comparer.Compare(new[] { withDisplay, withDisplay, withDisplay }, Set((100, 70), (100, 70), (100, 70)));
        Assert.Equal(FrameTimeBasis.Presents, mixed.Basis);
        Assert.Contains(mixed.Reasons, x => x.Contains("no on-screen timing"));
    }
}

public class VerdictFloorTests
{
    [Fact]
    public void The_two_percent_floor_decides_when_the_spread_floor_is_lower()
    {
        // 2 × 1.5% = 3% > 2%, so with tight runs a +2.5% change is still no change and +3.5% is Better.
        List<RunSummary> S(params double[] avg) => avg.Select(a => Synth.Run(a, 70)).ToList();
        Assert.Equal(MetricVerdict.NoChange, Comparer.Compare(S(100, 100.1, 99.9), S(102.5, 102.6, 102.4)).Average!.Verdict);
        Assert.Equal(MetricVerdict.Better, Comparer.Compare(S(100, 100.1, 99.9), S(103.5, 103.6, 103.4)).Average!.Verdict);
    }
}
