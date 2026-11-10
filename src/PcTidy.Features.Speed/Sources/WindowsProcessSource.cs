using System.Diagnostics;
using System.Text;
using static PcTidy.Features.Speed.Common.NativeMethods;

namespace PcTidy.Features.Speed.Sources;

/// <summary>
/// The running processes, read with limited query rights only (image path, CPU time, I/O bytes, start time).
/// Never opens a process for anything that could change it.
/// </summary>
public sealed class WindowsProcessSource : IProcessSource
{
    public int CurrentSessionId
    {
        get
        {
            using var me = Process.GetCurrentProcess();
            return me.SessionId;
        }
    }

    public IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4) continue; // Idle and System
                    var (path, start, cpu, io) = Query(p.Id);
                    long privateBytes = 0;
                    bool hasWindow = false;
                    string? title = null;
                    try { privateBytes = p.PrivateMemorySize64; } catch (Exception ex) when (IsGone(ex)) { }
                    try
                    {
                        hasWindow = p.MainWindowHandle != IntPtr.Zero;
                        title = hasWindow && !string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.MainWindowTitle.Trim() : null;
                    }
                    catch (Exception ex) when (IsGone(ex)) { }
                    list.Add(new ProcessSnapshot(p.Id, p.ProcessName, path, SafeSession(p), start, cpu, privateBytes, io, hasWindow, title));
                }
                catch (Exception ex) when (IsGone(ex))
                {
                    // The process ended while we looked: skip it.
                }
            }
        }
        return list;
    }

    private static int SafeSession(Process p)
    {
        try { return p.SessionId; } catch (Exception ex) when (IsGone(ex)) { return -1; }
    }

    private static bool IsGone(Exception ex)
        => ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException;

    private static (string? Path, DateTime? Start, TimeSpan? Cpu, long? Io) Query(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return (null, null, null, null);
        try
        {
            string? path = null;
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString();

            DateTime? start = null;
            TimeSpan? cpu = null;
            if (GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
            {
                if (creation > 0) start = DateTime.FromFileTimeUtc(creation);
                cpu = TimeSpan.FromTicks(kernel + user);
            }

            long? io = GetProcessIoCounters(handle, out var counters)
                ? (long)Math.Min(long.MaxValue, counters.ReadTransferCount + counters.WriteTransferCount)
                : null;
            return (path, start, cpu, io);
        }
        finally { CloseHandle(handle); }
    }
}
