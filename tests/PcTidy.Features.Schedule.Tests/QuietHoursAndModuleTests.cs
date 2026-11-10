using PcTidy.Features.Schedule.Drift;
using PcTidy.Features.Schedule.Metrics;
using PcTidy.Features.Schedule.Reports;
using PcTidy.Features.Schedule.Runner;
using PcTidy.Features.Schedule.Storage;
using PcTidy.Features.Schedule.View;

namespace PcTidy.Features.Schedule.Tests;

public class QuietHoursAndModuleTests
{
    private static ScanFindings Found(long bytes, int drifted = 0, params string[] diskWarnings)
    {
        var spec = new RegistryValueSpec { Hive = RegistryHive.CurrentUser, KeyPath = "k", ValueName = "v" };
        return new ScanFindings
        {
            TimeUtc = DateTime.UtcNow, Checkup = drifted > 0,
            Sections = new[] { new SectionFinding("cleanup", "Clean", bytes, 1, 0, Array.Empty<string>(), null) },
            Drift = drifted > 0 ? new DriftReport(5, Enumerable.Range(0, drifted).Select(i => new DriftItem("t" + i, spec, "1", "0", "tweaks", "r", DateTime.UtcNow)).ToList()) : null,
            DiskWarnings = diskWarnings,
        };
    }

    [Theory]
    [InlineData(UserNotificationState.AcceptsNotifications, true)]
    [InlineData(UserNotificationState.NotPresent, false)]
    [InlineData(UserNotificationState.Busy, false)]
    [InlineData(UserNotificationState.RunningFullScreenGame, false)]
    [InlineData(UserNotificationState.PresentationMode, false)]
    [InlineData(UserNotificationState.QuietTime, false)]
    [InlineData(UserNotificationState.App, false)]
    [InlineData(UserNotificationState.Unknown, false)]
    public void Only_notifies_when_windows_accepts_notifications(UserNotificationState state, bool expected)
    {
        Assert.Equal(expected, QuietHours.ShouldNotify(state));
        var (toast, whyNot) = ScheduledScan.Notify(Found(7L << 30), new ScheduleSettings(), state, "r.md");
        Assert.Equal(expected, toast is not null);
        Assert.Equal(expected, whyNot is null);
        if (!expected) Assert.StartsWith("No notification", whyNot);
    }

    [Fact]
    public void Small_finds_stay_quiet_but_drift_and_disk_warnings_speak_up()
    {
        var settings = new ScheduleSettings();
        var accept = UserNotificationState.AcceptsNotifications;

        Assert.Null(ScheduledScan.Notify(Found(100L << 20), settings, accept, null).Notification);

        var drift = ScheduledScan.Notify(Found(0, drifted: 2), settings, accept, null).Notification!;
        Assert.Equal("2 of your tweaks were changed back", drift.Title);
        Assert.Equal("schedule", drift.NavigateTo);

        var disk = ScheduledScan.Notify(Found(0, 0, "SSD: Windows reports a health warning."), settings, accept, null).Notification!;
        Assert.Equal("A disk needs a look", disk.Title);

        var big = ScheduledScan.Notify(Found((long)(7.3 * (1L << 30))), settings, accept, "x.md").Notification!;
        Assert.Equal("PC Tidy found 7.3 GB you can clean", big.Title);
        Assert.Equal("x.md", big.ReportPath);

        settings.NotifyMinBytes = 50L << 20;
        Assert.NotNull(ScheduledScan.Notify(Found(100L << 20), settings, accept, null).Notification);
    }

    [Fact]
    public void Module_is_the_v3_schedule_section_without_scanners()
    {
        var m = new ScheduleModule();
        Assert.Equal("schedule", m.Id);
        Assert.Equal("Schedule & reports", m.Title);
        Assert.Equal(NavGroup.You, m.Group);
        Assert.Equal("CalendarClock24", m.Icon);
        Assert.Equal(Release.V3, m.Release);
        Assert.Empty(m.Scanners);
        Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Icon, out _));
    }

    [Fact]
    public void Graph_describes_its_numbers_for_screen_readers()
    {
        var info = MetricKeys.Describe("freeC")!;
        var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var text = HistoryGraph.Describe(info, new[]
        {
            new GraphPoint(t, 40L << 30, SampleKind.Before),
            new GraphPoint(t.AddMinutes(5), 46L << 30, SampleKind.After),
        });
        Assert.Contains("C: free space history: 2 samples", text);
        Assert.Contains("latest 46 GB, lowest 40 GB, highest 46 GB", text);
        Assert.Equal("History graph: no samples yet.", HistoryGraph.Describe(null, Array.Empty<GraphPoint>()));
    }

    [Fact]
    public void Page_builds_on_a_practice_pc()
    {
        using var box = new ScheduleSandbox("page", SandboxProfile.Minimal);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var services = new SpyServices(box.Host);
                var page = new ScheduleModule().CreatePage(services);
                Assert.IsType<SchedulePage>(page);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    [Fact]
    public void Report_markdown_escapes_formatting_characters()
        => Assert.Equal(@"\[\[Home\]\] \*x\* \<b\> a\|b \#1", ReportDocument.Md("[[Home]] *x* <b> a|b #1"));
}
