using PcTidy.Features.SpaceMap.Treemap;

namespace PcTidy.Features.SpaceMap.Tests;

public class SquarifiedLayoutTests
{
    private const double Eps = 1e-6;

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { new double[] { 6, 6, 4, 3, 2, 2, 1 }, new RectD(0, 0, 600, 400) };   // the paper's example
        yield return new object[] { new double[] { 1 }, new RectD(10, 20, 300, 50) };
        yield return new object[] { new double[] { 100, 1, 1, 1, 1, 1, 1, 1 }, new RectD(0, 0, 50, 800) };
        yield return new object[] { Enumerable.Range(1, 500).Select(i => 1000.0 / i).ToArray(), new RectD(3, 7, 1280, 720) };
        yield return new object[] { Enumerable.Repeat(5.0, 97).ToArray(), new RectD(0, 0, 333, 333) };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Areas_are_proportional_within_bounds_and_without_overlap(double[] weights, RectD bounds)
    {
        var rects = new RectD[weights.Length];
        SquarifiedLayout.Layout(weights, bounds, rects);
        double total = weights.Sum();

        for (int i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            Assert.True(r.W >= 0 && r.H >= 0);
            Assert.True(r.X >= bounds.X - Eps && r.Y >= bounds.Y - Eps && r.Right <= bounds.Right + Eps && r.Bottom <= bounds.Bottom + Eps, $"rect {i} {r} outside {bounds}");
            Assert.Equal(weights[i] / total * bounds.Area, r.Area, 3);
        }
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                Assert.True(Overlap(rects[i], rects[j]) < 1e-6, $"rects {i} and {j} overlap");
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3); // fully tiled
    }

    [Fact]
    public void Squarified_rows_keep_aspect_ratios_reasonable()
    {
        var weights = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var rects = new RectD[weights.Length];
        SquarifiedLayout.Layout(weights, new RectD(0, 0, 6, 4), rects);
        double worst = rects.Max(r => Math.Max(r.W / r.H, r.H / r.W));
        Assert.True(worst < 3, $"worst aspect {worst}"); // slice-and-dice would give 6+ here
    }

    [Fact]
    public void Zero_area_bounds_or_weights_give_empty_rects()
    {
        var rects = new RectD[2];
        SquarifiedLayout.Layout(new double[] { 0, 0 }, new RectD(0, 0, 100, 100), rects);
        Assert.All(rects, r => Assert.Equal(0, r.Area));
        SquarifiedLayout.Layout(new double[] { 1, 2 }, new RectD(0, 0, 0, 100), rects);
        Assert.All(rects, r => Assert.Equal(0, r.Area));
    }

    private static double Overlap(RectD a, RectD b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}
