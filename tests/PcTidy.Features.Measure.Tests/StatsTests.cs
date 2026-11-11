using PcTidy.Features.Measure.Stats;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

public class StatsTests
{
    [Fact]
    public void Average_fps_is_frames_over_total_time_not_the_mean_of_per_frame_fps()
    {
        // One 10 ms frame and one 30 ms frame: 2 frames in 40 ms = 50 FPS. The mean of per-frame FPS would be (100 + 33.3) / 2 = 66.7.
        var s = FrameStats.Compute(new float[] { 10, 30 });
        Assert.Equal(50.0, s.AverageFps, 6);
        Assert.Equal(0.04, s.Seconds, 6);
    }

    [Fact]
    public void One_percent_low_is_1000_over_the_mean_of_the_slowest_one_percent()
    {
        // 1000 frames: 990 at 10 ms and 10 at 20, 30, ..., 110 ms. Slowest 1% = those 10, mean 65 ms → 15.38 FPS.
        var frames = Enumerable.Repeat(10f, 990).Concat(Enumerable.Range(1, 10).Select(i => 10f + 10 * i)).ToArray();
        var s = FrameStats.Compute(frames);
        Assert.Equal(1000.0 / 65.0, s.OnePercentLowFps, 6);
        // 0.1% of 1000 = 1 frame: the slowest (110 ms).
        Assert.Equal(1000.0 / 110.0, s.PointOnePercentLowFps, 6);
        Assert.False(s.PointOnePercentLowIsReliable);
    }

    [Fact]
    public void Lows_use_at_least_one_frame()
    {
        var s = FrameStats.Compute(new float[] { 10, 10, 10, 40 });
        Assert.Equal(25.0, s.OnePercentLowFps, 6);
    }

    [Fact]
    public void P99_is_the_nearest_rank_percentile()
    {
        var frames = Enumerable.Range(1, 200).Select(i => (float)i).ToArray(); // 1..200 ms
        var s = FrameStats.Compute(frames);
        Assert.Equal(198.0, s.P99FrameMs); // ceil(0.99 × 200) = 198th value
        Assert.Equal(100.5, s.MedianFrameMs);
    }

    [Fact]
    public void Stutter_is_above_twice_the_median_and_above_the_minimum()
    {
        // Median 10 ms: the line is max(20, 25) = 25 ms. 24 ms isn't a stutter, 26 is.
        var frames = Enumerable.Repeat(10f, 100).Concat(new float[] { 24, 26, 50 }).ToArray();
        Assert.Equal(2, FrameStats.Compute(frames).Stutters);
        // Median 20 ms: the line is 40 ms.
        var slow = Enumerable.Repeat(20f, 100).Concat(new float[] { 30, 39, 41 }).ToArray();
        Assert.Equal(1, FrameStats.Compute(slow).Stutters);
    }

    [Fact]
    public void Stutters_per_minute_uses_run_length()
    {
        var frames = Enumerable.Repeat(10f, 6000).Concat(new float[] { 100, 100, 100 }).ToArray(); // ~60 s
        var s = FrameStats.Compute(frames);
        Assert.Equal(3, s.Stutters);
        Assert.Equal(3.0 / (s.Seconds / 60), s.StuttersPerMinute, 6);
    }

    [Fact]
    public void Spread_is_the_sample_sd_as_percent_of_the_mean()
    {
        Assert.Equal(10.0, FrameStats.SpreadPercent(new[] { 90.0, 100.0, 110.0 }), 6);
        Assert.Equal(0.0, FrameStats.SpreadPercent(new[] { 100.0 }));
    }

    [Fact]
    public void The_definitions_shown_on_the_page_name_the_rules()
    {
        Assert.Contains("frames ÷ total time", FrameStats.Definitions.AverageFps);
        Assert.Contains("slowest 1%", FrameStats.Definitions.OnePercentLow);
        Assert.Contains("never used for the verdict", FrameStats.Definitions.PointOnePercentLow);
        Assert.Contains("2× the run's median", FrameStats.Definitions.Stutter);
        Assert.Contains("25 ms", FrameStats.Definitions.Stutter);
    }

    [Fact]
    public void Empty_input_gives_zeros()
    {
        Assert.Equal(0, FrameStats.Compute(ReadOnlySpan<float>.Empty).Frames);
    }
}
