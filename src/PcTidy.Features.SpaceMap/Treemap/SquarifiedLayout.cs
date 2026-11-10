namespace PcTidy.Features.SpaceMap.Treemap;

/// <summary>Plain rectangle in device pixels (no WPF dependency, so layout runs on any thread and in tests).</summary>
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Area => W * H;
    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    public RectD Inset(double left, double top, double right, double bottom)
        => new(X + left, Y + top, Math.Max(0, W - left - right), Math.Max(0, H - top - bottom));

    public RectD Inset(double d) => Inset(d, d, d, d);
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk, 2000). Items are laid out in rows along the
/// shorter side; an item joins the current row while that doesn't make the row's worst aspect ratio worse.
/// O(n), allocation-free. The rectangles exactly tile <c>bounds</c> and their areas are proportional to the weights.
/// </summary>
public static class SquarifiedLayout
{
    /// <param name="weights">Positive weights, biggest first (smaller-first still tiles correctly, just less square).</param>
    /// <param name="output">Receives one rectangle per weight, in the same order.</param>
    public static void Layout(ReadOnlySpan<double> weights, RectD bounds, Span<RectD> output)
    {
        if (output.Length < weights.Length) throw new ArgumentException("Output is shorter than weights.", nameof(output));
        double total = 0;
        foreach (var w in weights) total += Math.Max(0, w);
        if (weights.Length == 0) return;
        if (total <= 0 || bounds.W <= 0 || bounds.H <= 0)
        {
            for (int k = 0; k < weights.Length; k++) output[k] = new RectD(bounds.X, bounds.Y, 0, 0);
            return;
        }

        double scale = bounds.Area / total;
        double x = bounds.X, y = bounds.Y, width = bounds.W, height = bounds.H;
        double remaining = bounds.Area;
        int i = 0;
        while (i < weights.Length)
        {
            double side = Math.Min(width, height);
            int start = i;
            double rowSum = 0, rowMin = double.MaxValue, rowMax = 0;
            double worst = double.MaxValue;
            while (i < weights.Length)
            {
                double a = Math.Max(0, weights[i]) * scale;
                double s = rowSum + a, mn = Math.Min(rowMin, a), mx = Math.Max(rowMax, a);
                double w = Worst(s, mn, mx, side);
                if (i > start && w > worst) break;
                rowSum = s; rowMin = mn; rowMax = mx; worst = w; i++;
            }

            // The last row takes whatever area is left, so rounding never leaves a sliver uncovered.
            bool lastRow = i == weights.Length;
            double factor = lastRow && rowSum > 0 ? remaining / rowSum : 1;
            remaining -= rowSum * factor;

            if (width >= height)
            {
                double rowWidth = lastRow ? width : Math.Min(width, rowSum / height);
                double yy = y;
                for (int k = start; k < i; k++)
                {
                    double h = k == i - 1 ? y + height - yy : rowWidth > 0 ? Math.Max(0, weights[k]) * scale * factor / rowWidth : 0;
                    output[k] = new RectD(x, yy, rowWidth, Math.Max(0, h));
                    yy += h;
                }
                x += rowWidth; width = Math.Max(0, width - rowWidth);
            }
            else
            {
                double rowHeight = lastRow ? height : Math.Min(height, rowSum / width);
                double xx = x;
                for (int k = start; k < i; k++)
                {
                    double w = k == i - 1 ? x + width - xx : rowHeight > 0 ? Math.Max(0, weights[k]) * scale * factor / rowHeight : 0;
                    output[k] = new RectD(xx, y, Math.Max(0, w), rowHeight);
                    xx += w;
                }
                y += rowHeight; height = Math.Max(0, height - rowHeight);
            }
        }
    }

    /// <summary>Worst aspect ratio of a row with total area s (min/max item areas) laid along a side of length <paramref name="side"/>.</summary>
    private static double Worst(double s, double min, double max, double side)
    {
        if (s <= 0 || min <= 0) return double.MaxValue;
        double s2 = s * s, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
