using PcTidy.Core;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>Snapshot round-trip (own data only), the watcher debounce with a fake clock, and the refresh service.</summary>
[Collection("search-testbed")]
public class SnapshotAndRefreshTests
{
    // ------------------------------------------------------------------ snapshot store

    [Fact]
    public async Task Snapshot_round_trips_exactly_and_lives_only_in_DataRoot_search()
    {
        using var s = new SearchSandbox("snapshot", SandboxProfile.Minimal);
        var original = new IndexSnapshot(new[] { Synthetic.Generate(@"X:\Users\tester", 20_000, 1), Synthetic.Generate(@"Y:\", 5_000, 2) }, 3);
        var store = new SearchIndexStore(s.K.DataRoot);
        var before = Directory.EnumerateFileSystemEntries(s.Root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await store.SaveAsync(original, CancellationToken.None);

        var added = Directory.EnumerateFileSystemEntries(s.Root, "*", SearchOption.AllDirectories).Where(p => !before.Contains(p)).ToList();
        Assert.All(added, p => Assert.True(PathUtil.IsUnder(p, Path.Combine(s.K.DataRoot, "search")), p));
        Assert.Equal(Path.Combine(s.K.DataRoot, "search", "index.bin"), store.SnapshotPath);
        Assert.False(File.Exists(store.SnapshotPath + ".tmp"));

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(original.Segments.Count, loaded!.Segments.Count);
        for (int k = 0; k < original.Segments.Count; k++)
        {
            var a = original.Segments[k];
            var b = loaded.Segments[k];
            Assert.Equal(a.Root, b.Root);
            Assert.Equal(a.Count, b.Count);
            Assert.Equal(a.BuiltUtc, b.BuiltUtc);
            Assert.Equal(a.ArrayBytes, b.ArrayBytes);
            for (int i = 0; i < a.Count; i += 97)
            {
                Assert.Equal(a.FullPath(i), b.FullPath(i));
                Assert.Equal(a.SizeOf(i), b.SizeOf(i));
                Assert.Equal(a.ModifiedUtc(i), b.ModifiedUtc(i));
                Assert.Equal(a.AttributesOf(i), b.AttributesOf(i));
                Assert.Equal(a.ExtensionOf(i), b.ExtensionOf(i));
            }
        }
        var q = QueryParser.Parse("holiday ext:jpg size:>1mb");
        Assert.Equal(QueryEngine.Search(original, q).Count, QueryEngine.Search(loaded, q).Count);
    }

    [Fact]
    public async Task Damaged_or_foreign_snapshots_are_ignored_not_trusted()
    {
        using var s = new SearchSandbox("snapshot-bad", SandboxProfile.Minimal);
        var store = new SearchIndexStore(s.K.DataRoot);
        Assert.Null(store.TryLoad()); // none yet

        await store.SaveAsync(new IndexSnapshot(new[] { Synthetic.Generate(@"X:\", 3_000) }, 1), CancellationToken.None);
        var bytes = File.ReadAllBytes(store.SnapshotPath);
        bytes[bytes.Length / 2] ^= 0x5A; // one flipped byte in the middle
        File.WriteAllBytes(store.SnapshotPath, bytes);
        Assert.Null(store.TryLoad());

        File.WriteAllBytes(store.SnapshotPath, bytes[..100]); // cut short
        Assert.Null(store.TryLoad());

        File.WriteAllText(store.SnapshotPath, "not an index");
        Assert.Null(store.TryLoad());

        store.Clear();
        Assert.False(File.Exists(store.SnapshotPath));
    }

    // ------------------------------------------------------------------ debounce (fake clock)

    private static (RefreshScheduler Scheduler, ManualClock Clock) Scheduler()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var policy = new RefreshPolicy
        {
            QuietPeriod = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromMinutes(5),
            MinInterval = TimeSpan.FromMinutes(2), PeriodicRescan = TimeSpan.FromMinutes(30),
        };
        var s = new RefreshScheduler(clock, policy);
        s.Register("A", clock.GetUtcNow() - TimeSpan.FromMinutes(10));
        s.Register("B", clock.GetUtcNow() - TimeSpan.FromMinutes(10));
        return (s, clock);
    }

    [Fact]
    public void Hints_wait_for_a_quiet_period()
    {
        var (s, clock) = Scheduler();
        s.Hint("A");
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(s.TakeDue());
        s.Hint("A"); // still busy: the quiet period restarts
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(s.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "A" }, s.TakeDue());
        Assert.Empty(s.TakeDue()); // handed out once
    }

    [Fact]
    public void A_folder_that_never_goes_quiet_still_refreshes_after_the_max_delay()
    {
        var (s, clock) = Scheduler();
        for (int i = 0; i < 100; i++)
        {
            s.Hint("A");
            clock.Advance(TimeSpan.FromSeconds(4));
            if (s.TakeDue().Count > 0) { Assert.True((i + 1) * 4 >= 300, $"due too early at {(i + 1) * 4}s"); return; }
        }
        Assert.Fail("never became due");
    }

    [Fact]
    public void A_root_is_not_rescanned_for_hints_more_often_than_the_min_interval()
    {
        var (s, clock) = Scheduler();
        s.Hint("A");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(new[] { "A" }, s.TakeDue());
        s.Completed("A");
        s.Hint("A");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(s.TakeDue()); // rested only 11 s
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new[] { "A" }, s.TakeDue());
    }

    [Fact]
    public void Lost_events_make_a_root_due_and_hints_during_a_scan_are_not_lost()
    {
        var (s, clock) = Scheduler();
        s.Overflow("B");
        Assert.Equal(new[] { "B" }, s.TakeDue());
        // A change arrives while B's re-scan runs: B is due again afterwards.
        s.Hint("B");
        s.Completed("B");
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(new[] { "B" }, s.TakeDue());
    }

    [Fact]
    public void Every_root_is_rescanned_periodically_without_any_hint()
    {
        var (s, clock) = Scheduler();
        Assert.Empty(s.TakeDue());
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(new[] { "A", "B" }, s.TakeDue().OrderBy(x => x));
        Assert.Empty(s.TakeDue());
        Assert.False(s.HasPending("A"));
    }

    // ------------------------------------------------------------------ the service end to end (sandbox)

    private sealed class ManualHints : IChangeHintSource
    {
        public readonly List<(string Root, Action<string, string> Hint, Action<string> Overflow)> Watches = new();
        public IDisposable Watch(string root, Action<string, string> onHint, Action<string> onOverflow)
        {
            Watches.Add((root, onHint, onOverflow));
            return new Nothing();
        }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Service_builds_saves_warm_starts_and_refreshes_only_after_debounced_hints()
    {
        using var s = new SearchSandbox("service", SandboxProfile.Minimal);
        s.Put(Path.Combine(s.K.Downloads, "first.txt"), "1");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var hints = new ManualHints();
        var policy = new RefreshPolicy { QuietPeriod = TimeSpan.FromSeconds(10), MinInterval = TimeSpan.FromSeconds(30), PeriodicRescan = TimeSpan.FromHours(1) };
        var roots = new[] { PathUtil.Normalize(s.K.UserProfile) };
        var store = new SearchIndexStore(s.K.DataRoot);
        using (var service = new SearchIndexService(new IndexBuilder(s.Host.Walker, s.Host.Safety, s.K), store, () => roots, clock, policy, hints,
                   new[] { Path.Combine(s.K.UserProfile, "AppData") }))
        {
            await service.StartAsync();
            await service.TickAsync(CancellationToken.None); // no snapshot yet: built straight away
            Assert.Equal(1, Count(service, "first.txt"));
            Assert.True(File.Exists(store.SnapshotPath));
            Assert.Single(hints.Watches);

            // A new file + a hint: nothing happens until the quiet period (and the min interval) has passed.
            s.Put(Path.Combine(s.K.Downloads, "second.txt"), "2");
            hints.Watches[0].Hint(roots[0], Path.Combine(s.K.Downloads, "second.txt"));
            clock.Advance(TimeSpan.FromSeconds(5));
            await service.TickAsync(CancellationToken.None);
            Assert.Equal(0, Count(service, "second.txt"));
            clock.Advance(TimeSpan.FromSeconds(30));
            await service.TickAsync(CancellationToken.None);
            Assert.Equal(1, Count(service, "second.txt"));

            // Hints from folders that aren't indexed (AppData, PC Tidy's own data) are ignored.
            clock.Advance(TimeSpan.FromMinutes(5));
            hints.Watches[0].Hint(roots[0], Path.Combine(s.K.LocalAppData, "Temp", "x.tmp"));
            hints.Watches[0].Hint(roots[0], store.SnapshotPath);
            Assert.False(service.Scheduler.HasPending(roots[0]));

            var status = service.Status;
            Assert.True(status.Entries > 0);
            Assert.True(status.SnapshotBytes > 0);
            Assert.False(status.Building);
        }

        // Warm start: a new service answers from the saved snapshot before any walk.
        using var warm = new SearchIndexService(new IndexBuilder(s.Host.Walker, s.Host.Safety, s.K), store, () => roots, clock, policy, null);
        await warm.StartAsync();
        Assert.Equal(1, Count(warm, "second.txt"));
    }

    private static int Count(SearchIndexService service, string query) => QueryEngine.Search(service.Current, QueryParser.Parse(query)).Count;

    /// <summary>Counts every folder the walker opens, so a test can prove a refresh didn't walk the whole root.</summary>
    private sealed class CountingWalker(IFileWalker inner) : IFileWalker
    {
        public int Listings;
        public WalkStats Walk(string root, WalkOptions options, Func<FileEntry, bool> onEntry, CancellationToken ct)
        {
            Listings++;
            int descents = 0;
            var stats = inner.Walk(root, options, e =>
            {
                var go = onEntry(e);
                if (go && e.IsDirectory && options.Recurse) descents++;
                return go;
            }, ct);
            Listings += descents;
            return stats;
        }
    }

    private static HashSet<string> Paths(IndexSegment seg)
        => Enumerable.Range(0, seg.Count).Select(i => seg.FullPath(i) + "|" + seg.SizeOf(i)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_patch_of_the_changed_folders_matches_a_full_walk_and_lists_far_less()
    {
        using var s = new SearchSandbox("patch", SandboxProfile.Minimal);
        for (int i = 0; i < 30; i++) s.Put(Path.Combine(s.K.Documents, $"Project {i}", "sub", $"file{i}.txt"), "x");
        s.Put(Path.Combine(s.K.Downloads, "old.txt"), "old");
        s.Put(Path.Combine(s.K.Downloads, "gone.txt"), "bye");
        var root = PathUtil.Normalize(s.K.UserProfile);
        var walker = new CountingWalker(s.Host.Walker);
        var builder = new IndexBuilder(walker, s.Host.Safety, s.K);
        var before = builder.Build(root, DateTime.UtcNow, null, CancellationToken.None);
        int fullListings = walker.Listings;

        // In Downloads: one file grows, one is deleted, one arrives, and a whole folder is moved in.
        s.Put(Path.Combine(s.K.Downloads, "old.txt"), "now much longer");
        File.Delete(Path.Combine(s.K.Downloads, "gone.txt"));
        s.Put(Path.Combine(s.K.Downloads, "new.txt"), "n");
        s.Put(Path.Combine(s.K.Downloads, "Moved in", "deep", "inner.txt"), "i");
        // Hints name the changed items; the scheduler turns them into the folders that hold them.
        var changed = new[] { s.K.Downloads, Path.Combine(s.K.Downloads, "Moved in", "deep") };

        walker.Listings = 0;
        var patched = builder.Patch(before, changed, DateTime.UtcNow, CancellationToken.None);
        Assert.NotNull(patched);
        int patchListings = walker.Listings;
        var full = new IndexBuilder(s.Host.Walker, s.Host.Safety, s.K).Build(root, DateTime.UtcNow, null, CancellationToken.None);

        Assert.Equal(Paths(full), Paths(patched!));
        Assert.True(patchListings < fullListings / 4, $"patch opened {patchListings} folders, a full walk {fullListings}");
        // Parents still come before their children, so the segment is valid for every query.
        for (int i = 0; i < patched!.Count; i++) Assert.True(patched.ParentOf(i) < i);
    }

    [Fact]
    public void Hints_become_folders_to_list_and_lost_events_or_too_many_mean_a_full_walk()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var s = new RefreshScheduler(clock, new RefreshPolicy { QuietPeriod = TimeSpan.FromSeconds(10), MinInterval = TimeSpan.FromSeconds(30), PeriodicRescan = TimeSpan.FromHours(1) });
        s.Register(@"X:\", clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        s.Hint(@"X:\", @"X:\Photos\a.jpg");
        s.Hint(@"X:\", @"X:\Photos\b.jpg");
        clock.Advance(TimeSpan.FromSeconds(20));
        var due = Assert.Single(s.TakeDueChanges());
        Assert.Equal(new[] { @"X:\Photos" }, due.Folders);

        clock.Advance(TimeSpan.FromMinutes(1));
        s.Hint(@"X:\", @"X:\Photos\c.jpg");
        s.Overflow(@"X:\");
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(Assert.Single(s.TakeDueChanges()).Folders);

        clock.Advance(TimeSpan.FromMinutes(1));
        for (int i = 0; i <= RefreshScheduler.MaxChangedFolders; i++) s.Hint(@"X:\", $@"X:\F{i}\x.txt");
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(Assert.Single(s.TakeDueChanges()).Folders);
    }
}
