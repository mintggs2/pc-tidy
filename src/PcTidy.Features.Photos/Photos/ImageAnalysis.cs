using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcTidy.Features.Photos.Photos;

/// <summary>What PC Tidy measured on a photo's small grey thumbnail.</summary>
/// <param name="Sharpness">Edge sharpness, see <see cref="ImageMath.Sharpness"/>: about 100 for crisp edges, lower = softer.</param>
/// <param name="Contrast">Standard deviation of brightness. Very flat pictures can't be judged for blur.</param>
/// <param name="Hash">64-bit dHash: near-identical pictures differ in only a few bits.</param>
public sealed record ImageMetrics(int Width, int Height, double Sharpness, double Contrast, ulong Hash);

/// <summary>Pure maths on 8-bit grey pixels (row-major, width × height).</summary>
public static class ImageMath
{
    /// <summary>
    /// Edge sharpness: the variance of the 4-neighbour Laplacian divided by the mean squared gradient, scaled so a
    /// perfectly crisp edge scores 100. The plain variance of the Laplacian grows with how busy a picture is (a
    /// sharp photo of a clear sky scores lower than a blurry photo of a forest); dividing by the gradient energy
    /// leaves only how WIDE the edges are. An edge smeared over w pixels scores about 100 / w; noise scores high,
    /// so dark, grainy shots aren't mistaken for blurry ones. Returns 0 for a completely flat picture.
    /// </summary>
    public static double Sharpness(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (width < 3 || height < 3) return 0;
        double lapSum = 0, lapSq = 0, gradSq = 0;
        long n = 0;
        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                double lap = gray[i - 1] + gray[i + 1] + gray[i - width] + gray[i + width] - 4.0 * gray[i];
                double dx = gray[i + 1] - gray[i], dy = gray[i + width] - gray[i];
                lapSum += lap;
                lapSq += lap * lap;
                gradSq += dx * dx + dy * dy;
                n++;
            }
        }
        if (gradSq <= 0) return 0;
        double mean = lapSum / n;
        double lapVariance = lapSq / n - mean * mean;
        // A one-pixel step of height d: Laplacian² sums to 2d², gradient² to d² → ratio 2 → score 100.
        return 50.0 * lapVariance / (gradSq / n);
    }

    /// <summary>Standard deviation of brightness.</summary>
    public static double Contrast(ReadOnlySpan<byte> gray)
    {
        if (gray.Length == 0) return 0;
        double sum = 0, sumSq = 0;
        foreach (var v in gray) { sum += v; sumSq += (double)v * v; }
        double mean = sum / gray.Length;
        return Math.Sqrt(Math.Max(0, sumSq / gray.Length - mean * mean));
    }

    /// <summary>dHash: on a 9×8 grid of box averages, is each cell brighter than its right neighbour?</summary>
    public static ulong DifferenceHash(ReadOnlySpan<byte> gray, int width, int height)
    {
        Span<double> grid = stackalloc double[9 * 8];
        for (int gy = 0; gy < 8; gy++)
        {
            int y0 = gy * height / 8, y1 = Math.Max(y0 + 1, (gy + 1) * height / 8);
            for (int gx = 0; gx < 9; gx++)
            {
                int x0 = gx * width / 9, x1 = Math.Max(x0 + 1, (gx + 1) * width / 9);
                double s = 0; int c = 0;
                for (int y = y0; y < y1 && y < height; y++)
                    for (int x = x0; x < x1 && x < width; x++) { s += gray[y * width + x]; c++; }
                grid[gy * 9 + gx] = c == 0 ? 0 : s / c;
            }
        }
        ulong hash = 0;
        int bit = 0;
        for (int gy = 0; gy < 8; gy++)
            for (int gx = 0; gx < 8; gx++, bit++)
                if (grid[gy * 9 + gx] > grid[gy * 9 + gx + 1]) hash |= 1UL << bit;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>
/// Blur check (a heuristic, and the page says so): <see cref="ImageMath.Sharpness"/> (variance of the Laplacian over the
/// gradient energy) on a thumbnail of at most <see cref="ImageAnalyzer.ThumbnailSide"/> pixels. Tuned on the testbed's
/// synthetic photos: every unblurred testbed picture (JPEG q55 to q92, PNG screenshots, 80 to 192 px) scores 87 to 150;
/// the same kind of scene blurred with a 3×3 box scores about 39, with a 5×5 box about 25. The threshold sits between,
/// low enough that only clearly soft shots are flagged. Blur is judged at thumbnail size, so it finds shake or focus
/// misses you'd see on screen, not the last bit of crispness. Fog, mist and soft-focus portraits can still be flagged:
/// every flag is unticked and shown with a preview.
/// </summary>
public static class BlurCheck
{
    public const double Threshold = 50;
    /// <summary>Below this brightness spread a picture is too flat to judge (a plain wall or a black frame isn't "blurry").</summary>
    public const double MinContrast = 10;
    /// <summary>Thumbnails smaller than this on either side are too small to judge.</summary>
    public const int MinSide = 48;

    public static bool IsBlurry(ImageMetrics m)
        => m.Width >= MinSide && m.Height >= MinSide && m.Contrast >= MinContrast && m.Sharpness < Threshold;
}

/// <summary>
/// Decodes a photo straight to a small grey thumbnail with WPF imaging (WIC; JPEGs are scaled while decoding, so a big
/// photo is never held at full size) and measures it. Read-only. Never call it for online-only files.
/// </summary>
public static class ImageAnalyzer
{
    /// <summary>Longest side of the thumbnail the checks run on. Smaller pictures are used as they are (never enlarged).</summary>
    public const int ThumbnailSide = 512;
    /// <summary>Bigger files aren't decoded (they're rarely photos, and decoding them is slow).</summary>
    public const long MaxBytes = 150L * 1024 * 1024;

    public static ImageMetrics? TryAnalyze(string path, long size)
    {
        if (size <= 0 || size > MaxBytes || !PhotoFormats.CanDecode(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w < 3 || h < 3) return null;
            double scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(w, h));
            BitmapSource small = scale < 1.0 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            int gw = gray.PixelWidth, gh = gray.PixelHeight;
            int stride = (gw + 3) & ~3;
            var raw = new byte[stride * gh];
            gray.CopyPixels(raw, stride, 0);
            var pixels = stride == gw ? raw : Compact(raw, stride, gw, gh);
            return new ImageMetrics(gw, gh, ImageMath.Sharpness(pixels, gw, gh), ImageMath.Contrast(pixels), ImageMath.DifferenceHash(pixels, gw, gh));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or COMException or FileFormatException or OverflowException)
        {
            return null;
        }
    }

    private static byte[] Compact(byte[] raw, int stride, int width, int height)
    {
        var dst = new byte[width * height];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(raw, y * stride, dst, y * width, width);
        return dst;
    }
}
