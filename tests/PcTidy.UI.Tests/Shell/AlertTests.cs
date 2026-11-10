using PcTidy.App.Notifications;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>Tray alerts: at most one of each kind a day, quiet hours (also overnight), Windows' busy state, the user's switch.</summary>
public class AlertTests
{
    private static readonly QuietHoursWindow NoQuiet = new(false, new TimeOnly(22, 0), new TimeOnly(8, 0));

    [Fact]
    public void An_alert_is_shown_at_most_once_in_24_hours()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 10, 12, 0, 0));
        var policy = new AlertPolicy(clock, new FakeBusy(), new InMemoryAlertMemory());

        Assert.Equal(AlertVerdict.Show, policy.TryClaim(AlertKind.LowDisk, true, NoQuiet));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AlertVerdict.AlreadyToday, policy.TryClaim(AlertKind.LowDisk, true, NoQuiet));
        clock.Advance(TimeSpan.FromHours(12)); // past midnight, but not a day later
        Assert.Equal(AlertVerdict.AlreadyToday, policy.TryClaim(AlertKind.LowDisk, true, NoQuiet));
        clock.Advance(TimeSpan.FromHours(11));
        Assert.Equal(AlertVerdict.Show, policy.TryClaim(AlertKind.LowDisk, true, NoQuiet));
    }

    [Fact]
    public void Each_kind_has_its_own_daily_limit()
    {
        var policy = new AlertPolicy(new FakeClock(new DateTime(2026, 11, 10, 12, 0, 0)), new FakeBusy(), new InMemoryAlertMemory());
        Assert.Equal(AlertVerdict.Show, policy.TryClaim(AlertKind.LowDisk, true, NoQuiet));
        Assert.Equal(AlertVerdict.Show, policy.TryClaim(AlertKind.ScanReport, true, NoQuiet));
    }

    [Fact]
    public void A_refused_alert_does_not_use_up_the_day()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 10, 23, 0, 0));
        var busy = new FakeBusy();
        var policy = new AlertPolicy(clock, busy, new InMemoryAlertMemory());
        Assert.Equal(AlertVerdict.QuietHours, policy.TryClaim(AlertKind.LowDisk, true, QuietHoursWindow.Default));
        clock.UtcNow = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        busy.Accepts = false; // a full-screen game
        Assert.Equal(AlertVerdict.Busy, policy.TryClaim(AlertKind.LowDisk, true, QuietHoursWindow.Default));
        busy.Accepts = true;
        Assert.Equal(AlertVerdict.Show, policy.TryClaim(AlertKind.LowDisk, true, QuietHoursWindow.Default));
    }

    [Fact]
    public void Switched_off_means_never()
    {
        var policy = new AlertPolicy(new FakeClock(new DateTime(2026, 11, 10, 12, 0, 0)), new FakeBusy(), new InMemoryAlertMemory());
        Assert.Equal(AlertVerdict.Off, policy.TryClaim(AlertKind.LowDisk, false, NoQuiet));
    }

    [Theory]
    [InlineData("22:00", "08:00", "23:30", true)]
    [InlineData("22:00", "08:00", "02:00", true)]
    [InlineData("22:00", "08:00", "07:59", true)]
    [InlineData("22:00", "08:00", "08:00", false)]
    [InlineData("22:00", "08:00", "12:00", false)]
    [InlineData("12:00", "14:00", "13:00", true)]
    [InlineData("12:00", "14:00", "14:30", false)]
    [InlineData("09:00", "09:00", "09:00", false)] // from == to: no quiet hours
    public void Quiet_hours_wrap_past_midnight(string from, string to, string now, bool quiet)
        => Assert.Equal(quiet, QuietHoursWindow.Parse(true, from, to).Contains(TimeOnly.Parse(now)));

    [Fact]
    public void Quiet_hours_switched_off_never_hold_anything_back()
        => Assert.False(QuietHoursWindow.Parse(false, "00:00", "23:59").Contains(new TimeOnly(12, 0)));

    [Fact]
    public void Alert_times_survive_a_restart()
    {
        using var folder = new ShellTestFolder("alerts");
        var clock = new FakeClock(new DateTime(2026, 11, 10, 12, 0, 0));
        Assert.Equal(AlertVerdict.Show, new AlertPolicy(clock, new FakeBusy(), new AlertMemoryStore(folder.Root)).TryClaim(AlertKind.LowDisk, true, NoQuiet));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(AlertVerdict.AlreadyToday, new AlertPolicy(clock, new FakeBusy(), new AlertMemoryStore(folder.Root)).TryClaim(AlertKind.LowDisk, true, NoQuiet));
        Assert.True(File.Exists(Path.Combine(folder.Root, "shell", "alerts.json")));
    }

    [Fact]
    public void Low_disk_means_the_Windows_drive_under_ten_percent_and_twenty_GB_with_calm_words()
    {
        var drives = new[]
        {
            new DriveSpace(@"C:\", 250L << 30, 12L << 30),   // 4.8 % and 12 GB: low
            new DriveSpace(@"D:\", 1000L << 30, 60L << 30),  // 6 % but 60 GB: a big drive, not low
            new DriveSpace(@"E:\", 8000L << 30, 10L << 30),  // a full backup disk: not the Windows drive
            new DriveSpace(@"F:\", 0, 0),
        };
        var low = LowDiskCheck.LowDrives(drives, @"C:\");
        Assert.Equal(@"C:\", Assert.Single(low).Root);
        var n = LowDiskCheck.Notification(low)!;
        Assert.Equal(AlertKind.LowDisk, n.Kind);
        Assert.Contains("Nothing has been changed", n.Body);
        Assert.DoesNotContain("!", n.Title + n.Body);
        Assert.Empty(LowDiskCheck.LowDrives(drives, @"D:\"));
        Assert.Empty(LowDiskCheck.LowDrives(new[] { new DriveSpace(@"C:\", 100L << 30, 20L << 30) }, @"C:\"));
    }

    [Fact]
    public void Practice_mode_logs_instead_of_showing_a_notification()
    {
        using var folder = new ShellTestFolder("lognotify");
        var log = new LogNotifier(folder.Root);
        log.Show(new AppNotification(AlertKind.LowDisk, "C: is getting full", "body", "Have a look", "optimize"));
        Assert.Single(log.Recent);
        Assert.Contains("Would show: C: is getting full", File.ReadAllText(Path.Combine(folder.Root, "notifications.log")));
    }
}
