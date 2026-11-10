using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.Treemap;

namespace PcTidy.Features.SpaceMap.Tests;

public class TreemapTests
{
    private static SpaceIndex Index(FakeTree t, FakeSafety? safety = null, FakeDrives? drives = null)
        => new SpaceIndexScanner(t.Walker, safety ?? new FakeSafety(), new FakeTags(), drives ?? new FakeDrives())
            .Scan(t.Root, new SpaceScanOptions { ClusterSize = 4096, IncludeUnscannedNode = drives is not null }, null, CancellationToken.None);

    private static FakeTree Sample()
    {
        var t = new FakeTree(@"X:\Map");
        t.File(@"Videos\a.mp4", 400_000_000);
        t.File(@"Videos\b.mkv", 100_000_000);
        t.File(@"Pictures\p1.jpg", 50_000_000);
        t.File(@"Pictures\p2.png", 30_000_000);
        t.File(@"Docs\r.pdf", 20_000_000);
        for (int i = 0; i < 5000; i++) t.File($@"Docs\tiny\t{i}.txt", 10_000);
        t.File(@"Windows\sys.dll", 150_000_000);
        t.File("loose.zip", 60_000_000);
        return t;
    }

    [Fact]
    public void Tiles_stay_in_bounds_and_leaves_never_overlap()
    {
        var safety = new FakeSafety();
        safety.Never.Add(@"X:\Map\Windows");
        var ix = Index(Sample(), safety);
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 900, 600);

        var leaves = new List<RectD>();
        for (int i = 0; i < scene.TileCount; i++)
        {
            var t = scene.Tiles[i];
            Assert.True(t.Rect.X >= -1e-9 && t.Rect.Y >= -1e-9 && t.Rect.Right <= 900 + 1e-9 && t.Rect.Bottom <= 600 + 1e-9, $"tile {i} out of bounds");
            if (t.ParentTile >= 0)
            {
                var p = scene.Tiles[t.ParentTile].Rect;
                Assert.True(t.Rect.X >= p.X - 1e-6 && t.Rect.Right <= p.Right + 1e-6 && t.Rect.Y >= p.Y - 1e-6 && t.Rect.Bottom <= p.Bottom + 1e-6, $"tile {i} escapes its parent");
            }
            if (t.Kind is TileKind.Leaf or TileKind.Merged or TileKind.Protected) leaves.Add(t.Rect);
        }
        for (int i = 0; i < leaves.Count; i++)
            for (int j = i + 1; j < leaves.Count; j++)
            {
                double w = Math.Min(leaves[i].Right, leaves[j].Right) - Math.Max(leaves[i].X, leaves[j].X);
                double h = Math.Min(leaves[i].Bottom, leaves[j].Bottom) - Math.Max(leaves[i].Y, leaves[j].Y);
                Assert.False(w > 1e-6 && h > 1e-6, $"leaves {i} and {j} overlap");
            }
    }

    [Fact]
    public void Top_level_areas_are_proportional_to_size()
    {
        var ix = Index(Sample());
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 1000, 700);
        double total = ix.Size(SpaceIndex.RootNode);
        for (int i = 0; i < scene.TileCount; i++)
        {
            var t = scene.Tiles[i];
            if (t.Depth != 1 || t.Node < 0) continue;
            var outer = t.Rect.Inset(-2); // undo the 2 px gap
            double expected = ix.Size(t.Node) / total * 1000 * 700;
            Assert.InRange(outer.Area, expected * 0.99, expected * 1.01);
        }
    }

    [Fact]
    public void Many_tiny_files_merge_into_one_tile_and_protected_folders_are_not_opened()
    {
        var safety = new FakeSafety();
        safety.Never.Add(@"X:\Map\Windows");
        var ix = Index(Sample(), safety);
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 900, 600);
        var tiles = scene.Tiles.Take(scene.TileCount).ToList();

        var merged = tiles.Where(t => t.Kind == TileKind.Merged).ToList();
        Assert.NotEmpty(merged);
        Assert.True(merged.Sum(m => m.MergedCount) >= 4000);
        Assert.True(scene.TileCount < 200, $"{scene.TileCount} tiles: tiny files should not each get a tile");

        int windows = ix.Find(@"X:\Map\Windows");
        var w = tiles.Single(t => t.Node == windows);
        Assert.Equal(TileKind.Protected, w.Kind);
        Assert.DoesNotContain(tiles, t => t.Node >= 0 && t.Node != windows && ix.IsAncestorOrSelf(windows, t.Node));
        Assert.Contains(scene.Labels, l => l.Kind == LabelKind.Protected && l.Title == "Windows");
    }

    [Fact]
    public void No_tile_is_a_hairline_sliver()
    {
        // One huge file beside a few dozen small ones: the small ones (each big enough to count) end up in a strip
        // about 1 px wide along the edge, which is the classic recipe for unreadable slivers.
        var t = new FakeTree(@"X:\Thin");
        t.File(@"Big\huge.iso", 10_000_000_000);
        for (int i = 0; i < 25; i++) t.File($@"Big\part{i:00}.bin", 1_000_000);
        var ix = Index(t);
        static IEnumerable<TreemapTile> Shown(TreemapScene s) => s.Tiles.Take(s.TileCount).Where(x => x.Kind is TileKind.Leaf or TileKind.Protected);
        bool slivers = false;
        foreach (var (w, h) in new[] { (900, 600), (640, 400), (420, 300) })
        {
            // Small tiles allowed (area rule relaxed), so only the thin-side rule keeps slivers out.
            var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, w, h, new TreemapLayoutOptions { MinTileArea = 12 });
            Assert.NotEmpty(Shown(scene));
            // 6 px minimum (less the 0.5 px gap between nested tiles).
            Assert.All(Shown(scene), x => Assert.True(Math.Min(x.Rect.W, x.Rect.H) >= 5, $"{w}x{h}: {x.Rect}"));
            // Without the rule this tree does produce slivers, so the check above means something.
            var unguarded = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, w, h, new TreemapLayoutOptions { MinTileArea = 12, MinTileSide = 0 });
            slivers |= Shown(unguarded).Any(x => Math.Min(x.Rect.W, x.Rect.H) < 2);
        }
        Assert.True(slivers);
    }

    [Fact]
    public void Unscanned_block_is_drawn_as_protected()
    {
        var t = new FakeTree(@"X:\");
        t.File("a.bin", 1_000_000);
        var ix = Index(t, drives: new FakeDrives { ScanRoot = new DriveSpace(10_000_000, 7_000_000) });
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 400, 300);
        Assert.Equal(TileKind.Protected, scene.Tiles[scene.TileFor(ix, ix.UnscannedNode)].Kind);
    }

    [Fact]
    public void Render_fills_the_hit_map_with_the_deepest_tile()
    {
        var ix = Index(Sample());
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 640, 400);
        var pixels = new int[640 * 400];
        var map = new int[640 * 400];
        TreemapRenderer.Render(scene, new TreemapPalette(), pixels, map);

        int video = ix.Find(@"X:\Map\Videos\a.mp4");
        int tile = scene.TileFor(ix, video);
        Assert.Equal(video, scene.Tiles[tile].Node);
        var r = scene.Tiles[tile].Rect;
        int cx = (int)(r.X + r.W / 2), cy = (int)(r.Y + r.H / 2);
        Assert.Equal(tile, map[cy * 640 + cx]);
        Assert.NotEqual(0, pixels[cy * 640 + cx]);
        // Flat colour with only a faint shade: the top is a little lighter than the bottom, nothing more.
        int top = pixels[((int)r.Y + 3) * 640 + cx], bottom = pixels[((int)r.Bottom - 3) * 640 + cx];
        Assert.True(Brightness(top) > Brightness(bottom));
        Assert.True(Brightness(top) - Brightness(bottom) < 3 * 255 * 0.12, "shading must stay subtle");
        // Same row = same colour (no radial highlight).
        Assert.Equal(pixels[cy * 640 + (int)r.X + 4], pixels[cy * 640 + (int)r.Right - 4]);
        // A node deeper than any tile resolves to its nearest shown ancestor.
        int tiny = ix.Find(@"X:\Map\Docs\tiny\t1.txt");
        Assert.True(scene.TileFor(ix, tiny) >= 0);
    }

    [Fact]
    public void Highlight_dims_other_types()
    {
        var ix = Index(Sample());
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 640, 400, new TreemapLayoutOptions { Highlight = FileCategory.Videos });
        var tiles = scene.Tiles.Take(scene.TileCount).Where(t => t.Kind == TileKind.Leaf).ToList();
        Assert.All(tiles.Where(t => t.Category == FileCategory.Videos), t => Assert.False(t.Dimmed));
        Assert.Contains(tiles, t => t.Dimmed);
    }

    [Fact]
    public void Zooming_into_a_folder_lays_out_only_its_contents()
    {
        var ix = Index(Sample());
        int pictures = ix.Find(@"X:\Map\Pictures");
        var scene = TreemapLayoutEngine.Build(ix, pictures, 500, 300);
        Assert.All(scene.Tiles.Take(scene.TileCount), t => Assert.True(t.Node < 0 || ix.IsAncestorOrSelf(pictures, t.Node)));
        Assert.Equal(2, scene.Tiles.Take(scene.TileCount).Count(t => t.Depth == 1));
    }

    /// <summary>Writes a PNG of the synthetic tree (without the WPF text overlay) for visual review.</summary>
    [Fact]
    public void Renders_synthetic_tree_to_png_for_review()
    {
        var ix = new SpaceIndexScanner(SyntheticTree.Walker(), new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(SyntheticTree.Root, new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        const int w = 1200, h = 720;
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, w, h);
        var pixels = new int[w * h];
        TreemapRenderer.Render(scene, new TreemapPalette { Card = 0xFF2B2B2B }, pixels, new int[w * h]);
        for (int i = 0; i < pixels.Length; i++) if (pixels[i] == 0) pixels[i] = unchecked((int)0xFF202020);

        var dir = Path.Combine(TestDir.AreaRoot, "artifacts");
        Directory.CreateDirectory(dir);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(dir, "treemap-synthetic.png"))) enc.Save(fs);
        Assert.True(scene.TileCount > 100);
    }

    private static int Brightness(int argb) => ((argb >> 16) & 0xFF) + ((argb >> 8) & 0xFF) + (argb & 0xFF);
}
