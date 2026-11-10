using System.Runtime.InteropServices;

namespace PcTidy.Features.Speed.Sources;

/// <summary>
/// Task Scheduler 2.0 through its COM API (<c>Schedule.Service</c>), read-only: folders, tasks, triggers and actions
/// are only read, nothing is registered, run or changed. Language-neutral (no schtasks text).
/// </summary>
public sealed class ComTaskSource : ITaskSource
{
    private const int TaskEnumHidden = 1;
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int RunLevelHighest = 1;

    public IReadOnlyList<TaskInfo> StartupTasks(CancellationToken ct)
    {
        var found = new List<TaskInfo>();
        WithService(service =>
        {
            dynamic root = service.GetFolder("\\");
            try { Walk(root, found, ct, depth: 0); }
            finally { Release(root); }
            return true;
        }, false);
        return found;
    }

    public TaskState? GetState(string taskPath)
        => WithService<TaskState?>(service =>
        {
            var cut = taskPath.LastIndexOf('\\');
            dynamic folder = service.GetFolder(cut <= 0 ? "\\" : taskPath[..cut]);
            dynamic? task = null;
            try
            {
                task = folder.GetTask(taskPath[(cut + 1)..]);
                return new TaskState((bool)task.Enabled, Time((DateTime)task.LastRunTime), Time((DateTime)task.NextRunTime));
            }
            finally
            {
                Release(task);
                Release(folder);
            }
        }, null);

    private static void Walk(dynamic folder, List<TaskInfo> found, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        string path = folder.Path;
        // Windows' own tasks are never offered (the SafetyGuard refuses them too).
        if (path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return;
        if (depth > 16) return;

        dynamic tasks = folder.GetTasks(TaskEnumHidden);
        try
        {
            foreach (dynamic task in tasks)
            {
                try
                {
                    if (Read(task) is { } info) found.Add(info);
                }
                catch (COMException) { /* a task we can't read is skipped */ }
                catch (UnauthorizedAccessException) { }
                finally { Release(task); }
            }
        }
        finally { Release(tasks); }

        dynamic subfolders = folder.GetFolders(0);
        try
        {
            foreach (dynamic sub in subfolders)
            {
                try { Walk(sub, found, ct, depth + 1); }
                catch (COMException) { }
                catch (UnauthorizedAccessException) { }
                finally { Release(sub); }
            }
        }
        finally { Release(subfolders); }
    }

    private static TaskInfo? Read(dynamic task)
    {
        dynamic definition = task.Definition;
        try
        {
            bool atLogon = false, atBoot = false;
            dynamic triggers = definition.Triggers;
            try
            {
                foreach (dynamic trigger in triggers)
                {
                    try
                    {
                        int type = trigger.Type;
                        bool enabled = trigger.Enabled;
                        if (!enabled) continue;
                        if (type == TriggerLogon) atLogon = true;
                        if (type == TriggerBoot) atBoot = true;
                    }
                    finally { Release(trigger); }
                }
            }
            finally { Release(triggers); }
            if (!atLogon && !atBoot) return null;

            string? command = null, arguments = null;
            dynamic actions = definition.Actions;
            try
            {
                foreach (dynamic action in actions)
                {
                    try
                    {
                        if (command is null && (int)action.Type == ActionExec)
                        {
                            command = (string?)action.Path;
                            arguments = (string?)action.Arguments;
                        }
                    }
                    finally { Release(action); }
                }
            }
            finally { Release(actions); }

            string? author = null, runAs = null;
            bool highest = false;
            dynamic info = definition.RegistrationInfo;
            try { author = (string?)info.Author; } finally { Release(info); }
            dynamic principal = definition.Principal;
            try
            {
                runAs = (string?)principal.UserId;
                highest = (int)principal.RunLevel == RunLevelHighest;
            }
            finally { Release(principal); }

            return new TaskInfo((string)task.Path, (bool)task.Enabled, atLogon, atBoot, command, arguments, Clean(author), Clean(runAs), highest);
        }
        finally { Release(definition); }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Task Scheduler reports "never" as a date near year 1899/1999.</summary>
    private static DateTime? Time(DateTime local) => local.Year < 2000 ? null : local.ToUniversalTime();

    private static T WithService<T>(Func<dynamic, T> read, T whenUnavailable)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return whenUnavailable;
        dynamic? service = null;
        try
        {
            service = Activator.CreateInstance(type);
            if (service is null) return whenUnavailable;
            service.Connect();
            return read(service);
        }
        catch (COMException) { return whenUnavailable; }
        catch (UnauthorizedAccessException) { return whenUnavailable; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return whenUnavailable; }
        finally { Release(service); }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
