using System.Diagnostics;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Hashing;
using Xunit.Abstractions;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>50,000 files (5% exact copies, many same-size files) scanned in well under a minute on the testbed drive.</summary>
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int FileCount = 50_000;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Fifty_thousand_files_scan_in_under_a_minute()
    {
        using var pc = new HandMadePc("perf");
        var write = Stopwatch.StartNew();
        var rng = new Random(42);
        var specs = new (string Path, int Seed, int Size)[FileCount];
        int originals = FileCount * 95 / 100;
        for (int i = 0; i < FileCount; i++)
        {
            var root = i % 2 == 0 ? pc.K.Documents : pc.K.Downloads;
            var path = Path.Combine(root, $"batch {i / 250:D3}", $"file-{i:D6}.dat");
            // The last 5% copy an earlier file byte for byte; the rest are unique (sizes collide a lot on purpose).
            if (i < originals) specs[i] = (path, i, 256 + rng.Next(0, 24_000));
            else { var j = rng.Next(originals); specs[i] = (path, specs[j].Seed, specs[j].Size); }
        }
        Parallel.ForEach(specs.Select(s => Path.GetDirectoryName(s.Path)!).Distinct(), d => Directory.CreateDirectory(d));
        var old = DateTime.UtcNow.AddDays(-40);
        Parallel.For(0, FileCount, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var (path, seed, size) = specs[i];
            File.WriteAllBytes(path, HandMadePc.Bytes(seed, size));
            File.SetLastWriteTimeUtc(path, old);
        });
        output.WriteLine($"Wrote {FileCount:N0} files in {write.Elapsed.TotalSeconds:0.0} s");

        var cache = new HashCache();
        var cold = Stopwatch.StartNew();
        var report = await new DuplicatesEngine(cache).ScanAsync(pc.Context(ScanDepth.Quick));
        cold.Stop();
        var warm = Stopwatch.StartNew();
        var again = await new DuplicatesEngine(cache).ScanAsync(pc.Context(ScanDepth.Quick));
        warm.Stop();

        output.WriteLine($"Cold Quick scan: {cold.Elapsed.TotalSeconds:0.0} s, files {report.Stats.FilesSeen:N0}, size candidates {report.Stats.SizeCandidates:N0}, "
                         + $"partial {report.Stats.PartialHashed:N0}, full {report.Stats.FullHashed:N0}, groups {report.ExactGroups.Count:N0}");
        output.WriteLine($"Warm rescan (hash cache): {warm.Elapsed.TotalSeconds:0.0} s, cache hits {again.Stats.CacheHits:N0}");
        Assert.Equal(FileCount, report.Stats.FilesSeen);
        Assert.Equal(FileCount - originals, report.ExactGroups.Sum(g => g.Copies.Count - 1));
        Assert.True(cold.Elapsed < TimeSpan.FromSeconds(60), $"Cold scan took {cold.Elapsed.TotalSeconds:0.0} s");
        Assert.True(warm.Elapsed < TimeSpan.FromSeconds(60), $"Rescan took {warm.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(report.ExactGroups.Count, again.ExactGroups.Count);
    }
}
