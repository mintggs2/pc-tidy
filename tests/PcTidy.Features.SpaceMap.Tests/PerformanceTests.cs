using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.Treemap;
using Xunit.Abstractions;

namespace PcTidy.Features.SpaceMap.Tests;

/// <summary>
/// Measured numbers for the vault (plan §10). Budgets are generous so a slow CI box passes; the real
/// numbers are written to testbed\spacemap\perf-results.txt.
/// </summary>
[Collection("perf")] // run alone so timings aren't skewed by other tests
public class PerformanceTests
{
    private readonly ITestOutputHelper _out;
    public PerformanceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Index_200k_synthetic_entries_and_lay_out_within_budget()
    {
        var walker = SyntheticTree.Walker();
        var scanner = new SpaceIndexScanner(walker, new FakeSafety(), new FakeTags(), new FakeDrives());
        var options = new SpaceScanOptions { IncludeUnscannedNode = false };
        scanner.Scan(SyntheticTree.Root, options, null, CancellationToken.None); // warm-up (JIT)

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var ix = scanner.Scan(SyntheticTree.Root, options, null, CancellationToken.None);
        var build = sw.Elapsed;
        long retained = GC.GetTotalMemory(true) - before;
        int nodes = ix.Count;
        Assert.Equal(SyntheticTree.ExpectedFiles, ix.Summary.Files);
        Assert.True(nodes > 195_000, $"{nodes} nodes");

        sw.Restart();
        const int w = 1920, h = 1080;
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, w, h);
        var layout = sw.Elapsed;
        sw.Restart();
        TreemapRenderer.Render(scene, new TreemapPalette(), new int[w * h], new int[w * h]);
        var paint = sw.Elapsed;

        sw.Restart();
        var top = SpaceQueries.TopFiles(ix, SpaceIndex.RootNode);
        var types = SpaceQueries.ByExtension(ix, SpaceIndex.RootNode);
        var queries = sw.Elapsed;

        double footprintPerNode = (double)ix.FootprintBytes / nodes;
        double retainedPerNode = (double)retained / nodes;
        var line = $"synthetic {nodes:N0} nodes: index {build.TotalMilliseconds:N0} ms ({nodes / build.TotalSeconds:N0} entries/s), " +
                   $"{footprintPerNode:N1} B/node arrays, {retainedPerNode:N1} B/node retained; layout {scene.TileCount:N0} tiles {layout.TotalMilliseconds:N1} ms, " +
                   $"paint {w}x{h} {paint.TotalMilliseconds:N1} ms; top-100 + types {queries.TotalMilliseconds:N1} ms";
        _out.WriteLine(line);
        Perf.Record(line);
        GC.KeepAlive(ix);

        Assert.True(build < TimeSpan.FromSeconds(5), line);
        Assert.True(layout + paint < TimeSpan.FromSeconds(1), line);
        Assert.True(footprintPerNode <= 100, line);
        Assert.True(retainedPerNode <= 110, line);
        Assert.Equal(100, top.Count);
        Assert.NotEmpty(types);
    }

    /// <summary>~20k small files generated once under testbed\spacemap\bench-tree (kept between runs; it's the fixture).</summary>
    [Fact]
    public void Disk_scan_of_20k_files()
    {
        var root = BenchTree.Ensure();
        var safety = new FakeSafety();
        var scanner = new SpaceIndexScanner(new ManagedFileWalker(), safety);
        var options = new SpaceScanOptions { IncludeUnscannedNode = false };
        scanner.Scan(root, options, null, CancellationToken.None); // warm the file-system cache

        var best = TimeSpan.MaxValue;
        SpaceIndex ix = null!;
        for (int run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            ix = scanner.Scan(root, options, null, CancellationToken.None);
            if (sw.Elapsed < best) best = sw.Elapsed;
        }
        var single = Stopwatch.StartNew();
        scanner.Scan(root, options with { MaxParallelism = 1 }, null, CancellationToken.None);
        var singleThread = single.Elapsed;

        Assert.Equal(BenchTree.FileCount, ix.Summary.Files);
        Assert.Equal(BenchTree.TotalBytes, ix.Size(SpaceIndex.RootNode));
        var line = $"disk {ix.Summary.Files:N0} files / {ix.Summary.Folders:N0} folders: {best.TotalMilliseconds:N0} ms with {options.MaxParallelism} workers " +
                   $"({ix.Summary.Files / best.TotalSeconds:N0} files/s), {singleThread.TotalMilliseconds:N0} ms single-threaded, " +
                   $"{(double)ix.FootprintBytes / ix.Count:N1} B/node";
        _out.WriteLine(line);
        Perf.Record(line);
        Assert.True(best < TimeSpan.FromSeconds(20), line);
    }

    /// <summary>
    /// Optional, read-only: set PCTIDY_SPACEMAP_REALSCAN=C:\ to measure a real drive (nothing is changed;
    /// the scan only lists folders). Skipped silently otherwise so normal runs stay deterministic.
    /// </summary>
    [Fact]
    public void Optional_real_drive_scan()
    {
        var root = Environment.GetEnvironmentVariable("PCTIDY_SPACEMAP_REALSCAN");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var ix = new SpaceIndexScanner(new ManagedFileWalker(), new FakeSafety()).Scan(root, new SpaceScanOptions(), null, CancellationToken.None);
        var elapsed = sw.Elapsed;
        long retained = GC.GetTotalMemory(true) - before;
        sw.Restart();
        var scene = TreemapLayoutEngine.Build(ix, SpaceIndex.RootNode, 1920, 1080);
        TreemapRenderer.Render(scene, new TreemapPalette(), new int[1920 * 1080], new int[1920 * 1080]);
        var draw = sw.Elapsed;
        var line = $"real {root}: {ix.Summary.Files:N0} files, {ix.Summary.Folders:N0} folders, {ix.Summary.Inaccessible:N0} unreadable, " +
                   $"{elapsed.TotalSeconds:N1} s ({ix.Summary.Files / elapsed.TotalSeconds:N0} files/s), {(double)ix.FootprintBytes / ix.Count:N1} B/node arrays, " +
                   $"{(double)retained / ix.Count:N1} B/node retained, layout+paint 1920x1080 {draw.TotalMilliseconds:N0} ms ({scene.TileCount:N0} tiles), " +
                   $"scanned {Sizes.Format(ix.Summary.ScannedBytesOnDisk)} of {Sizes.Format(ix.Summary.DriveUsedBytes ?? 0)} used";
        _out.WriteLine(line);
        Perf.Record(line);
        GC.KeepAlive(ix);
    }
}

[CollectionDefinition("perf", DisableParallelization = true)]
public class PerfCollection { }

/// <summary>The persistent 20k-file disk fixture: 20 folders × 25 sub-folders × 40 files, 1–4,096 bytes each.</summary>
internal static class BenchTree
{
    public const int Top = 20, Sub = 25, PerFolder = 40;
    public const int FileCount = Top * Sub * PerFolder;
    public static readonly string Root = Path.Combine(TestDir.AreaRoot, "bench-tree");
    private static readonly string Marker = Path.Combine(TestDir.AreaRoot, "bench-tree.v1.ok");
    private static readonly object Gate = new();

    public static long TotalBytes
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < FileCount; i++) sum += SizeOf(i);
            return sum;
        }
    }

    private static int SizeOf(int i) => 1 + (int)((uint)(i * 2654435761u) % 4096);

    public static string Ensure()
    {
        lock (Gate)
        {
            if (File.Exists(Marker) && Directory.Exists(Root)) return Root;
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            int n = 0;
            var buffer = new byte[4096];
            for (int a = 0; a < Top; a++)
                for (int b = 0; b < Sub; b++)
                {
                    var dir = Path.Combine(Root, $"group {a:00}", $"set {b:00}");
                    Directory.CreateDirectory(dir);
                    for (int f = 0; f < PerFolder; f++, n++)
                        using (var fs = new FileStream(Path.Combine(dir, $"file {f:00}.{(f % 4) switch { 0 => "txt", 1 => "jpg", 2 => "json", _ => "bin" }}"), FileMode.CreateNew))
                            fs.Write(buffer, 0, SizeOf(n));
                }
            File.WriteAllText(Marker, $"{FileCount} files");
            return Root;
        }
    }
}
