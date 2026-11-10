using System.Diagnostics;
using PcTidy.Setup.Install;

namespace PcTidy.Setup.Windows;

/// <summary>
/// PC Tidy's scheduled tasks: found through the Task Scheduler COM API (no parsing of localized schtasks output),
/// each deleted with <c>schtasks /Delete /TN "..." /F</c>, and only if it exists.
/// </summary>
public sealed class WindowsTaskCleaner : ITaskCleaner
{
    private const int IncludeHidden = 1;

    public IReadOnlyList<string> FindPcTidyTasks()
    {
        var found = new List<string>();
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            foreach (dynamic task in root.GetTasks(IncludeHidden))
            {
                string name = task.Name;
                if (name.StartsWith(Product.TaskNamePrefix, StringComparison.OrdinalIgnoreCase) && IsMine(task)) found.Add("\\" + name);
            }
            try
            {
                dynamic folder = service.GetFolder("\\" + Product.TaskFolder);
                foreach (dynamic task in folder.GetTasks(IncludeHidden))
                    if (IsMine(task)) found.Add($"\\{Product.TaskFolder}\\{(string)task.Name}");
            }
            catch { /* no "\PC Tidy" folder: nothing there */ }
        }
        catch { /* Task Scheduler unavailable: nothing we can find */ }
        return found;
    }

    public string? Delete(string taskPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                ArgumentList = { "/Delete", "/TN", taskPath, "/F" },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) return "Task Scheduler didn't answer.";
            if (p.ExitCode != 0) return $"Task Scheduler couldn't remove it (code {p.ExitCode}).";
            TryRemoveEmptyFolder();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// On a per-machine install every account's tasks share the "\PC Tidy" folder: only the ones that run as this
    /// account are this uninstall's to remove. A task with no account recorded is treated as this account's.
    /// </summary>
    private static bool IsMine(dynamic task)
    {
        string? userId;
        try { userId = (string?)task.Definition.Principal.UserId; }
        catch { return true; }
        return IsCurrentUser(userId);
    }

    internal static bool IsCurrentUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return true;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var bare = me.Name.Contains('\\') ? me.Name[(me.Name.LastIndexOf('\\') + 1)..] : me.Name;
        return string.Equals(userId, me.User?.Value, StringComparison.OrdinalIgnoreCase)
               || string.Equals(userId, me.Name, StringComparison.OrdinalIgnoreCase)
               || string.Equals(userId, bare, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRemoveEmptyFolder()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\" + Product.TaskFolder);
            if ((int)folder.GetTasks(IncludeHidden).Count == 0) service.GetFolder("\\").DeleteFolder(Product.TaskFolder, 0);
        }
        catch { /* folder missing or not empty */ }
    }
}
