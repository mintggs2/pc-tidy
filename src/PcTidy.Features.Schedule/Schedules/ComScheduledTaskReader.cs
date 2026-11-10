using System.Globalization;
using System.Runtime.InteropServices;

namespace PcTidy.Features.Schedule.Schedules;

/// <summary>
/// Task Scheduler 2.0 through its COM API (<c>Schedule.Service</c>), read-only and only inside "\PC Tidy\":
/// the folder, its tasks, their triggers and actions are read; nothing is registered, run or changed.
/// Language-neutral (no schtasks text). A missing folder simply means "no schedules yet".
/// </summary>
public sealed class ComScheduledTaskReader : IScheduledTaskReader
{
    private const int TaskEnumHidden = 1;
    private const int TriggerWeekly = 3;
    private const int TriggerMonthly = 4;
    private const int ActionExec = 0;

    public bool IsAvailable => Type.GetTypeFromProgID("Schedule.Service") is not null;

    public IReadOnlyList<ScheduledTaskState> ReadOwnTasks(CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return Array.Empty<ScheduledTaskState>();
        var found = new List<ScheduledTaskState>();
        dynamic? service = null, folder = null, tasks = null;
        try
        {
            service = Activator.CreateInstance(type);
            if (service is null) return found;
            service.Connect();
            try { folder = service.GetFolder(ScheduleIds.TaskFolder); }
            catch (COMException) { return found; }        // folder not there: nothing scheduled yet
            catch (FileNotFoundException) { return found; }
            tasks = folder.GetTasks(TaskEnumHidden);
            foreach (dynamic task in tasks)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Read((object)task) is { } state) found.Add(state);
                }
                catch (COMException) { /* one unreadable task doesn't hide the other */ }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(task); }
            }
        }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            Release(tasks);
            Release(folder);
            Release(service);
        }
        return found;
    }

    private static ScheduledTaskState? Read(object com)
    {
        dynamic task = com;
        string name = task.Name;
        if (ScheduleIds.FromTaskName(name) is not { } kind) return null; // not one of ours: ignored
        string? day = null, time = null, arguments = null;
        dynamic definition = task.Definition;
        try
        {
            dynamic triggers = definition.Triggers;
            try
            {
                foreach (dynamic trigger in triggers)
                {
                    try
                    {
                        if (day is null) (day, time) = ReadTrigger((object)trigger, kind);
                    }
                    finally { Release(trigger); }
                }
            }
            finally { Release(triggers); }

            dynamic actions = definition.Actions;
            try
            {
                foreach (dynamic action in actions)
                {
                    try
                    {
                        if (arguments is null && (int)action.Type == ActionExec) arguments = (string?)action.Arguments ?? "";
                    }
                    finally { Release(action); }
                }
            }
            finally { Release(actions); }
        }
        finally { Release(definition); }

        int? lastResult = null;
        try { lastResult = (int)task.LastTaskResult; } catch (COMException) { }
        return new ScheduledTaskState(kind, (string)task.Path, (bool)task.Enabled, day, time, arguments,
            Time((DateTime)task.NextRunTime), Time((DateTime)task.LastRunTime), lastResult);
    }

    private static (string? Day, string? Time) ReadTrigger(object com, ScheduleKind kind)
    {
        dynamic trigger = com;
        int type = trigger.Type;
        string? time = StartTime((string?)trigger.StartBoundary);
        if (kind == ScheduleKind.WeeklyScan && type == TriggerWeekly)
        {
            int days = trigger.DaysOfWeek; // bit 0 = Sunday â€¦ bit 6 = Saturday
            for (int i = 0; i < 7; i++)
                if ((days & (1 << i)) != 0) return (((DayOfWeek)i).ToString(), time);
        }
        if (kind == ScheduleKind.MonthlyCheckup && type == TriggerMonthly)
        {
            int days = trigger.DaysOfMonth; // bit 0 = day 1
            for (int i = 0; i < 31; i++)
                if ((days & (1 << i)) != 0) return ((i + 1).ToString(CultureInfo.InvariantCulture), time);
        }
        return (null, time);
    }

    /// <summary>StartBoundary is ISO 8601 local time, e.g. "2026-11-15T19:00:00".</summary>
    internal static string? StartTime(string? boundary)
        => !string.IsNullOrWhiteSpace(boundary) && boundary.Length >= 16 && boundary[10] == 'T'
           && TimeOnly.TryParseExact(boundary.Substring(11, 5), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Task Scheduler reports "never" as a date near 1899.</summary>
    private static DateTime? Time(DateTime local) => local.Year < 2000 ? null : local.ToUniversalTime();

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
