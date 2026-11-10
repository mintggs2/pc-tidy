using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Learning.Store;
using D = PcTidy.Features.Learning.Model.LearningDecision;

namespace PcTidy.Features.Learning.Tests;

public class StoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly TestFolder _box = new("store");
    private readonly TestClock _clock = new(T0);

    public void Dispose() => _box.Dispose();

    private LearningStore NewStore() => new(_box.Db, _clock);

    [Fact]
    public void Lives_in_the_learning_folder_of_the_data_root()
    {
        Assert.Equal(Path.Combine(@"X:\Data", "learning", "learned.db"), LearningStore.PathFor(@"X:\Data"));
        var store = NewStore();
        Assert.True(File.Exists(_box.Db));
        Assert.Equal(T0, store.StartedUtc);
        Assert.Null(store.OpenProblem);
    }

    [Fact]
    public void Round_trips_every_kind_of_event()
    {
        var store = NewStore();
        store.AddDecision(new DecisionEvent("cleanup", "chrome", D.Reject, T0, "r1", "Chrome cache", "Browser caches"));
        store.AddMinedRun(new RunMark("r1", T0, new[] { @"C:\" }),
            new[] { Moves.Of("a.blend", @"C:\Dl", @"D:\3D", "r1", T0, 10, "example.com") },
            new[] { new FolderVisit(@"D:\3D", T0, VisitSource.Move) });
        store.AddFreeSpace(new FreeSpaceSample(@"C:\", T0, 100, 40));
        store.AddFreeSpace(new FreeSpaceSample(@"C:\", T0.AddHours(3), 100, 30)); // same day: replaces
        store.SaveApps(new[] { new AppObservation("app", "App", AppSource.Steam, T0, 2, null, T0) });
        store.AddIdleSample(3, true);
        store.AddIdleSample(3, false);
        store.SetItemState(new LearnedItemState { ItemId = "x", Off = true, EditedName = "N" });

        var reopened = NewStore();
        var d = Assert.Single(reopened.Decisions());
        Assert.Equal(("Browser caches", "Chrome cache", "r1"), (d.GroupTitle, d.Example, d.RunId));
        var m = Assert.Single(reopened.Moves());
        Assert.Equal(("example.com", 10L), (m.Host, m.Size));
        Assert.True(reopened.IsRunMined("r1"));
        Assert.Equal(@"C:\", Assert.Single(Assert.Single(reopened.RunMarks()).Drives));
        Assert.Equal(30, Assert.Single(reopened.FreeSpace()).FreeBytes);
        Assert.Equal(T0, Assert.Single(reopened.Apps()).LastCountChangeUtc);
        Assert.Equal(new IdleHourCounts(3, 1, 2), Assert.Single(reopened.IdleHours()));
        Assert.True(reopened.ItemStates()["X"].Off);
        Assert.Single(reopened.FolderVisits());
        Assert.Empty(reopened.AcceptedInRun("r1")); // a reject is not an accept
    }

    [Fact]
    public void Raw_events_older_than_ninety_days_become_totals()
    {
        var store = NewStore();
        // Old: 3 rejects 100+ days ago; new: nothing yet.
        for (int i = 0; i < 3; i++) store.AddDecision(new DecisionEvent("cleanup", "chrome", D.Reject, T0.AddDays(i), Example: $"item{i}", GroupTitle: "Browser caches"));
        store.AddDecision(new DecisionEvent("cleanup", "chrome", D.Accept, T0.AddDays(95)));
        var oldMoves = Moves.Daily("blend", @"C:\Dl", @"D:\3D", 6).Select(m => m with { Utc = T0.AddDays(m.Utc.Subtract(Moves.Day0).TotalDays) }).ToList();
        foreach (var g in oldMoves.GroupBy(m => m.RunId)) store.AddMinedRun(new RunMark(g.Key, g.First().Utc, new[] { @"C:\" }), g, Array.Empty<FolderVisit>());
        for (int i = 0; i < 5; i++) store.AddFreeSpace(new FreeSpaceSample(@"C:\", T0.AddDays(i), 100, 50 + i));
        // Ten visits long ago: faded, but together still above the cut-off.
        store.AddFolderVisits(Enumerable.Range(0, 10).Select(i => new FolderVisit(@"D:\Old", T0.AddDays(i), VisitSource.PcTidy)));

        var skipBefore = SkipCounter.Evaluate(store.Decisions().Select(x => x.Decision));
        var rulesBefore = RuleMiner.Mine(store.Moves(), store.MoveTotals());
        _clock.UtcNow = T0.AddDays(100);
        var foldersBefore = Frecency.Rank(store.FolderVisits(), store.FolderTotals(), _clock.UtcNow);

        var report = store.Maintain();
        Assert.Equal(3, report.Decisions);
        Assert.Equal(6, report.Moves);
        Assert.Equal(5, report.FreeSpaceSamples);
        Assert.Equal(10, report.FolderVisits);

        // Raw: only the new decision is left; the rest are totals.
        Assert.Equal(D.Accept, Assert.Single(store.Decisions()).Decision);
        var totals = Assert.Single(store.DecisionTotals());
        Assert.Equal((0, 3, 0, "RRR", "Browser caches"), (totals.Accepts, totals.Rejects, totals.Undos, totals.RecentVotes, totals.GroupTitle));
        Assert.Equal(3, RuleMiner.SplitExamples(totals.Examples).Count());
        Assert.Empty(store.Moves());
        Assert.NotEmpty(store.MoveTotals());
        Assert.Empty(store.FreeSpace());
        var month = Assert.Single(store.FreeSpaceMonths());
        Assert.Equal((52.0, 5), (month.AvgFree, month.Samples));
        Assert.Empty(store.FolderVisits());

        // The learners give the same answers from totals as from raw events.
        var skipAfter = SkipCounter.Evaluate(store.Decisions().Select(x => x.Decision), totals.RecentVotes);
        Assert.Equal(2, skipBefore.Skipped); // before: accept + 2 rejects in the window
        Assert.False(skipAfter.UntickByDefault);
        Assert.Equal((skipBefore.Skipped, skipBefore.Of), (skipAfter.Skipped, skipAfter.Of));
        var ruleBefore = Assert.Single(rulesBefore);
        var ruleAfter = Assert.Single(RuleMiner.Mine(store.Moves(), store.MoveTotals()));
        Assert.Equal((ruleBefore.Key, ruleBefore.Moves, ruleBefore.Days), (ruleAfter.Key, ruleAfter.Moves, ruleAfter.Days));
        var folderAfter = Frecency.Rank(store.FolderVisits(), store.FolderTotals(), _clock.UtcNow);
        Assert.Equal(foldersBefore.Single().Score, folderAfter.Single().Score, 4);

        // Running it again changes nothing.
        Assert.Equal(new LearningStore.MaintenanceReport(0, 0, 0, 0), store.Maintain());
    }

    [Fact]
    public void Maintenance_merges_into_existing_totals()
    {
        var store = NewStore();
        store.AddDecision(new DecisionEvent("f", "g", D.Accept, T0));
        _clock.UtcNow = T0.AddDays(91);
        store.Maintain();
        store.AddDecision(new DecisionEvent("f", "g", D.Reject, T0.AddDays(10)));
        store.AddDecision(new DecisionEvent("f", "g", D.Undo, T0.AddDays(11)));
        _clock.UtcNow = T0.AddDays(120);
        store.Maintain();
        var t = Assert.Single(store.DecisionTotals());
        Assert.Equal((1, 1, 1), (t.Accepts, t.Rejects, t.Undos));
        Assert.Equal("RRR", t.RecentVotes); // undo (RR) + reject (R); the older accept falls out of the window
        Assert.Equal(T0, t.FirstUtc);
    }

    [Fact]
    public void Forget_everything_deletes_the_file_and_starts_fresh()
    {
        var store = NewStore();
        store.AddDecision(new DecisionEvent("f", "g", D.Reject, T0));
        store.Paused = true;
        _clock.UtcNow = T0.AddDays(5);
        int changed = 0;
        store.Changed += (_, _) => changed++;
        var deletedSeen = false;
        store.Changed += (_, _) => deletedSeen = store.Decisions().Count == 0;

        store.ForgetEverything();

        Assert.True(deletedSeen);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(_box.Db)); // a fresh, empty file
        Assert.Empty(store.Decisions());
        Assert.False(store.Paused);
        Assert.Equal(T0.AddDays(5), store.StartedUtc);
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(_box.Db)!), f => Assert.StartsWith("learned.db", Path.GetFileName(f)));
    }

    [Fact]
    public void A_damaged_file_is_reported_not_deleted_until_forget_everything()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_box.Db)!);
        File.WriteAllText(_box.Db, "this is not a database, just some text that is long enough to have a header");
        var store = NewStore();
        Assert.NotNull(store.OpenProblem);
        Assert.True(File.Exists(_box.Db));
        Assert.ThrowsAny<Exception>(() => store.Decisions());

        store.ForgetEverything();
        Assert.Null(store.OpenProblem);
        Assert.Empty(store.Decisions());
    }

    [Fact]
    public void Deleting_evidence_removes_only_that_evidence()
    {
        var store = NewStore();
        store.AddDecision(new DecisionEvent("f", "g", D.Reject, T0));
        store.AddDecision(new DecisionEvent("f", "other", D.Reject, T0));
        store.DeleteDecisions("f", "g");
        Assert.Equal("other", Assert.Single(store.Decisions()).GroupId);

        store.AddFreeSpace(new FreeSpaceSample(@"C:\", T0, 1, 1));
        store.AddFreeSpace(new FreeSpaceSample(@"D:\", T0, 1, 1));
        store.DeleteFreeSpace(@"c:\");
        Assert.Equal(@"D:\", Assert.Single(store.FreeSpace()).Drive);

        store.AddFolderVisit(new FolderVisit(@"D:\A", T0, VisitSource.PcTidy));
        store.AddFolderVisit(new FolderVisit(@"D:\B", T0, VisitSource.PcTidy));
        store.DeleteFolder(@"d:\a");
        Assert.Equal(@"D:\B", Assert.Single(store.FolderVisits()).Folder);
    }
}
