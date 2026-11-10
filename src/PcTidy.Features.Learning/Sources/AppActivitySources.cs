using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PcTidy.Core;

namespace PcTidy.Features.Learning.Sources;

/// <summary>
/// Programs running right now in this user's session (their .exe paths). Windows' UserAssist only counts starts from
/// Start, the taskbar or a shortcut, so an app that starts with Windows or is reopened from its tray icon is never
/// counted there; seeing it running is the other proof it's used. Read-only.
/// </summary>
public interface IRunningAppsSource
{
    IReadOnlyCollection<string> RunningProgramPaths();
}

/// <summary>Programs Windows starts at sign-in for this user (Run keys and Startup folders). Read-only.</summary>
public interface IStartupAppsSource
{
    /// <summary>Program paths from the Run keys, and the names of the Startup folders' shortcuts ("Spotify").</summary>
    (IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> ShortcutNames) StartupPrograms();
}

/// <summary>
/// The running processes of this session, read with limited query rights (<c>QueryFullProcessImageName</c>), so no
/// admin prompt and nothing of another account. Processes it can't open are simply not listed.
/// </summary>
public sealed class WindowsRunningApps : IRunningAppsSource
{
    public static readonly WindowsRunningApps Instance = new();

    public IReadOnlyCollection<string> RunningProgramPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    if (ImagePath(p.Id) is { } path) paths.Add(path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited meanwhile */ }
            }
        }
        return paths;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// HKCU and HKLM Run keys (64- and 32-bit views) and the user's and all-users Startup folders. Only read.
/// </summary>
public sealed class WindowsStartupApps : IStartupAppsSource
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IRegistryKeyReader _registry;
    private readonly Func<IEnumerable<string>> _startupFolders;
    private readonly bool _readMachine;

    /// <param name="registry">Where HKCU's Run key is read (tests: a redirected tree).</param>
    /// <param name="startupFolders">Startup folders (default: the user's and all users').</param>
    /// <param name="readMachine">Also HKLM's Run keys (real PC only).</param>
    public WindowsStartupApps(IRegistryKeyReader registry, Func<IEnumerable<string>>? startupFolders = null, bool readMachine = true)
    {
        _registry = registry;
        _readMachine = readMachine;
        _startupFolders = startupFolders ?? (() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        });
    }

    public (IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> ShortcutNames) StartupPrograms()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var user = _registry.OpenCurrentUser(RunKey)) AddCommands(user, paths);
        if (_readMachine)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var run = hklm.OpenSubKey(RunKey, writable: false);
                    AddCommands(run, paths);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* not readable */ }
            }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _startupFolders())
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) paths.Add(file);
                    else if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not readable */ }
        }
        return (paths, names);
    }

    private static void AddCommands(RegistryKey? key, HashSet<string> paths)
    {
        if (key is null) return;
        string[] names;
        try { names = key.GetValueNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return; }
        foreach (var name in names)
        {
            try
            {
                if (key.GetValue(name) is string command && ProgramOf(command) is { } program) paths.Add(program);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* skip */ }
        }
    }

    /// <summary>The program of a Run command line: <c>"C:\A b\x.exe" --tray</c> or <c>C:\A b\x.exe /min</c>.</summary>
    public static string? ProgramOf(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end <= 1) return null;
            path = c[1..end];
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            path = c[..(exe + 4)];
        }
        try { return Path.IsPathFullyQualified(path) ? PathUtil.Normalize(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
