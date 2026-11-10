using System.Globalization;

namespace PcTidy.Features.Schedule.Schedules;

/// <summary>
/// Builds the PlanItems that turn a schedule on or off. The schedule itself only ever runs PC Tidy's report-only scan:
/// the task name and the switches come from <see cref="ScheduleIds"/> constants, and the only user input (day and time)
/// is validated to a strict form first, so no other command line can be produced.
///
/// Args emitted (documented contract for the Platform handler):
///   ScheduledTaskCreate: "name" = "Weekly scan" | "Monthly checkup", "schedule" = weekly | monthly,
///                        "day" = Monday…Sunday (weekly) | 1…28 (monthly), "time" = HH:mm,
///                        "arguments" = "--scheduled-scan" | "--scheduled-scan --checkup".
///   ScheduledTaskDelete: "name".
/// TaskPath is set too ("\PC Tidy\&lt;name&gt;") for History and the confirm dialog.
/// </summary>
public static class SchedulePlanner
{
    /// <summary>Latest day of the month offered: every month has it, so the checkup never skips February.</summary>
    public const int MaxMonthDay = 28;

    public static PlanItem Create(ScheduleKind kind, string day, string time)
    {
        var normalizedDay = NormalizeDay(kind, day) ?? throw new ArgumentException($"'{day}' isn't a valid day for the {ScheduleIds.Title(kind).ToLowerInvariant()}.", nameof(day));
        var normalizedTime = NormalizeTime(time) ?? throw new ArgumentException($"'{time}' isn't a valid time (use HH:mm).", nameof(time));
        return new PlanItem
        {
            Kind = ActionKind.ScheduledTaskCreate,
            FeatureId = ScheduleIds.FeatureId,
            Description = Describe(kind, normalizedDay, normalizedTime),
            TaskPath = ScheduleIds.TaskPath(kind),
            Reversible = true,
            Args = new Dictionary<string, string>
            {
                ["name"] = ScheduleIds.TaskName(kind),
                ["schedule"] = ScheduleIds.Cadence(kind),
                ["day"] = normalizedDay,
                ["time"] = normalizedTime,
                ["arguments"] = ScheduleIds.Arguments(kind),
            },
        };
    }

    public static PlanItem Delete(ScheduleKind kind) => Delete(kind, ScheduleIds.TaskName(kind));

    /// <summary>Deletes the task with this exact name (this account's, or an untagged one from an older version).</summary>
    public static PlanItem Delete(ScheduleKind kind, string taskName) => new()
    {
        Kind = ActionKind.ScheduledTaskDelete,
        FeatureId = ScheduleIds.FeatureId,
        Description = $"Turn off the {ScheduleIds.Title(kind).ToLowerInvariant()} (removes PC Tidy's own task \"{taskName}\").",
        TaskPath = ScheduleIds.TaskFolder + "\\" + taskName,
        Reversible = true,
        Args = new Dictionary<string, string> { ["name"] = taskName },
    };

    private static string NameOf(ScheduledTaskState t) => t.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    /// <summary>
    /// The items needed to make Windows match what the user picked: create (or replace) when wanted and different,
    /// delete when unwanted but present. Nothing when they already match.
    /// </summary>
    public static IReadOnlyList<PlanItem> Reconcile(ScheduleSettings wanted, IReadOnlyList<ScheduledTaskState> current)
    {
        var items = new List<PlanItem>();
        foreach (var kind in new[] { ScheduleKind.WeeklyScan, ScheduleKind.MonthlyCheckup })
        {
            var choice = wanted.For(kind);
            var mine = ScheduleIds.TaskName(kind);
            var existing = current.FirstOrDefault(t => t.Kind == kind && NameOf(t) == mine);
            // Untagged tasks from an older version: replaced by this account's own task, or removed when unwanted.
            var legacy = current.Where(t => t.Kind == kind && NameOf(t) != mine).ToList();
            if (choice.Enabled)
            {
                var day = NormalizeDay(kind, choice.Day);
                var time = NormalizeTime(choice.Time);
                if (day is null || time is null) continue; // the page never lets an invalid choice through
                if (existing is null || !existing.Matches(day, time)) items.Add(Create(kind, day, time));
                items.AddRange(legacy.Select(l => Delete(kind, NameOf(l))));
            }
            else
            {
                if (existing is not null) items.Add(Delete(kind));
                items.AddRange(legacy.Select(l => Delete(kind, NameOf(l))));
            }
        }
        return items;
    }

    public static ActionPlan Plan(IReadOnlyList<PlanItem> items)
        => new() { FeatureId = ScheduleIds.FeatureId, Title = "Schedule", Items = items };

    /// <summary>Weekly: an English day name (invariant data, any display language). Monthly: 1…28.</summary>
    public static string? NormalizeDay(ScheduleKind kind, string? day)
    {
        if (string.IsNullOrWhiteSpace(day)) return null;
        day = day.Trim();
        if (kind == ScheduleKind.WeeklyScan)
            return Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var d) && Enum.IsDefined(d) && !int.TryParse(day, out _) ? d.ToString() : null;
        return int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxMonthDay
            ? n.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>"7:5" → null; "07:05" / "7:05" → "07:05".</summary>
    public static string? NormalizeTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        return TimeOnly.TryParseExact(time.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;
    }

    public static string Describe(ScheduleKind kind, string day, string time)
    {
        var when = kind == ScheduleKind.WeeklyScan
            ? $"every {LocalDayName(day)} at {LocalTime(time)}"
            : $"on day {day} of every month at {LocalTime(time)}";
        var what = kind == ScheduleKind.WeeklyScan ? "a weekly scan" : "a monthly checkup";
        return $"Run {what} {when}. It only looks and writes a report: nothing is changed.";
    }

    /// <summary>The day name in the user's language for display ("Sunday" → "dimanche").</summary>
    public static string LocalDayName(string invariantDay)
        => Enum.TryParse<DayOfWeek>(invariantDay, true, out var d) ? CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d) : invariantDay;

    public static string LocalTime(string hhmm)
        => TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("t", CultureInfo.CurrentCulture)
            : hhmm;
}
