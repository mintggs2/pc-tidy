using System.Buffers.Binary;

namespace PcTidy.TestbedGen.Layout;

/// <summary>
/// Many tiny files for realistic counts (Small) and performance tests (Large). Names, sizes and ages come from a
/// separate seeded generator, so changing the count never changes the hand-placed files. Content is derived
/// from (seed, index) so files can be written in parallel and still be byte-identical run to run.
/// </summary>
internal static class BulkArea
{
    private const int FilesPerFolder = 400;

    private sealed record Bucket(string Name, double Weight, Func<Ctx, int, (string Dir, string File)> Place);

    public static void Build(Ctx c, int count, CancellationToken ct)
    {
        if (count <= 0) return;
        var buckets = Buckets(c);
        var rng = new Random(c.Seed + 1);
        double totalWeight = buckets.Sum(b => b.Weight);
        var specs = new (string Path, int Size, double Age)[count];
        var perBucket = new int[buckets.Count];
        for (int i = 0; i < count; i++)
        {
            double pick = rng.NextDouble() * totalWeight;
            int b = 0;
            while (b < buckets.Count - 1 && (pick -= buckets[b].Weight) > 0) b++;
            var (dir, file) = buckets[b].Place(c, perBucket[b]++);
            specs[i] = (Path.Combine(dir, file), rng.Next(0, 4_096), rng.NextDouble() * 400);
        }
        for (int b = 0; b < buckets.Count; b++)
            c.BulkCounts[buckets[b].Name] = perBucket[b];

        Parallel.For(0, count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new byte[4_096],
            (i, _, buffer) =>
            {
                var (path, size, age) = specs[i];
                Fill(buffer.AsSpan(0, size), c.Seed, i);
                c.W.File(path, buffer.AsSpan(0, size), c.Ago(age));
                return buffer;
            },
            _ => { });
    }

    private static List<Bucket> Buckets(Ctx c)
    {
        var k = c.K;
        var list = new List<Bucket>();
        // Browser caches: spread over every Chromium profile's Cache_Data.
        var caches = BrowserArea.ChromiumUserData
            .SelectMany(u => BrowserArea.ChromiumProfiles.Select(p => Path.Combine(k.LocalAppData, u, p, "Cache", "Cache_Data"))).ToArray();
        list.Add(new("browser-cache", 34, (_, n) => (caches[n % caches.Length], $"f_{0x100000 + n:x6}")));
        var nodeModules = Path.Combine(k.Downloads, DownloadsArea.WebProject, "node_modules");
        list.Add(new("node_modules", 22, (_, n) => (Path.Combine(nodeModules, $"pkg-{n / FilesPerFolder:D4}", "lib"), $"module{n % FilesPerFolder:D3}.js")));
        list.Add(new("user-temp", 10, (_, n) => (Path.Combine(k.UserTemp, $"bulk-{n / FilesPerFolder:D4}"), $"tmp{n:X6}.tmp")));
        list.Add(new("documents-notes", 12, (_, n) => (Path.Combine(k.Documents, "Notes", $"{2020 + n / FilesPerFolder % 7}", $"batch {n / FilesPerFolder:D4}"), $"note-{n:D6}.txt")));
        var repos = DevAndAiArea.ReposDir(c);
        list.Add(new("build-output", 10, (_, n) => (Path.Combine(repos, "WeatherApp", "WeatherApp", "obj", "Debug", $"chunk{n / FilesPerFolder:D4}"), $"gen{n:D6}.cache")));
        var gameAssets = k.SteamRoot is { } steam
            ? Path.Combine(steam, "steamapps", "common", "SomeGame", "SomeGame_Data", "StreamingAssets")
            : Path.Combine(k.ProgramFiles, "FakeApp", "resources", "assets");
        list.Add(new("app-assets", 12, (_, n) => (Path.Combine(gameAssets, $"pack{n / FilesPerFolder:D4}"), $"asset{n:D6}.bin")));
        return list;
    }

    /// <summary>SplitMix64 stream keyed by (seed, index): fast, deterministic, independent of thread scheduling.</summary>
    private static void Fill(Span<byte> span, int seed, int index)
    {
        ulong state = ((ulong)(uint)seed << 32) ^ (ulong)(uint)index ^ 0x9E3779B97F4A7C15UL;
        Span<byte> word = stackalloc byte[8];
        for (int i = 0; i < span.Length; i += 8)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            BinaryPrimitives.WriteUInt64LittleEndian(word, z);
            word[..Math.Min(8, span.Length - i)].CopyTo(span[i..]);
        }
    }
}
