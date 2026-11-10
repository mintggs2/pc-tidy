using System.Numerics;

namespace PcTidy.Features.Games.Clips;

/// <summary>Length and frame size of a video, from Windows' property system.</summary>
public readonly record struct MediaInfo(TimeSpan Duration, int Width, int Height);

/// <summary>
/// Reads clip facts without ever downloading anything: Windows' property system (System.Media.Duration,
/// System.Video.FrameWidth/FrameHeight) and the shell's thumbnail cache. Callers never pass OneDrive online-only files.
/// </summary>
public interface IMediaProbe
{
    MediaInfo? Probe(string path);

    /// <summary>64-bit difference hash of the shell thumbnail, or null when there is none.</summary>
    /// <param name="cacheOnly">True: only a thumbnail already in Windows' cache (never makes the shell open the file).</param>
    ulong? ThumbnailHash(string path, bool cacheOnly);
}

/// <summary>No media facts (sandbox default): near-duplicate detection simply finds nothing.</summary>
public sealed class NullMediaProbe : IMediaProbe
{
    public static readonly NullMediaProbe Instance = new();
    public MediaInfo? Probe(string path) => null;
    public ulong? ThumbnailHash(string path, bool cacheOnly) => null;
}

/// <summary>Perceptual "difference hash" of a small grayscale image (9×8 → 64 bits).</summary>
public static class DHash
{
    /// <summary>Hash of a 32-bit BGRA top-down pixel buffer.</summary>
    public static ulong FromBgra(ReadOnlySpan<byte> pixels, int width, int height)
    {
        Span<double> gray = stackalloc double[9 * 8];
        for (int ty = 0; ty < 8; ty++)
            for (int tx = 0; tx < 9; tx++)
            {
                int x0 = tx * width / 9, x1 = Math.Max(x0 + 1, (tx + 1) * width / 9);
                int y0 = ty * height / 8, y1 = Math.Max(y0 + 1, (ty + 1) * height / 8);
                double sum = 0; int n = 0;
                for (int y = y0; y < y1 && y < height; y++)
                    for (int x = x0; x < x1 && x < width; x++)
                    {
                        var i = (y * width + x) * 4;
                        sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                        n++;
                    }
                gray[ty * 9 + tx] = n == 0 ? 0 : sum / n;
            }
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (gray[y * 9 + x] > gray[y * 9 + x + 1]) hash |= 1UL << bit;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
