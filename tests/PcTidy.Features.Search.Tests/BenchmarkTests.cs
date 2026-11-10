using System.Diagnostics;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.Search.Tests;

/// <summary>
/// Memory and latency budgets of the own index, on synthetic in-memory sets (Research §6: ~100 B per file;
/// Plan §8: instant search). Numbers are written to the test log and recorded on the vault page.
/// </summary>
[Collection("search-testbed")] // run alone: timings shouldn't compete with other tests
[Trait("Category", "Benchmark")]
public class BenchmarkTests(ITestOutputHelper log)
{
    [Fact]
    public void Memory_is_under_100_bytes_per_entry_on_200k()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        var segment = Synthetic.Generate(@"X:\Users\tester", 200_000, seed: 7);
        var buildMs = sw.ElapsedMilliseconds;
        long after = GC.GetTotalMemory(forceFullCollection: true);
        double arrayPerEntry = (double)segment.ArrayBytes / segment.Count;
        double heapPerEntry = (double)(after - before) / segment.Count;
        double avgName = (double)segment.Names.Length / segment.Count;
        log.WriteLine($"200,000 entries: arrays {segment.ArrayBytes:N0} B = {arrayPerEntry:N1} B/entry; heap delta {heapPerEntry:N1} B/entry; " +
                      $"average name {avgName:N1} chars; built + sorted in {buildMs} ms");
        Assert.True(arrayPerEntry <= 100, $"{arrayPerEntry:N1} B/entry");
        Assert.True(heapPerEntry <= 110, $"{heapPerEntry:N1} B/entry on the heap");
        GC.KeepAlive(segment);
    }

    [Fact]
    public void Queries_on_1M_entries_answer_in_under_100_ms()
    {
        var snapshot = new IndexSnapshot(new[]
        {
            Synthetic.Generate(@"X:\Users\tester", 700_000, seed: 11),
            Synthetic.Generate(@"Y:\", 300_000, seed: 12),
        }, 1);
        Assert.Equal(1_000_000, snapshot.EntryCount);
        log.WriteLine($"1,000,000 entries, {snapshot.ArrayBytes / 1048576.0:N1} MB in arrays ({snapshot.BytesPerEntry:N1} B/entry)");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero));

        // Typical, selective searches: the budget applies.
        string[] instant =
        {
            "holiday 2023 0412",
            "ext:pdf size:>100mb",
            "contract dm:lastweek",
            "summer|travel ext:heic",
            "\"rep*t 2020*.docx\"",
            "path:\"budget 2021\" ext:xlsx",
            "!ext:jpg final 2024 99",
            "dupe: recipe 2025 ext:mp3",
        };
        foreach (var q in instant)
        {
            var (ms, count) = Time(snapshot, q, clock);
            log.WriteLine($"{ms,6:N1} ms  {count,9:N0} hits  {q}");
            Assert.True(ms < 100, $"'{q}' took {ms:N1} ms");
        }

        // Broad searches (hundreds of thousands of hits): recorded, not budgeted.
        foreach (var q in new[] { "file:", "ext:jpg", "path:summer" })
        {
            var (ms, count) = Time(snapshot, q, clock);
            log.WriteLine($"{ms,6:N1} ms  {count,9:N0} hits  {q} (broad)");
        }
    }

    /// <summary>Median of 5 runs after one warm-up (JIT).</summary>
    private static (double Ms, int Count) Time(IndexSnapshot snapshot, string text, TimeProvider clock)
    {
        var parsed = QueryParser.Parse(text, clock);
        int count = QueryEngine.Search(snapshot, parsed).Count;
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            count = QueryEngine.Search(snapshot, QueryParser.Parse(text, clock)).Count;
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return (times[2], count);
    }
}
