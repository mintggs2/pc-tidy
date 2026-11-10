using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Features.Boost.Detection;

/// <summary>A process from the system's process list, read without opening the process.</summary>
public readonly record struct ProcessEntry(int Pid, string Name, int SessionId);

/// <summary>What a limited-rights query returns: the exe path and the start time (both may be unknown).</summary>
public sealed record ProcessDetails(string? ImagePath, DateTime? StartUtc);

/// <summary>Process access rights Game boost may ask for. There is exactly one.</summary>
public static class ProcessAccess
{
    /// <summary>
    /// PROCESS_QUERY_LIMITED_INFORMATION (0x1000): exe path and times only. It can't read memory, list modules,
    /// suspend, terminate or re-prioritise, and anti-cheats allow it (Task Manager uses the same right).
    /// </summary>
    public const uint QueryLimitedInformation = 0x1000;
}

/// <summary>
/// The only way Game boost looks at running programs. Real: <see cref="WindowsProcessQuery"/>. Tests: a fake that
/// records every access mask asked for, so a test proves nothing more than limited rights is ever requested.
/// </summary>
public interface IProcessQuery
{
    int CurrentSessionId { get; }

    /// <summary>Pid, name and session of every process (the system's process list; no process is opened).</summary>
    IReadOnlyList<ProcessEntry> List();

    /// <summary>Opens the process with exactly <paramref name="desiredAccess"/>, reads its exe path and start time, closes it.</summary>
    ProcessDetails? Query(int pid, uint desiredAccess);

    /// <summary>Processes that own a visible top-level window (one window enumeration; no process is opened).</summary>
    IReadOnlySet<int> PidsWithWindows();
}

/// <summary>Real process list. Read-only: OpenProcess with the caller's mask, QueryFullProcessImageName, GetProcessTimes.</summary>
public sealed class WindowsProcessQuery : IProcessQuery
{
    public int CurrentSessionId
    {
        get
        {
            using var me = Process.GetCurrentProcess();
            return me.SessionId;
        }
    }

    public IReadOnlyList<ProcessEntry> List()
    {
        var list = new List<ProcessEntry>();
        // GetProcesses reads the system's process snapshot; Id, ProcessName and SessionId come from it, no handle needed.
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new ProcessEntry(p.Id, p.ProcessName, p.SessionId)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Ended while we looked.
                }
            }
        }
        return list;
    }

    public ProcessDetails? Query(int pid, uint desiredAccess)
    {
        var handle = OpenProcess(desiredAccess, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            string? path = null;
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) path = buffer.ToString();
            DateTime? start = GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
                ? DateTime.FromFileTimeUtc(creation)
                : null;
            return new ProcessDetails(path, start);
        }
        finally { CloseHandle(handle); }
    }

    public IReadOnlySet<int> PidsWithWindows()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                pids.Add(owner);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private const uint GwOwner = 4;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
}
