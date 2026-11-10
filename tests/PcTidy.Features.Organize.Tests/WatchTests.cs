using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.Watch;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class WatchTests
{
    [Fact]
    public void Quiet_period_tracker_waits_ten_minutes_after_the_last_change()
    {
        var t0 = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var tracker = new QuietPeriodTracker(TimeSpan.FromMinutes(10));
        tracker.Touch(@"C:\d\a.zip", t0);
        Assert.False(tracker.IsQuiet(@"C:\d\a.zip", t0.AddMinutes(9)));
        tracker.Touch(@"C:\D\A.ZIP", t0.AddMinutes(9)); // still being written; case-insensitive
        Assert.Empty(tracker.Ready(t0.AddMinutes(12)));
        Assert.Contains(@"C:\d\a.zip", tracker.NotQuiet(t0.AddMinutes(12)), StringComparer.OrdinalIgnoreCase);
        Assert.Single(tracker.Ready(t0.AddMinutes(19)));
        Assert.True(tracker.IsQuiet(@"C:\d\untracked", t0));
        tracker.Forget(@"c:\d\a.zip");
        Assert.Equal(0, tracker.Count);
    }

    private static OrganizeSettings AutomaticPictures()
    {
        var s = new OrganizeSettings();
        s.Rules.Single(r => r.Id == DefaultRules.CategoryRuleId("Pictures")).RunAutomatically = true;
        return s;
    }

    [Fact]
    public async Task New_files_move_only_after_ten_quiet_minutes_and_only_for_automatic_rules()
    {
        using var sb = new Sandbox();
        var flow = new FakeFlowHost();
        var settings = AutomaticPictures();
        await using var watch = new WatchService(sb.Planner(), flow, () => settings, sb.Clock, new WatchTiming());
        watch.Start(new[] { sb.Downloads });
        await watch.StopAsync(); // keep the folder list, drive cycles by hand with the fake clock

        var photo = sb.File("new.jpg", ageDays: 0);   // just arrived
        var doc = sb.File("new.pdf", ageDays: 0);     // Documents isn't automatic
        sb.Clock.UtcNow = DateTime.UtcNow;
        watch.NotifyChanged(photo);
        watch.NotifyChanged(doc);

        Assert.Equal(WatchCycleResult.Idle, await watch.RunCycleAsync(false, CancellationToken.None));
        sb.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(WatchCycleResult.Idle, await watch.RunCycleAsync(false, CancellationToken.None));
        // A full re-check now still leaves both alone: they changed less than 10 minutes ago.
        var early = await watch.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, early.RanAutomatically);
        Assert.Empty(flow.Approved);

        IReadOnlyList<Planning.PlannedAction>? pending = null;
        watch.PendingChanged += (_, e) => pending = e.Pending;
        sb.Clock.Advance(TimeSpan.FromMinutes(6));
        var result = await watch.RunCycleAsync(false, CancellationToken.None);

        Assert.Equal(1, result.RanAutomatically);
        var plan = Assert.Single(flow.Approved);
        Assert.Contains(plan.Items, i => i.Kind == ActionKind.MovePath && i.SourcePath == photo);
        Assert.DoesNotContain(plan.Items, i => i.SourcePath == doc);
        Assert.Equal(OrganizeModule.FeatureId, plan.FeatureId);
        Assert.NotNull(pending);
        Assert.Equal(doc, Assert.Single(pending!).SourcePath);
        Assert.Equal(1, watch.AutomaticRuns);
    }

    [Fact]
    public async Task Each_watched_folder_is_its_own_run_and_quarantine_never_runs_automatically()
    {
        using var sb = new Sandbox(Core.SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        var settings = AutomaticPictures();
        var oldInstallers = settings.Rules.Single(r => r.Id == DefaultRules.OldInstallersId);
        oldInstallers.RunAutomatically = true; // but not AllowAutomaticQuarantine
        await using var watch = new WatchService(sb.Planner(), flow, () => settings, sb.Clock);
        watch.Start(new[] { sb.Downloads, sb.Locations.Desktop });
        await watch.StopAsync();

        sb.File("a.png");
        sb.File("b.png", folder: sb.Locations.Desktop);
        sb.File("ancient.msi", ageDays: 90);
        var result = await watch.RunCycleAsync(true, CancellationToken.None);

        Assert.Equal(2, flow.Approved.Count);
        Assert.Equal(2, result.Runs.Count);
        Assert.All(flow.Approved, p => Assert.DoesNotContain(p.Items, i => i.Kind == ActionKind.QuarantinePath));
        Assert.Contains(watch.PendingActions, a => a.Name == "ancient.msi");
    }

    [Fact]
    public async Task Files_that_changed_after_planning_are_not_moved()
    {
        using var sb = new Sandbox();
        var flow = new FakeFlowHost();
        var settings = AutomaticPictures();
        // The file was quiet, then got written again: every condition is re-checked, so it waits again.
        await using var watch = new WatchService(sb.Planner(), flow, () => settings, sb.Clock);
        watch.Start(new[] { sb.Downloads });
        await watch.StopAsync();
        var photo = sb.File("pic.jpg");
        watch.NotifyChanged(photo);
        sb.Clock.Advance(TimeSpan.FromMinutes(11));
        System.IO.File.AppendAllText(photo, "more"); // written again just now (real time)
        sb.Clock.UtcNow = DateTime.UtcNow;            // quiet period by the file's own time not over
        watch.NotifyChanged(photo);
        sb.Clock.Advance(TimeSpan.FromMinutes(3));
        await watch.RunCycleAsync(true, CancellationToken.None);
        Assert.Empty(flow.Approved);
    }

    [Fact]
    public async Task Start_and_stop_return_quickly_and_never_block()
    {
        using var sb = new Sandbox();
        var flow = new FakeFlowHost();
        await using var watch = new WatchService(sb.Planner(), flow, () => new OrganizeSettings(), sb.Clock);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        watch.Start(new[] { sb.Downloads, System.IO.Path.Combine(sb.Root, "missing") });
        Assert.True(watch.IsRunning);
        Assert.Single(watch.Folders); // missing folders are ignored, not an error
        await watch.StopAsync();
        Assert.False(watch.IsRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        watch.Start(new[] { sb.Downloads }); // restartable
        Assert.True(watch.IsRunning);
    }
}
