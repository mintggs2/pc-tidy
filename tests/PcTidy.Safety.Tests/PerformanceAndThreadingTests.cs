using System.Diagnostics;
using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Safety.Tests;

/// <summary>
/// The space map and the executor call ClassifyPath / Check once per item, so both must stay fast on big folders,
/// give the same answers from many threads, and keep their caches bounded.
/// </summary>
public sealed class PerformanceAndThreadingTests : IDisposable
{
    private readonly TestSandbox _s = new(SandboxProfile.Minimal);
    private readonly ITestOutputHelper _out;

    public PerformanceAndThreadingTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _s.Dispose();

    private List<string> ManyFiles(int count, string folder)
    {
        var dir = _s.Dir(Path.Combine(_s.K.Downloads, folder));
        var files = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var f = Path.Combine(dir, $"file{i:D5}.bin");
            File.WriteAllBytes(f, Array.Empty<byte>());
            files.Add(f);
        }
        return files;
    }

    [Fact]
    public void Check_on_10000_quarantine_items_in_one_folder_takes_under_2_seconds()
    {
        var items = ManyFiles(10_000, "bulk").Select(f => _s.Quarantine(f)).ToList();
        _s.Guard.Check(items[0]); // warm-up (JIT, protection map)
        var sw = Stopwatch.StartNew();
        var allowed = items.Count(i => _s.Guard.Check(i).Allowed);
        sw.Stop();
        _out.WriteLine($"Check x {items.Count}: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMilliseconds * 1000 / items.Count:F1} µs each)");
        Assert.Equal(items.Count, allowed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Check took {sw.ElapsedMilliseconds} ms for {items.Count} items");
    }

    [Fact]
    public void ClassifyPath_on_100000_paths_takes_under_3_seconds()
    {
        var existing = ManyFiles(10_000, "scan");
        // 10,000 real files plus 90,000 paths in 90 other folders (half of them missing), like a big scan.
        var paths = new List<string>(100_000);
        paths.AddRange(existing);
        for (var d = 0; d < 90; d++)
        {
            var dir = Path.Combine(_s.K.Downloads, $"dir{d:D2}");
            if (d % 2 == 0) _s.Dir(dir);
            for (var i = 0; i < 1_000; i++) paths.Add(Path.Combine(dir, $"item{i:D4}.dat"));
        }
        _s.Guard.ClassifyPath(paths[0]);
        var sw = Stopwatch.StartNew();
        var blocked = paths.Count(p => !_s.Guard.ClassifyPath(p).Allowed);
        sw.Stop();
        _out.WriteLine($"ClassifyPath x {paths.Count}: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMilliseconds * 1000 / paths.Count:F1} µs each)");
        Assert.Equal(0, blocked);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"ClassifyPath took {sw.ElapsedMilliseconds} ms for {paths.Count} paths");
    }

    [Fact]
    public void Many_threads_get_the_same_answers_as_one()
    {
        var link = _s.Junction(Path.Combine(_s.K.Downloads, "lnk."), _s.Dir(Path.Combine(_s.K.WindowsDir, "System32")));
        var paths = ManyFiles(500, "mixed")
            .Concat(new[]
            {
                Path.Combine(link, "x.dll"), Path.Combine(_s.K.WindowsDir, "System32", "y.dll"), Path.Combine(_s.K.Downloads, "a.txt:s"),
                Path.Combine(Path.GetDirectoryName(_s.K.UserProfile)!, "other", "f.txt"), _s.K.Downloads, _s.K.UserProfile,
            })
            .ToList();
        var expected = paths.Select(p => (_s.Guard.ClassifyPath(p), _s.Guard.Check(_s.Quarantine(p)))).ToList();
        var mismatches = 0;
        Parallel.For(0, paths.Count * 8, new ParallelOptions { MaxDegreeOfParallelism = 16 }, n =>
        {
            var i = n % paths.Count;
            var got = (_s.Guard.ClassifyPath(paths[i]), _s.Guard.Check(_s.Quarantine(paths[i])));
            if (got != expected[i]) Interlocked.Increment(ref mismatches);
        });
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void Caches_stay_bounded_and_keep_entries_in_use()
    {
        var cache = new BoundedCache<int>(generationSize: 100, TimeSpan.FromMinutes(1));
        cache.Set("keep", 1);
        for (var i = 0; i < 10_000; i++)
        {
            cache.Set("k" + i, i);
            if (i % 50 == 0) Assert.True(cache.TryGet("keep", out _)); // in use: survives every swap
        }
        Assert.True(cache.Count <= 200, $"cache grew to {cache.Count}");
        Assert.True(cache.TryGet("KEEP", out var v) && v == 1); // keys are paths: case-insensitive
        Assert.False(cache.TryGet("k0", out _));

        var expiring = new BoundedCache<int>(10, TimeSpan.Zero);
        expiring.Set("a", 1);
        Assert.False(expiring.TryGet("a", out _));
    }

    [Fact]
    public void Answers_do_not_depend_on_the_display_language()
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR"); // dotted/dotless i
            var spec = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = @"hkey_current_user\Software\PCTidyTest\a", ValueName = "V", Data = "1" };
            Assert.False(_s.Guard.Check(new PlanItem { Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = "t", Registry = spec }).Allowed);
            var ok = spec with { KeyPath = @"software\pctidytest\İ" };
            Assert.True(_s.Guard.Check(new PlanItem { Kind = ActionKind.RegistrySetValue, FeatureId = "tweaks", Description = "t", Registry = ok }).Allowed);
            var sys = Path.Combine(_s.K.WindowsDir, "SYSTEM32", "drivers", "x.sys").ToLowerInvariant();
            Assert.False(_s.Guard.ClassifyPath(sys).Allowed);
            var f = _s.File(Path.Combine(_s.K.Downloads, "INSTALL.EXE"));
            Assert.True(_s.Guard.Check(_s.Quarantine(f, carveOut: CarveOuts.OldInstallers)).Allowed);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before;
        }
    }
}
