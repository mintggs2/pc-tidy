using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Learners;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Organize.Rules;
using D = PcTidy.Features.Learning.Model.LearningDecision;

namespace PcTidy.Features.Learning.Tests;

public class SkipCounterTests
{
    [Fact]
    public void Three_of_three_skips_unticks()
    {
        var v = SkipCounter.Evaluate(new[] { D.Reject, D.Reject, D.Reject, D.Accept });
        Assert.True(v.UntickByDefault);
        Assert.Equal(3, v.Skipped);
        Assert.Contains("3 of the last 3", v.Reason);
    }

    [Fact]
    public void Two_skips_or_a_recent_keep_is_not_enough()
    {
        Assert.False(SkipCounter.Evaluate(new[] { D.Reject, D.Reject }).UntickByDefault);
        Assert.False(SkipCounter.Evaluate(new[] { D.Reject, D.Reject, D.Accept, D.Reject }).UntickByDefault);
        Assert.False(SkipCounter.Evaluate(new[] { D.Accept, D.Reject, D.Reject, D.Reject }).UntickByDefault);
        Assert.False(SkipCounter.Evaluate(Array.Empty<D>()).UntickByDefault);
    }

    [Fact]
    public void Undo_counts_double()
    {
        Assert.Equal("RRA", SkipCounter.Votes(new[] { D.Undo, D.Accept }));
        // one undo + one reject = three skip votes
        Assert.True(SkipCounter.Evaluate(new[] { D.Reject, D.Undo }).UntickByDefault);
        // an undo right after an accept is still only two of three
        Assert.False(SkipCounter.Evaluate(new[] { D.Undo, D.Accept }).UntickByDefault);
    }

    [Fact]
    public void Older_votes_from_totals_fill_the_window()
    {
        Assert.True(SkipCounter.Evaluate(new[] { D.Reject }, "RRA").UntickByDefault);
        Assert.False(SkipCounter.Evaluate(new[] { D.Reject }, "RAR").UntickByDefault);
        Assert.Equal("RRA", SkipCounter.KeepRecent("R", "RAAA"));
    }
}

public class RuleMinerTests
{
    private const string Downloads = @"C:\Users\tester\Downloads";
    private const string ThreeD = @"D:\3D";

    [Fact]
    public void Five_moves_on_two_days_all_to_one_folder_suggests_a_rule()
    {
        var c = Assert.Single(RuleMiner.Mine(Moves.Daily("blend", Downloads, ThreeD, 5)), x => x.Feature == MoveFeature.Extension);
        Assert.Equal("blend", c.Value);
        Assert.Equal(ThreeD, c.Destination);
        Assert.Equal(5, c.Moves);
        Assert.Equal(1.0, c.Confidence);
        Assert.Equal(5, c.Days);
        Assert.Equal(Downloads, c.CommonSource);
        Assert.Equal(3, c.Examples.Count);
    }

    [Fact]
    public void Four_moves_is_too_few()
        => Assert.DoesNotContain(RuleMiner.Mine(Moves.Daily("blend", Downloads, ThreeD, 4)), x => x.Feature == MoveFeature.Extension);

    [Fact]
    public void All_on_one_day_is_not_a_habit()
    {
        var sameDay = Enumerable.Range(0, 6).Select(i => Moves.Of($"f{i}.blend", Downloads, ThreeD, $"run{i}", Moves.Day0.AddMinutes(i * 30)));
        Assert.Empty(RuleMiner.Mine(sameDay));
    }

    [Fact]
    public void Eighty_percent_is_enough_seventy_is_not()
    {
        var eight = Moves.Daily("blend", Downloads, ThreeD, 8).Concat(Moves.Daily("blend", Downloads, @"D:\Other", 2, startDay: 20, idStart: 100));
        var c = Assert.Single(RuleMiner.Mine(eight), x => x.Feature == MoveFeature.Extension);
        Assert.Equal(0.8, c.Confidence, 3);
        Assert.Equal(10, c.TotalMoves);

        var seven = Moves.Daily("blend", Downloads, ThreeD, 7).Concat(Moves.Daily("blend", Downloads, @"D:\Other", 3, startDay: 20, idStart: 100));
        Assert.DoesNotContain(RuleMiner.Mine(seven), x => x.Feature == MoveFeature.Extension);
    }

    [Fact]
    public void A_big_one_off_tidy_counts_once()
    {
        // 30 files in one run + 3 more on later days = weight 1 + 3 = 4: below 5.
        var bulk = Enumerable.Range(0, 30).Select(i => Moves.Of($"f{i}.blend", Downloads, ThreeD, "bulk", Moves.Day0.AddSeconds(i), id: i + 1));
        var later = Moves.Daily("blend", Downloads, ThreeD, 3, startDay: 5, idStart: 100);
        Assert.Empty(RuleMiner.Mine(bulk.Concat(later)));

        // Two more later moves push it over: 1 + 5 = 6.
        var more = Moves.Daily("blend", Downloads, ThreeD, 5, startDay: 5, idStart: 100);
        var c = Assert.Single(RuleMiner.Mine(bulk.Concat(more)), x => x.Feature == MoveFeature.Extension);
        Assert.Equal(6, c.Moves, 3);
    }

    [Fact]
    public void A_small_run_still_counts_each_move()
    {
        var run = Enumerable.Range(0, 4).Select(i => Moves.Of($"f{i}.blend", Downloads, ThreeD, "small", Moves.Day0.AddSeconds(i), id: i + 1));
        var other = Moves.Daily("blend", Downloads, ThreeD, 1, startDay: 3, idStart: 50);
        var c = Assert.Single(RuleMiner.Mine(run.Concat(other)), x => x.Feature == MoveFeature.Extension);
        Assert.Equal(5, c.Moves);
        Assert.Equal(2, c.Days);
    }

    [Theory]
    [InlineData("IMG_20240101_1234.jpg", "IMG_*_*.jpg")]
    [InlineData("Screenshot 2026-11-03 101500.png", "Screenshot *-*-* *.png")]
    [InlineData("invoice.pdf", null)]
    [InlineData("2024-01-01.txt", null)]
    [InlineData("a1b.txt", null)]
    public void Name_pattern_strips_digits(string name, string? expected)
        => Assert.Equal(expected, MoveFeatures.NamePatternOf(name));

    [Fact]
    public void Name_pattern_rule_when_extensions_vary()
    {
        var moves = Enumerable.Range(0, 6).Select(i => Moves.Of($"Scan_{i:000}.{(i % 2 == 0 ? "pdf" : "png")}", Downloads, @"D:\Scans", $"r{i}", Moves.Day0.AddDays(i), id: i + 1));
        var rules = RuleMiner.Mine(moves);
        // Neither extension has 5 moves, the name pattern doesn't either (it keeps the extension), the source folder does.
        var c = Assert.Single(rules);
        Assert.Equal(MoveFeature.SourceFolder, c.Feature);
    }

    [Fact]
    public void Download_site_and_overlap_are_deduplicated()
    {
        var moves = Enumerable.Range(0, 6).Select(i => Moves.Of($"model{i}.stl", Downloads, @"D:\Prints", $"r{i}", Moves.Day0.AddDays(i), host: "printables.com", id: i + 1)).ToList();
        var rules = RuleMiner.Mine(moves);
        // Extension, site, source folder and size bucket all explain the same 6 moves: only the simplest stays.
        var c = Assert.Single(rules);
        Assert.Equal(MoveFeature.Extension, c.Feature);

        var mixed = moves.Select((m, i) => m with { SourcePath = Path.Combine(Downloads, $"model{i}.{(i % 2 == 0 ? "stl" : "3mf")}") }).ToList();
        var site = Assert.Single(RuleMiner.Mine(mixed), x => x.Feature == MoveFeature.DownloadSite);
        Assert.Equal("printables.com", site.Value);
    }

    [Fact]
    public void Suggested_rule_is_a_valid_organize_rule_that_matches_the_files()
    {
        var c = Assert.Single(RuleMiner.Mine(Moves.Daily("blend", Downloads, ThreeD, 5)), x => x.Feature == MoveFeature.Extension);
        var s = SuggestedRule.From(c);
        var rule = s.CreateRule();
        Assert.False(rule.RunAutomatically);
        Assert.Equal(NameClashPolicy.AutoNumber, rule.ClashPolicy);
        Assert.Equal(RuleActionKind.MoveToFolder, rule.Action.Kind);
        Assert.Equal(ThreeD, rule.Action.Destination);
        Assert.Contains("all 5", s.Reason);
        Assert.Equal(@"Move .blend files from Downloads to D:\3D", s.Title);

        var now = Moves.Day0;
        FileFacts Facts(string path) => new(new FileEntry(path, Path.GetFileName(path), false, 10, FileAttributes.Normal, now, now, now, 1), Downloads, ScanDepth.Quick);
        Assert.True(RuleEvaluator.Matches(rule.Conditions, Facts(Path.Combine(Downloads, "chair.blend")), now));
        Assert.False(RuleEvaluator.Matches(rule.Conditions, Facts(Path.Combine(Downloads, "chair.fbx")), now));
        Assert.False(RuleEvaluator.Matches(rule.Conditions, Facts(@"C:\Users\tester\Desktop\chair.blend"), now));
    }

    [Fact]
    public void Size_bucket_rule_uses_inclusive_bounds()
    {
        var s = new SuggestedRule
        {
            Id = "x", Feature = MoveFeature.SizeBucket, Value = "1mb-100mb", Destination = @"D:\Mid", Name = "Mid",
            Moves = 5, TotalMoves = 5, Days = 2, FirstUtc = Moves.Day0, Examples = Array.Empty<string>(),
        };
        var c = Assert.Single(s.CreateRule().Conditions.Conditions);
        Assert.Equal(MoveFeatures.MB, c.MinBytes);
        Assert.Equal(100 * MoveFeatures.MB - 1, c.MaxBytes);
    }

    [Fact]
    public void Folding_into_totals_gives_the_same_suggestion()
    {
        var moves = Moves.Daily("blend", Downloads, ThreeD, 6).ToList();
        var raw = Assert.Single(RuleMiner.Mine(moves), x => x.Feature == MoveFeature.Extension);
        // Totals carry no move ids; the same moves still give one suggestion, not one per feature.
        var folded = Assert.Single(RuleMiner.Mine(Array.Empty<MoveEvent>(), RuleMiner.Fold(moves)));
        Assert.Equal(MoveFeature.Extension, folded.Feature);
        Assert.Equal(raw.Moves, folded.Moves);
        Assert.Equal(raw.Days, folded.Days);
        Assert.Equal(raw.CommonSource, folded.CommonSource);
        Assert.Equal(raw.Examples, folded.Examples);
    }
}

public class FrecencyTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Half_life_is_fourteen_days()
    {
        Assert.Equal(1.0, Frecency.Weight(Now, Now), 6);
        Assert.Equal(0.5, Frecency.Weight(Now.AddDays(-14), Now), 6);
        Assert.Equal(0.25, Frecency.Weight(Now.AddDays(-28), Now), 6);
    }

    [Fact]
    public void Recent_beats_many_old_visits()
    {
        var visits = Enumerable.Range(0, 3).Select(_ => new FolderVisit(@"D:\Old", Now.AddDays(-60), VisitSource.PcTidy))
            .Append(new FolderVisit(@"D:\New", Now.AddDays(-1), VisitSource.PcTidy))
            .Append(new FolderVisit(@"D:\Faded", Now.AddDays(-200), VisitSource.PcTidy));
        var ranked = Frecency.Rank(visits, null, Now);
        Assert.Equal(new[] { @"D:\New", @"D:\Old" }, ranked.Select(r => r.Folder));
        Assert.Equal(3, ranked[1].Visits);
    }

    [Fact]
    public void Folded_totals_keep_fading_the_same_way()
    {
        var visits = new[] { new FolderVisit(@"D:\A", Now.AddDays(-40), VisitSource.PcTidy), new FolderVisit(@"D:\A", Now.AddDays(-35), VisitSource.Move) };
        var foldedAt = Now.AddDays(-5);
        var totals = Frecency.Fold(@"D:\A", visits, null, foldedAt);
        var fromRaw = Frecency.Rank(visits, null, Now, 1).Single().Score;
        var fromTotals = Frecency.Rank(Array.Empty<FolderVisit>(), new[] { totals }, Now, 1).Single().Score;
        Assert.Equal(fromRaw, fromTotals, 4);
    }
}

public class DriveTrendTests
{
    private static readonly DateTime Start = new(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
    private const long GB = 1L << 30;

    private static List<FreeSpaceSample> Fill(int days, long startFree, long perDay, Func<int, long>? noise = null)
        => Enumerable.Range(0, days).Select(i => new FreeSpaceSample(@"C:\", Start.AddDays(i), 500 * GB, startFree - perDay * i + (noise?.Invoke(i) ?? 0))).ToList();

    [Fact]
    public void Steady_fill_gives_days_until_full()
    {
        var samples = Fill(30, 100 * GB, 1 * GB);
        var f = DriveTrend.Evaluate(@"C:\", samples, Array.Empty<RunMark>(), Start.AddDays(30));
        Assert.Equal(GB, f.BytesPerDay, 0);
        // 71 GB left at 1 GB a day
        Assert.Equal(71, f.DaysUntilFull);
        Assert.Contains("full in about 71 days", f.Reason);
    }

    [Fact]
    public void Fewer_than_fourteen_samples_never_forecasts()
    {
        var f = DriveTrend.Evaluate(@"C:\", Fill(13, 100 * GB, 5 * GB), Array.Empty<RunMark>(), Start.AddDays(13));
        Assert.Null(f.DaysUntilFull);
        Assert.Contains("13 of 14", f.Reason);
        Assert.NotNull(DriveTrend.Evaluate(@"C:\", Fill(14, 100 * GB, 5 * GB), Array.Empty<RunMark>(), Start.AddDays(14)).DaysUntilFull);
    }

    [Fact]
    public void Emptying_or_flat_drive_never_forecasts()
    {
        Assert.Null(DriveTrend.Evaluate(@"C:\", Fill(20, 100 * GB, -1 * GB), Array.Empty<RunMark>(), Start.AddDays(20)).DaysUntilFull);
        Assert.Null(DriveTrend.Evaluate(@"C:\", Fill(20, 100 * GB, 0), Array.Empty<RunMark>(), Start.AddDays(20)).DaysUntilFull);
    }

    [Fact]
    public void Theil_Sen_ignores_outliers()
    {
        // Three wild days (a 40 GB game download that was later deleted, a bad reading) don't move the slope.
        var samples = Fill(30, 200 * GB, 1 * GB, i => i is 7 or 15 or 22 ? -40 * GB : 0);
        var f = DriveTrend.Evaluate(@"C:\", samples, Array.Empty<RunMark>(), Start.AddDays(30));
        Assert.InRange(f.BytesPerDay, 0.95 * GB, 1.05 * GB);
        // Ordinary least squares would be pulled noticeably; the median isn't.
    }

    [Fact]
    public void Jumps_caused_by_PC_Tidy_runs_are_left_out()
    {
        // Filling 1 GB a day, but half-way PC Tidy cleaned 60 GB: without the fix the drive looks like it empties.
        var samples = Fill(30, 100 * GB, 1 * GB, i => i >= 15 ? 60 * GB : 0);
        var run = new RunMark("clean", Start.AddDays(14.5), new[] { @"C:\" });
        var with = DriveTrend.Evaluate(@"C:\", samples, new[] { run }, Start.AddDays(30));
        Assert.Equal(GB, with.BytesPerDay, 0);
        Assert.NotNull(with.DaysUntilFull);

        // The same run recorded for another drive isn't taken out of C:, so C: looks like it's emptying.
        var other = DriveTrend.Evaluate(@"C:\", samples, new[] { run with { Drives = new[] { @"D:\" } } }, Start.AddDays(30));
        Assert.True(other.BytesPerDay < 0);
        Assert.Null(other.DaysUntilFull);
    }

    [Fact]
    public void Only_the_last_ninety_days_count()
    {
        var old = Fill(40, 300 * GB, -5 * GB); // emptying long ago
        var recent = Enumerable.Range(0, 20).Select(i => new FreeSpaceSample(@"C:\", Start.AddDays(120 + i), 500 * GB, 100 * GB - i * GB));
        var f = DriveTrend.Evaluate(@"C:\", old.Concat(recent), Array.Empty<RunMark>(), Start.AddDays(140));
        Assert.Equal(20, f.Samples);
        Assert.Equal(GB, f.BytesPerDay, 0);
    }
}

public class UnusedAppsTests
{
    private static readonly DateTime Since = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AppObservation App(string key, DateTime firstSeen, DateTime? lastRun = null, DateTime? changed = null)
        => new(key, key, AppSource.UserAssist, firstSeen, 3, lastRun, changed);

    [Fact]
    public void Nothing_is_claimed_before_thirty_days()
    {
        var r = UnusedApps.Evaluate(new[] { App("a", Since) }, Since, Since.AddDays(29));
        Assert.Empty(r.Apps);
        Assert.Equal(1, r.DaysUntilReady);
        Assert.Equal(29, r.DaysWatched);
    }

    [Fact]
    public void Never_seen_used_says_how_long_watched_not_never()
    {
        var r = UnusedApps.Evaluate(new[] { App("a", Since) }, Since, Since.AddDays(45));
        var a = Assert.Single(r.Apps);
        Assert.Null(a.LastUsedUtc);
        Assert.Equal(45, a.DaysUnused);
        Assert.Contains("45 days it has been watching", a.Reason);
        Assert.DoesNotContain("never", a.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Old_launch_is_reported_recent_launch_is_not()
    {
        var now = Since.AddDays(40);
        var r = UnusedApps.Evaluate(new[]
        {
            App("old", Since, lastRun: Since.AddDays(-200)),
            App("recent", Since, lastRun: now.AddDays(-3)),
            App("counted", Since, changed: now.AddDays(-10)),
        }, Since, now);
        var a = Assert.Single(r.Apps);
        Assert.Equal("old", a.Key);
        Assert.Equal(240, a.DaysUnused);
    }

    [Fact]
    public void An_app_seen_late_needs_its_own_thirty_days()
    {
        var now = Since.AddDays(60);
        var r = UnusedApps.Evaluate(new[] { App("new", now.AddDays(-10)) }, Since, now);
        Assert.Empty(r.Apps);
    }

    [Fact]
    public void Observe_notices_a_launch_count_going_up()
    {
        var t0 = Since;
        var first = UnusedApps.Observe(null, new AppUsage("a", "A", AppSource.UserAssist, 3, null), t0);
        Assert.Null(first.LastCountChangeUtc);
        var same = UnusedApps.Observe(first, new AppUsage("a", "A", AppSource.UserAssist, 3, null), t0.AddDays(1));
        Assert.Null(same.LastCountChangeUtc);
        var used = UnusedApps.Observe(same, new AppUsage("a", "A", AppSource.UserAssist, 4, null), t0.AddDays(2));
        Assert.Equal(t0.AddDays(2), used.LastCountChangeUtc);
        Assert.Equal(t0, used.FirstSeenUtc);
    }
}

public class IdleTests
{
    private sealed class Signals : IIdleSignals
    {
        public uint TickCount { get; set; }
        public uint? LastInputTick { get; set; }
        public UserNotificationState NotificationState { get; set; } = UserNotificationState.AcceptsNotifications;
        public bool? OnBattery { get; set; } = false;
    }

    [Fact]
    public void Idle_time_survives_tick_wraparound()
    {
        // last input 5 s before the 32-bit tick counter wrapped, now 10 s after
        Assert.Equal(TimeSpan.FromSeconds(15), IdleDetector.IdleTime(10_000, uint.MaxValue - 4_999));
        var s = new Signals { TickCount = 10_000, LastInputTick = uint.MaxValue - 20 * 60_000 };
        var state = new IdleDetector(s).Check(TimeSpan.FromMinutes(10));
        Assert.True(state.IsIdle);
        Assert.InRange(state.IdleFor.TotalMinutes, 20, 21);
    }

    [Fact]
    public void Recent_input_is_not_idle()
        => Assert.False(new IdleDetector(new Signals { TickCount = 100_000, LastInputTick = 90_000 }).Check(TimeSpan.FromMinutes(1)).IsIdle);

    [Theory]
    [InlineData(UserNotificationState.RunningD3DFullScreen)]
    [InlineData(UserNotificationState.Busy)]
    [InlineData(UserNotificationState.PresentationMode)]
    [InlineData(UserNotificationState.App)]
    public void Never_during_full_screen_apps(UserNotificationState state)
    {
        var s = new Signals { TickCount = 10_000_000, LastInputTick = 0, NotificationState = state };
        Assert.False(new IdleDetector(s).Check().IsIdle);
    }

    [Fact]
    public void Never_on_battery()
    {
        var s = new Signals { TickCount = 10_000_000, LastInputTick = 0, OnBattery = true };
        var state = new IdleDetector(s).Check();
        Assert.False(state.IsIdle);
        Assert.Contains("battery", state.Reason);
    }

    [Fact]
    public void Unknown_input_time_is_not_idle()
        => Assert.False(new IdleDetector(new Signals { TickCount = 5, LastInputTick = null }).Check().IsIdle);

    [Fact]
    public void Locked_pc_counts_as_idle()
        => Assert.True(new IdleDetector(new Signals { TickCount = 1000, LastInputTick = 900, NotificationState = UserNotificationState.NotPresent }).Check().IsIdle);

    [Fact]
    public void Quiet_hours_need_enough_mostly_idle_checks()
    {
        var q = QuietHoursLearner.Evaluate(new[]
        {
            new IdleHourCounts(2, 5, 5), new IdleHourCounts(3, 9, 10), new IdleHourCounts(4, 4, 4), new IdleHourCounts(14, 1, 10),
        });
        Assert.Equal(new[] { 2, 3 }, q.Hours);
        Assert.Equal(29, q.Samples);
    }
}
