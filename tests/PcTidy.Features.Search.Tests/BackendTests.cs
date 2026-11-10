using PcTidy.Features.Search.Backends;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>Everything when it's there, the own index when it isn't, and never an error in between.</summary>
public class BackendTests
{
    private static readonly IndexSnapshot Snapshot = new(new[]
    {
        Synthetic.Tree(@"X:\", (@"Docs\report.pdf", 10, Synthetic.Base), (@"Docs\notes.txt", 5, Synthetic.Base)),
    }, 1);

    private sealed class NoEverything : IEverythingLocator
    {
        public int Calls;
        public EverythingInstall? Locate() { Calls++; return null; }
    }

    private sealed class FakeInstall(string sdk) : IEverythingLocator
    {
        public EverythingInstall? Locate() => new(@"X:\Everything\Everything.exe", sdk);
    }

    private sealed class Failing : ISearchBackend
    {
        public string Name => "Everything";
        public bool IsAvailable => true;
        public Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct) => throw new InvalidOperationException("closed");
    }

    private sealed class Throwing : ISearchBackend
    {
        public string Name => "Everything";
        public bool IsAvailable => throw new DllNotFoundException();
        public Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Everything_absent_falls_back_to_the_own_index_silently()
    {
        var locator = new NoEverything();
        var everything = new EverythingBackend(locator);
        Assert.False(everything.IsAvailable);
        var router = new SearchRouter(new OwnIndexBackend(() => Snapshot), everything);
        Assert.IsType<OwnIndexBackend>(router.Active);
        var r = await router.SearchAsync("report", 100, CancellationToken.None);
        Assert.Equal(QueryEngine.BackendName, r.Backend);
        Assert.Equal(1, r.Count);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Availability_is_rechecked_only_every_few_seconds()
    {
        var locator = new NoEverything();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var everything = new EverythingBackend(locator, clock);
        for (int i = 0; i < 50; i++) Assert.False(everything.CheckAvailable());
        Assert.Equal(1, locator.Calls);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(everything.CheckAvailable());
        Assert.Equal(2, locator.Calls);
    }

    /// <summary>A locator that hangs, like looking for Everything's window while it shows a dialog of its own.</summary>
    private sealed class HangingLocator : IEverythingLocator
    {
        public readonly ManualResetEventSlim Release = new(false);
        public EverythingInstall? Locate() { Release.Wait(TimeSpan.FromSeconds(30)); return null; }
    }

    [Fact]
    public void The_cheap_availability_answer_never_waits_for_a_slow_check()
    {
        var locator = new HangingLocator();
        var everything = new EverythingBackend(locator);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) Assert.False(everything.IsAvailable); // what the UI thread asks
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"IsAvailable took {sw.Elapsed}");
        locator.Release.Set();
    }

    /// <summary>Everything stuck on a slow query (content: or regex: over a big drive): never answers in time.</summary>
    private sealed class Stuck : ISearchBackend
    {
        public readonly TaskCompletionSource<SearchResults> Never = new();
        public string Name => "Everything";
        public bool IsAvailable => true;
        public Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct) => Never.Task; // ignores the token, like QueryW
    }

    [Fact]
    public async Task A_slow_Everything_query_is_given_up_on_and_the_own_index_answers()
    {
        var router = new SearchRouter(new OwnIndexBackend(() => Snapshot), new Stuck(), everythingTimeout: TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await router.SearchAsync("report", 100, CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"waited {sw.Elapsed}");
        Assert.Equal(QueryEngine.BackendName, r.Backend);
        Assert.Equal(1, r.Count);
        Assert.Contains(r.Notes, n => n.Contains("taking too long"));
    }

    [Fact]
    public async Task Cancelling_a_search_stuck_in_Everything_returns_at_once()
    {
        var router = new SearchRouter(new OwnIndexBackend(() => Snapshot), new Stuck(), everythingTimeout: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.SearchAsync("report", 100, cts.Token));
    }

    [Fact]
    public void A_missing_or_bogus_SDK_file_is_just_not_available()
    {
        Assert.False(new EverythingBackend(new FakeInstall(@"X:\nowhere\Everything64.dll")).CheckAvailable());
        var bogus = Path.Combine(Path.GetTempPath(), "pctidy-search-" + Guid.NewGuid().ToString("N"), EverythingLocator.SdkFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bogus)!);
        try
        {
            File.WriteAllText(bogus, "not a DLL");
            Assert.False(new EverythingBackend(new FakeInstall(bogus)).CheckAvailable());
        }
        finally { Directory.Delete(Path.GetDirectoryName(bogus)!, recursive: true); }
    }

    [Fact]
    public void The_real_locator_never_throws()
    {
        // Read-only: looks for Everything's window on this PC. Either answer is fine; an exception is not.
        var found = new EverythingLocator().Locate();
        if (found is not null) Assert.True(File.Exists(found.SdkPath));
    }

    [Fact]
    public async Task Everything_failing_mid_search_is_answered_by_the_own_index_with_a_note()
    {
        var router = new SearchRouter(new OwnIndexBackend(() => Snapshot), new Failing());
        var r = await router.SearchAsync("notes", 100, CancellationToken.None);
        Assert.Equal(QueryEngine.BackendName, r.Backend);
        Assert.Equal(1, r.Count);
        Assert.Contains(r.Notes, n => n.Contains("Everything"));
        Assert.Equal(1, r.SortedBy(SearchSort.Size, true).Count);
    }

    [Fact]
    public async Task Everything_switched_off_or_throwing_on_check_uses_the_own_index()
    {
        var off = new SearchRouter(new OwnIndexBackend(() => Snapshot), new Failing(), useEverything: () => false);
        Assert.IsType<OwnIndexBackend>(off.Active);
        Assert.Empty((await off.SearchAsync("notes", 10, CancellationToken.None)).Notes);
        var throwing = new SearchRouter(new OwnIndexBackend(() => Snapshot), new Throwing());
        Assert.IsType<OwnIndexBackend>(throwing.Active);
    }

    [Fact]
    public void List_results_sort_and_number_rows()
    {
        var hits = new[]
        {
            new SearchHit(0, "b.txt", @"X:\b", false, 30, Synthetic.Base),
            new SearchHit(1, "a.txt", @"X:\c", false, 10, Synthetic.Base.AddDays(-1)),
            new SearchHit(2, "c", @"X:\a", true, 0, Synthetic.Base.AddDays(1)),
        };
        var r = new ListResults("Everything", hits, TimeSpan.Zero, Array.Empty<string>(), false);
        Assert.Equal("a.txt", r.SortedBy(SearchSort.Name, false)[0].Name);
        Assert.Equal("b.txt", r.SortedBy(SearchSort.Size, true)[0].Name);
        Assert.Equal("c", r.SortedBy(SearchSort.Folder, false)[0].Name);
        Assert.Equal("c", r.SortedBy(SearchSort.Modified, true)[0].Name);
        Assert.Equal(1, r.SortedBy(SearchSort.Name, false)[1].Ordinal);
    }
}
