using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>PC Tidy's own tasks in the "\PC Tidy\" Task Scheduler folder. Real: Task Scheduler COM. Tests: a fake.</summary>
public interface IOwnTaskControl
{
    bool TouchesRealSystem { get; }
    /// <summary>The task's definition XML, or null if there's no such task.</summary>
    string? GetXml(string name);
    /// <summary>Creates or replaces the task for the current user (interactive token, no stored password, not elevated).</summary>
    void Register(string name, string xml);
    void Delete(string name);
}

public sealed class ComOwnTaskControl : IOwnTaskControl
{
    public const string FolderName = "PC Tidy";
    private const int CreateOrUpdate = 6;
    private const int LogonInteractiveToken = 3;
    private const int NotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    public bool TouchesRealSystem => true;

    public string? GetXml(string name) => WithFolder(create: false, folder =>
    {
        if (folder is null) return null;
        try
        {
            dynamic task = folder.GetTask(name);
            try { return (string)task.Xml; } finally { Marshal.FinalReleaseComObject(task); }
        }
        catch (COMException ex) when (ex.HResult is NotFound or PathNotFound) { return null; }
    });

    public void Register(string name, string xml) => WithFolder(create: true, folder =>
    {
        dynamic task = folder!.RegisterTask(name, xml, CreateOrUpdate, null, null, LogonInteractiveToken, null);
        Marshal.FinalReleaseComObject(task);
        return 0;
    });

    public void Delete(string name) => WithFolder(create: false, folder =>
    {
        folder?.DeleteTask(name, 0);
        return 0;
    });

    private static T WithFolder<T>(bool create, Func<dynamic?, T> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic? folder;
            try { folder = service.GetFolder("\\" + FolderName); }
            catch (COMException ex) when (ex.HResult is NotFound or PathNotFound)
            {
                if (!create) return action(null);
                dynamic root = service.GetFolder("\\");
                try { folder = root.CreateFolder(FolderName, null); } finally { Marshal.FinalReleaseComObject(root); }
            }
            try { return action(folder); } finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}

/// <summary>Builds and checks the definitions of PC Tidy's own tasks.</summary>
public static partial class OwnTaskDefinition
{
    /// <summary>The only switches a scheduled PC Tidy may get: report-only runs.</summary>
    public static readonly IReadOnlyList<string> AllowedSwitches = new[] { "--scheduled-scan", "--checkup" };
    public static readonly IReadOnlyList<string> Schedules = new[] { "daily", "weekly", "monthly", "logon" };

    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [GeneratedRegex(@"^[A-Za-z0-9 _\-().]{1,100}$", RegexOptions.CultureInvariant)] public static partial Regex NamePattern();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.CultureInvariant)] private static partial Regex TimePattern();

    /// <summary>"--scheduled-scan --checkup" → validated, de-duplicated switches; null if anything else is in there.</summary>
    public static string? CleanArguments(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => !AllowedSwitches.Contains(p, StringComparer.Ordinal))) return null;
        return string.Join(' ', parts.Distinct(StringComparer.Ordinal));
    }

    /// <summary>Null when the schedule arguments are valid.</summary>
    public static string? ScheduleProblem(string schedule, string day, string time)
    {
        if (!Schedules.Contains(schedule)) return "The schedule must be daily, weekly, monthly or at sign-in.";
        if (schedule != "logon" && !TimePattern().IsMatch(time)) return "The time must look like 09:30.";
        if (schedule == "weekly" && !Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out _)) return "The day of the week is not valid.";
        if (schedule == "monthly" && !(int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 28)) return "The day of the month must be 1 to 28.";
        return null;
    }

    /// <summary>Task Scheduler 1.2 XML: runs <paramref name="appPath"/> with the given switches, as the current user, never elevated.</summary>
    public static string Build(string appPath, string arguments, string schedule, string day, string time, string userSid, DateTime nowLocal)
    {
        XNamespace ns = Ns;
        var start = schedule == "logon" ? nowLocal : nowLocal.Date.Add(TimeSpan.ParseExact(time, "hh\\:mm", CultureInfo.InvariantCulture));
        var boundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        XElement trigger = schedule switch
        {
            "logon" => new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", userSid), new XElement(ns + "Delay", "PT5M")),
            "daily" => new XElement(ns + "CalendarTrigger", new XElement(ns + "StartBoundary", boundary),
                new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", 1))),
            "weekly" => new XElement(ns + "CalendarTrigger", new XElement(ns + "StartBoundary", boundary),
                new XElement(ns + "ScheduleByWeek", new XElement(ns + "WeeksInterval", 1),
                    new XElement(ns + "DaysOfWeek", new XElement(ns + Enum.Parse<DayOfWeek>(day, true).ToString())))),
            _ => new XElement(ns + "CalendarTrigger", new XElement(ns + "StartBoundary", boundary),
                new XElement(ns + "ScheduleByMonth",
                    new XElement(ns + "DaysOfMonth", new XElement(ns + "Day", int.Parse(day, CultureInfo.InvariantCulture))),
                    new XElement(ns + "Months", CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).Select(m => new XElement(ns + m))))),
        };
        var doc = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Author", "PC Tidy"), new XElement(ns + "Description", "PC Tidy: report only. It never changes anything on its own.")),
            new XElement(ns + "Triggers", trigger),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "true"),
                new XElement(ns + "StopIfGoingOnBatteries", "true"),
                new XElement(ns + "StartWhenAvailable", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT2H"),
                new XElement(ns + "Enabled", "true")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec", new XElement(ns + "Command", appPath), new XElement(ns + "Arguments", arguments))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + doc;
    }

    /// <summary>
    /// True only for a definition whose one action is PC Tidy itself with allowed switches and that never runs elevated.
    /// Used before putting a saved definition back, so undo can't be used to schedule anything else.
    /// </summary>
    public static bool IsOwnDefinition(string xml, string appPath)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = Ns;
            var actions = doc.Root?.Element(ns + "Actions")?.Elements().ToList() ?? new List<XElement>();
            if (actions.Count != 1 || actions[0].Name != ns + "Exec") return false;
            var command = actions[0].Element(ns + "Command")?.Value.Trim().Trim('"');
            var args = actions[0].Element(ns + "Arguments")?.Value ?? "";
            if (!string.Equals(command, appPath, StringComparison.OrdinalIgnoreCase) || CleanArguments(args) is null) return false;
            var runLevel = doc.Root!.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "RunLevel")?.Value;
            return runLevel is null or "LeastPrivilege";
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException) { return false; }
    }
}

/// <summary>
/// ScheduledTaskCreate. Args: "name", "schedule" = daily|weekly|monthly|logon, "day" (weekday name, or 1-28),
/// "time" (HH:mm), "arguments" (only --scheduled-scan and/or --checkup). Creates or replaces one of PC Tidy's own tasks
/// in "\PC Tidy\" for the current user; the action is always this PcTidy.exe, never elevated. The previous definition
/// (if any) is saved, so undo puts it back, or deletes the new task.
/// </summary>
public sealed class ScheduledTaskCreateHandler(IOwnTaskControl tasks, string? appPath) : SystemActionHandler
{
    internal const string PriorXmlData = "priorTaskXml";

    public ScheduledTaskCreateHandler() : this(new ComOwnTaskControl(), Environment.ProcessPath) { }

    protected override ActionKind Kind => ActionKind.ScheduledTaskCreate;
    protected override bool TouchesRealSystem => tasks.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static string Name(PlanItem item) => item.Arg("name").Trim();

    protected override string? Validate(PlanItem item)
    {
        if (!OwnTaskDefinition.NamePattern().IsMatch(Name(item))) return "The task name is not valid.";
        if (OwnTaskDefinition.CleanArguments(item.Arg("arguments").Trim()) is null) return "A scheduled PC Tidy may only make reports (--scheduled-scan, --checkup).";
        if (OwnTaskDefinition.ScheduleProblem(item.Arg("schedule").Trim(), item.Arg("day").Trim(), item.Arg("time").Trim()) is { } p) return p;
        if (!PathArgs.IsPlainLocal(appPath) || !Path.GetFileName(appPath!).Equals("PcTidy.exe", StringComparison.OrdinalIgnoreCase))
            return "Only an installed PC Tidy can schedule itself.";
        return null;
    }

    protected override string Describe(PlanItem item)
        => $"Would create the task \\{ComOwnTaskControl.FolderName}\\{Name(item)} ({item.Arg("schedule").Trim()}) running {appPath} {OwnTaskDefinition.CleanArguments(item.Arg("arguments").Trim())}";

    protected override bool CanUndo(StepRecord step) => true;
    protected override string DescribeUndo(StepRecord step) => $"Would remove the task \\{ComOwnTaskControl.FolderName}\\{Name(step.Item)} or put its old definition back";

    internal string BuildXml(PlanItem item, DateTime nowLocal)
    {
        string sid;
        using (var id = WindowsIdentity.GetCurrent()) sid = id.User?.Value ?? throw new SecurityException("Your account couldn't be identified.");
        return OwnTaskDefinition.Build(appPath!, OwnTaskDefinition.CleanArguments(item.Arg("arguments").Trim())!, item.Arg("schedule").Trim(),
            item.Arg("day").Trim(), item.Arg("time").Trim(), sid, nowLocal);
    }

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var name = Name(step.Item);
        var prior = tasks.GetXml(name);
        tasks.Register(name, BuildXml(step.Item, DateTime.Now));
        if (tasks.GetXml(name) is null) return Task.FromResult(step.Failed("Windows didn't create the task."));
        var done = step.Done(prior is null ? $"Scheduled '{name}'. It only makes a report; it never changes anything." : $"Updated the schedule '{name}'.");
        return Task.FromResult(prior is null ? done : done.WithUndo((PriorXmlData, prior)));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var name = Name(step.Item);
        if (step.Undo(PriorXmlData) is { } prior)
        {
            if (!OwnTaskDefinition.IsOwnDefinition(prior, appPath!)) return Task.FromResult(step.UndoFailed("The saved task isn't one of PC Tidy's own report tasks, so it wasn't put back."));
            tasks.Register(name, prior);
            return Task.FromResult(step.Undone($"Put the schedule '{name}' back as it was."));
        }
        tasks.Delete(name);
        return Task.FromResult(tasks.GetXml(name) is null ? step.Undone($"Removed the schedule '{name}'.") : step.UndoFailed("Windows didn't remove the task."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(tasks.GetXml(Name(step.Item)) is { } xml && OwnTaskDefinition.IsOwnDefinition(xml, appPath!));
}

/// <summary>
/// ScheduledTaskDelete. Args: "name". Deletes one of PC Tidy's own tasks in "\PC Tidy\" after saving its definition;
/// undo re-creates it from that definition, but only if it is still a PC Tidy report task.
/// </summary>
public sealed class ScheduledTaskDeleteHandler(IOwnTaskControl tasks, string? appPath) : SystemActionHandler
{
    internal const string SavedXmlData = "savedTaskXml";

    public ScheduledTaskDeleteHandler() : this(new ComOwnTaskControl(), Environment.ProcessPath) { }

    protected override ActionKind Kind => ActionKind.ScheduledTaskDelete;
    protected override bool TouchesRealSystem => tasks.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static string Name(PlanItem item) => item.Arg("name").Trim();

    protected override string? Validate(PlanItem item) => OwnTaskDefinition.NamePattern().IsMatch(Name(item)) ? null : "The task name is not valid.";

    protected override string Describe(PlanItem item) => $"Would save and then remove the task \\{ComOwnTaskControl.FolderName}\\{Name(item)}";

    protected override bool CanUndo(StepRecord step) => step.Undo(SavedXmlData) is not null;
    protected override string DescribeUndo(StepRecord step) => $"Would re-create the task \\{ComOwnTaskControl.FolderName}\\{Name(step.Item)} from its saved definition";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var name = Name(step.Item);
        if (tasks.GetXml(name) is not { } saved) return Task.FromResult(step.Skipped("This schedule isn't there. Nothing to remove."));
        tasks.Delete(name);
        return Task.FromResult(tasks.GetXml(name) is null
            ? step.Done($"Removed the schedule '{name}'.").WithUndo((SavedXmlData, saved))
            : step.Failed("Windows didn't remove the task."));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var saved = step.Undo(SavedXmlData)!;
        if (appPath is null || !OwnTaskDefinition.IsOwnDefinition(saved, appPath))
            return Task.FromResult(step.UndoFailed("The saved task isn't one of PC Tidy's own report tasks, so it wasn't re-created."));
        tasks.Register(Name(step.Item), saved);
        return Task.FromResult(tasks.GetXml(Name(step.Item)) is not null ? step.Undone($"Re-created the schedule '{Name(step.Item)}'.") : step.UndoFailed("Windows didn't re-create the task."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(tasks.GetXml(Name(step.Item)) is null);
}
