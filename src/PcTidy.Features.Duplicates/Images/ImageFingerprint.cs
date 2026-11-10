using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Features.Duplicates.Hashing;

namespace PcTidy.Features.Duplicates.Images;

/// <summary>
/// Perceptual fingerprints of a picture: dHash (brightness gradient, 9×8) and pHash (low-frequency DCT of 32×32).
/// Pictures are decoded with WPF/WIC straight to a small thumbnail, read-only. Two pictures are "similar" when
/// both hashes differ in only a few of their 64 bits (Hamming distance).
/// </summary>
public static class ImageFingerprint
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".jfif" };

    private const int Side = 32;

    /// <summary>Fingerprint, or null if the file can't be decoded as a picture (or is too small to compare).</summary>
    public static ImageHashes? TryCompute(string path)
    {
        try
        {
            using var fs = new FileStream(Win32Path.Raw(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            // CacheOption.None: pixels are pulled straight into the small thumbnail below (WIC can scale JPEGs while
            // decoding), all before the stream closes, so a big photo is never held in memory at full size.
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w < 16 || h < 16) return null;
            var small = new TransformedBitmap(frame, new ScaleTransform((double)Side / w, (double)Side / h));
            var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            int gw = gray.PixelWidth, gh = gray.PixelHeight;
            var pixels = new byte[gw * gh];
            gray.CopyPixels(pixels, gw, 0);
            var luma = Resample(pixels, gw, gh, Side, Side);
            return new ImageHashes(DHash(luma), PHash(luma), w, h);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException
                                       or OverflowException)
        {
            return null;
        }
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Box-average resample (the WIC scaler may be off by a pixel; this makes the grid exact).</summary>
    private static double[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new double[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                double sum = 0; int n = 0;
                for (int yy = y0; yy < y1 && yy < sh; yy++)
                    for (int xx = x0; xx < x1 && xx < sw; xx++) { sum += src[yy * sw + xx]; n++; }
                dst[y * dw + x] = n == 0 ? 0 : sum / n;
            }
        }
        return dst;
    }

    /// <summary>dHash: is each of 8 columns brighter than the next one (9×8 grid)?</summary>
    private static ulong DHash(double[] luma)
    {
        var grid = new double[9 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 9; x++)
            {
                int x0 = x * Side / 9, x1 = Math.Max(x0 + 1, (x + 1) * Side / 9);
                int y0 = y * Side / 8, y1 = (y + 1) * Side / 8;
                double sum = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) { sum += luma[yy * Side + xx]; n++; }
                grid[y * 9 + x] = sum / Math.Max(1, n);
            }
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (grid[y * 9 + x] > grid[y * 9 + x + 1]) hash |= 1UL << bit;
        return hash;
    }

    private static readonly double[,] Cos = BuildCos();

    private static double[,] BuildCos()
    {
        var c = new double[Side, Side];
        for (int u = 0; u < Side; u++)
            for (int x = 0; x < Side; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        return c;
    }

    /// <summary>pHash: signs of the 8×8 lowest DCT frequencies relative to their median (DC term left out).</summary>
    private static ulong PHash(double[] luma)
    {
        // Separable 2-D DCT, only the 8 lowest frequencies in each direction are needed.
        var rows = new double[Side * 8];
        for (int y = 0; y < Side; y++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int x = 0; x < Side; x++) s += luma[y * Side + x] * Cos[u, x];
                rows[y * 8 + u] = s;
            }
        var coeffs = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int y = 0; y < Side; y++) s += rows[y * 8 + u] * Cos[v, y];
                coeffs[v * 8 + u] = s;
            }
        var sorted = coeffs.Skip(1).OrderBy(c => c).ToArray();
        double median = (sorted[31] + sorted[32]) / 2;
        ulong hash = 0;
        for (int i = 1; i < 64; i++)
            if (coeffs[i] > median) hash |= 1UL << i;
        return hash;
    }
}
