using PcTidy.Features.Schedule.Schedules;

namespace PcTidy.Features.Schedule.Tests;

public class SchedulePlannerTests
{
    private static readonly string[] CreateKeys = { "arguments", "day", "name", "schedule", "time" };

    [Fact]
    public void Weekly_scan_creates_exactly_the_documented_task_with_report_only_arguments()
    {
        var item = SchedulePlanner.Create(ScheduleKind.WeeklyScan, "sunday", "7:30");

        Assert.Equal(ActionKind.ScheduledTaskCreate, item.Kind);
        Assert.Equal("schedule", item.FeatureId);
        Assert.Equal(ScheduleIds.TaskFolder + "\\" + ScheduleIds.TaskName(ScheduleKind.WeeklyScan), item.TaskPath);
        Assert.Equal(CreateKeys, item.Args!.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(ScheduleIds.TaskName(ScheduleKind.WeeklyScan), item.Args["name"]);
        Assert.Equal("weekly", item.Args["schedule"]);
        Assert.Equal("Sunday", item.Args["day"]);
        Assert.Equal("07:30", item.Args["time"]);
        Assert.Equal("--scheduled-scan", item.Args["arguments"]);
        Assert.True(item.Reversible);
        Assert.False(item.NeedsAdmin);
        Assert.Null(item.SourcePath);
        Assert.Null(item.DestinationPath);
        Assert.Null(item.Registry);
        Assert.Contains("nothing is changed", item.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Monthly_checkup_creates_the_checkup_task()
    {
        var item = SchedulePlanner.Create(ScheduleKind.MonthlyCheckup, " 28 ", "23:00");

        Assert.Equal(ScheduleIds.TaskFolder + "\\" + ScheduleIds.TaskName(ScheduleKind.MonthlyCheckup), item.TaskPath);
        Assert.Equal(ScheduleIds.TaskName(ScheduleKind.MonthlyCheckup), item.Args!["name"]);
        Assert.Equal("monthly", item.Args["schedule"]);
        Assert.Equal("28", item.Args["day"]);
        Assert.Equal("23:00", item.Args["time"]);
        Assert.Equal("--scheduled-scan --checkup", item.Args["arguments"]);
    }

    [Fact]
    public void Delete_only_names_the_task()
    {
        foreach (var kind in new[] { ScheduleKind.WeeklyScan, ScheduleKind.MonthlyCheckup })
        {
            var item = SchedulePlanner.Delete(kind);
            Assert.Equal(ActionKind.ScheduledTaskDelete, item.Kind);
            Assert.Equal(new[] { "name" }, item.Args!.Keys);
            Assert.Equal(ScheduleIds.TaskName(kind), item.Args["name"]);
            Assert.StartsWith(@"\PC Tidy\", item.TaskPath);
        }
    }

    [Fact]
    public void No_input_can_produce_any_other_arguments()
    {
        // Every valid choice yields one of the two constant switch strings; nothing user-typed reaches the command line.
        var allowed = new HashSet<string> { "--scheduled-scan", "--scheduled-scan --checkup" };
        foreach (var day in Enum.GetNames<DayOfWeek>())
            foreach (var time in new[] { "00:00", "12:30", "23:59" })
                Assert.Contains(SchedulePlanner.Create(ScheduleKind.WeeklyScan, day, time).Args!["arguments"], allowed);
        for (int d = 1; d <= 28; d++)
            Assert.Contains(SchedulePlanner.Create(ScheduleKind.MonthlyCheckup, d.ToString(), "08:00").Args!["arguments"], allowed);
    }

    [Theory]
    [InlineData(ScheduleKind.WeeklyScan, "Sunday & calc.exe", "10:00")]
    [InlineData(ScheduleKind.WeeklyScan, "3", "10:00")]
    [InlineData(ScheduleKind.WeeklyScan, "Funday", "10:00")]
    [InlineData(ScheduleKind.MonthlyCheckup, "29", "10:00")]
    [InlineData(ScheduleKind.MonthlyCheckup, "0", "10:00")]
    [InlineData(ScheduleKind.MonthlyCheckup, "1 --other", "10:00")]
    [InlineData(ScheduleKind.MonthlyCheckup, "Monday", "10:00")]
    [InlineData(ScheduleKind.WeeklyScan, "Monday", "25:00")]
    [InlineData(ScheduleKind.WeeklyScan, "Monday", "10:00 --x")]
    [InlineData(ScheduleKind.WeeklyScan, "Monday", "")]
    public void Invalid_day_or_time_is_refused(ScheduleKind kind, string day, string time)
        => Assert.Throws<ArgumentException>(() => SchedulePlanner.Create(kind, day, time));

    [Fact]
    public void Reconcile_creates_missing_replaces_different_deletes_unwanted_and_leaves_matching_alone()
    {
        var wanted = new ScheduleSettings
        {
            Weekly = new ScheduleChoice { Enabled = true, Day = "Friday", Time = "18:00" },
            Monthly = new ScheduleChoice { Enabled = false },
        };

        // Nothing in Windows yet: one create.
        var items = SchedulePlanner.Reconcile(wanted, Array.Empty<ScheduledTaskState>());
        Assert.Equal(ActionKind.ScheduledTaskCreate, Assert.Single(items).Kind);

        // Same task already there: nothing to do.
        var same = new ScheduledTaskState(ScheduleKind.WeeklyScan, ScheduleIds.TaskPath(ScheduleKind.WeeklyScan), true, "Friday", "18:00", "--scheduled-scan", null, null, 0);
        Assert.Empty(SchedulePlanner.Reconcile(wanted, new[] { same }));

        // Different time, or someone changed its arguments: replaced.
        Assert.Single(SchedulePlanner.Reconcile(wanted, new[] { same with { Time = "19:00" } }));
        Assert.Single(SchedulePlanner.Reconcile(wanted, new[] { same with { Arguments = "--something-else" } }));
        Assert.Single(SchedulePlanner.Reconcile(wanted, new[] { same with { Enabled = false } }));

        // Monthly is off but present in Windows: deleted.
        var monthly = new ScheduledTaskState(ScheduleKind.MonthlyCheckup, ScheduleIds.TaskPath(ScheduleKind.MonthlyCheckup), true, "1", "19:00", "--scheduled-scan --checkup", null, null, 0);
        var both = SchedulePlanner.Reconcile(wanted, new[] { same, monthly });
        var delete = Assert.Single(both);
        Assert.Equal(ActionKind.ScheduledTaskDelete, delete.Kind);
        Assert.Equal(ScheduleIds.TaskName(ScheduleKind.MonthlyCheckup), delete.Args!["name"]);

        var plan = SchedulePlanner.Plan(both);
        Assert.Equal("schedule", plan.FeatureId);
        Assert.False(plan.RequiresAdmin);
        Assert.False(plan.RequiresRestorePoint);
    }

    [Theory]
    [InlineData("2026-11-15T19:00:00", "19:00")]
    [InlineData("2026-11-15T07:05:00+01:00", "07:05")]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Task_start_boundary_is_read_as_HHmm(string? boundary, string? expected)
        => Assert.Equal(expected, ComScheduledTaskReader.StartTime(boundary));

    [Fact]
    public void Settings_round_trip_through_AppSettings_and_default_everything_external_off()
    {
        var app = new AppSettings();
        var fresh = ScheduleSettings.Load(app);
        Assert.False(fresh.Weekly.Enabled);
        Assert.False(fresh.Monthly.Enabled);
        Assert.False(fresh.CopyReportsToFolder);
        Assert.Null(fresh.ReportCopyFolder);

        fresh.Weekly.Enabled = true;
        fresh.Weekly.Day = "Tuesday";
        fresh.Store(app);
        var again = ScheduleSettings.Load(app);
        Assert.True(again.Weekly.Enabled);
        Assert.Equal("Tuesday", again.Weekly.Day);
    }
}
