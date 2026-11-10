using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Treemap;

/// <summary>Colours as 0xAARRGGBB. Built on the UI thread from theme brushes, then used on a background thread.</summary>
public sealed record TreemapPalette
{
    public uint Card { get; init; } = 0xFF2B2B2B;
    public uint Protected { get; init; } = 0xFF3A3F45;
    public uint HatchLine { get; init; } = 0x38FFFFFF;
    public uint Merged { get; init; } = 0xFF6E7781;
    public uint Dim { get; init; } = 0xFF202020;
    /// <summary>One colour per <see cref="FileCategory"/> (mockup palette).</summary>
    public uint[] Categories { get; init; } =
    {
        0xFF848E99, // Other
        0xFF866BD6, // Videos
        0xFF339CD6, // Images
        0xFFCF5E96, // Music
        0xFFC29A1E, // Documents
        0xFF5F7FC9, // Archives
        0xFF24A08C, // Apps
        0xFFD97A3E, // Games
        0xFF7A8794, // System
    };

    public uint ColorOf(FileCategory c) => Categories[(int)c < Categories.Length ? (int)c : 0];
}

/// <summary>
/// Paints a <see cref="TreemapScene"/> into a premultiplied BGRA pixel buffer: flat type colours with a faint
/// top-to-bottom shade (so neighbouring tiles of one colour still read as separate blocks), hatching on protected
/// areas, and fills a tile-id map so hit-testing is a single array lookup. Thread-safe for distinct buffers.
/// </summary>
public static class TreemapRenderer
{
    /// <summary>Brightness at the top and bottom edge of a tile (1 = the flat colour).</summary>
    private const double TopLight = 1.07, BottomLight = 0.93;

    /// <param name="pixels">Width*Height premultiplied BGRA pixels (cleared to transparent first).</param>
    /// <param name="tileMap">Width*Height tile indices (-1 = background).</param>
    public static void Render(TreemapScene scene, TreemapPalette palette, int[] pixels, int[] tileMap, double scale = 1.0, CancellationToken ct = default)
    {
        int w = scene.Width, h = scene.Height;
        if (pixels.Length < w * h || tileMap.Length < w * h) throw new ArgumentException("Buffers are smaller than the scene.");
        Array.Clear(pixels, 0, w * h);
        Array.Fill(tileMap, -1, 0, w * h);
        double radius = 5 * scale;
        double hatchPeriod = 10 * scale, hatchWidth = 1.6 * scale;

        for (int t = 0; t < scene.TileCount; t++)
        {
            if ((t & 255) == 0) ct.ThrowIfCancellationRequested();
            ref readonly var tile = ref scene.Tiles[t];
            int x0 = Clamp((int)Math.Round(tile.Rect.X), w), x1 = Clamp((int)Math.Round(tile.Rect.Right), w);
            int y0 = Clamp((int)Math.Round(tile.Rect.Y), h), y1 = Clamp((int)Math.Round(tile.Rect.Bottom), h);
            if (x1 <= x0 || y1 <= y0) continue;
            double r = tile.Rounded ? Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2.0) : Math.Min(2 * scale, Math.Min(x1 - x0, y1 - y0) / 4.0);

            switch (tile.Kind)
            {
                case TileKind.Folder:
                    // Not painted: its children cover it. Still owns gap pixels for hit-testing.
                    FillMap(tileMap, w, x0, x1, y0, y1, t, r);
                    break;
                case TileKind.Card:
                    FillFlat(pixels, tileMap, w, x0, x1, y0, y1, t, r, palette.Card);
                    break;
                case TileKind.Protected:
                    FillHatched(pixels, tileMap, w, x0, x1, y0, y1, t, r, palette.Protected, palette.HatchLine, hatchPeriod, hatchWidth);
                    break;
                default:
                    uint baseColor = tile.Kind == TileKind.Merged ? palette.Merged : palette.ColorOf(tile.Category);
                    if (tile.Dimmed) baseColor = Mix(baseColor, palette.Dim, 0.72);
                    FillShaded(pixels, tileMap, w, x0, x1, y0, y1, t, r, baseColor);
                    break;
            }
        }
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    /// <summary>Horizontal inset of row <paramref name="y"/> inside a rounded rectangle.</summary>
    private static int CornerInset(int y, int y0, int y1, double r)
    {
        if (r <= 0) return 0;
        double dy = y + 0.5 < y0 + r ? y0 + r - (y + 0.5) : y + 0.5 > y1 - r ? (y + 0.5) - (y1 - r) : -1;
        if (dy < 0) return 0;
        return (int)Math.Ceiling(r - Math.Sqrt(Math.Max(0, r * r - dy * dy)) - 0.35);
    }

    private static void FillMap(int[] map, int w, int x0, int x1, int y0, int y1, int t, double r)
    {
        for (int y = y0; y < y1; y++)
        {
            int inset = CornerInset(y, y0, y1, r);
            int row = y * w;
            for (int x = x0 + inset; x < x1 - inset; x++) map[row + x] = t;
        }
    }

    private static void FillFlat(int[] px, int[] map, int w, int x0, int x1, int y0, int y1, int t, double r, uint color)
    {
        int c = Premultiply(color);
        for (int y = y0; y < y1; y++)
        {
            int inset = CornerInset(y, y0, y1, r);
            int row = y * w;
            for (int x = x0 + inset; x < x1 - inset; x++) { px[row + x] = c; map[row + x] = t; }
        }
    }

    private static void FillHatched(int[] px, int[] map, int w, int x0, int x1, int y0, int y1, int t, double r,
        uint fill, uint line, double period, double lineWidth)
    {
        int plain = Premultiply(fill);
        int striped = Premultiply(Blend(fill, line));
        for (int y = y0; y < y1; y++)
        {
            int inset = CornerInset(y, y0, y1, r);
            int row = y * w;
            for (int x = x0 + inset; x < x1 - inset; x++)
            {
                double phase = (x + y) % period;
                px[row + x] = phase < lineWidth ? striped : plain;
                map[row + x] = t;
            }
        }
    }

    /// <summary>The type colour, a touch lighter at the top and darker at the bottom. One colour per row: cheap.</summary>
    private static void FillShaded(int[] px, int[] map, int w, int x0, int x1, int y0, int y1, int t, double r, uint color)
    {
        double cr = (color >> 16) & 0xFF, cg = (color >> 8) & 0xFF, cb = color & 0xFF;
        double span = Math.Max(1, y1 - y0 - 1);
        for (int y = y0; y < y1; y++)
        {
            double light = TopLight + (BottomLight - TopLight) * ((y - y0) / span);
            int c = unchecked((int)(0xFF000000u | (Ch(cr * light) << 16) | (Ch(cg * light) << 8) | Ch(cb * light)));
            int inset = CornerInset(y, y0, y1, r);
            int row = y * w;
            for (int x = x0 + inset; x < x1 - inset; x++) { px[row + x] = c; map[row + x] = t; }
        }
    }

    private static uint Ch(double v) => v <= 0 ? 0u : v >= 255 ? 255u : (uint)v;

    /// <summary>Alpha-blends <paramref name="top"/> (with its alpha) over an opaque <paramref name="bottom"/>.</summary>
    private static uint Blend(uint bottom, uint top)
    {
        double a = (top >> 24) / 255.0;
        return Mix(bottom, top | 0xFF000000, a);
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint Lerp(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t) & 0xFF;
        return 0xFF000000 | (Lerp(16) << 16) | (Lerp(8) << 8) | Lerp(0);
    }

    /// <summary>Opaque colours are already premultiplied; translucent ones are scaled.</summary>
    private static int Premultiply(uint argb)
    {
        uint a = argb >> 24;
        if (a == 255) return unchecked((int)argb);
        uint P(int shift) => ((argb >> shift) & 0xFF) * a / 255;
        return unchecked((int)((a << 24) | (P(16) << 16) | (P(8) << 8) | P(0)));
    }
}
