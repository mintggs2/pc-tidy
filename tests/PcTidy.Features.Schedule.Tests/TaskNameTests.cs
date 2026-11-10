using PcTidy.Features.Schedule.Schedules;
using Xunit;

namespace PcTidy.Features.Schedule.Tests;

/// <summary>Per-account task names (two accounts on one PC never share a task).</summary>
public class TaskNameTests
{
    [Fact]
    public void Each_account_gets_its_own_task_name_and_ignores_the_others()
    {
        var mine = ScheduleIds.UserTagFor("S-1-5-21-1-2-3-1001");
        var theirs = ScheduleIds.UserTagFor("S-1-5-21-1-2-3-1002");
        Assert.NotEqual(mine, theirs);
        Assert.Matches("^[0-9a-f]{8}$", mine);

        var myWeekly = ScheduleIds.TaskName(ScheduleKind.WeeklyScan, mine);
        Assert.Equal("Weekly scan - " + mine, myWeekly);
        Assert.Equal(ScheduleKind.WeeklyScan, ScheduleIds.FromTaskName(myWeekly, mine));
        Assert.Null(ScheduleIds.FromTaskName(ScheduleIds.TaskName(ScheduleKind.WeeklyScan, theirs), mine));
        // Older, untagged tasks are still recognised (so they can be shown and removed).
        Assert.Equal(ScheduleKind.MonthlyCheckup, ScheduleIds.FromTaskName("Monthly checkup", mine));
    }

    [Fact]
    public void An_untagged_task_from_an_older_version_is_replaced_by_this_accounts_own()
    {
        var wanted = new ScheduleSettings
        {
            Weekly = new ScheduleChoice { Enabled = true, Day = "Friday", Time = "18:00" },
            Monthly = new ScheduleChoice { Enabled = false },
        };
        var old = new ScheduledTaskState(ScheduleKind.WeeklyScan, @"\PC Tidy\Weekly scan", true, "Friday", "18:00", "--scheduled-scan", null, null, 0);
        var items = SchedulePlanner.Reconcile(wanted, new[] { old });
        if (ScheduleIds.UserTag.Length == 0) { Assert.Empty(items); return; } // no SID: the untagged name is this account's
        Assert.Contains(items, i => i.Kind == PcTidy.Core.Model.ActionKind.ScheduledTaskCreate && i.Args!["name"] == ScheduleIds.TaskName(ScheduleKind.WeeklyScan));
        Assert.Contains(items, i => i.Kind == PcTidy.Core.Model.ActionKind.ScheduledTaskDelete && i.Args!["name"] == "Weekly scan");
    }

    [Fact]
    public void The_tag_holds_no_user_name_and_fits_the_guard_name_rule()
    {
        var name = ScheduleIds.TaskName(ScheduleKind.MonthlyCheckup);
        Assert.Matches(@"^[A-Za-z0-9 ._\-]{1,64}$", name);
        Assert.DoesNotContain(Environment.UserName, name, StringComparison.OrdinalIgnoreCase);
    }
}
