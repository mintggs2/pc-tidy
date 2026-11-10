using System.Diagnostics;
using static PcTidy.Features.Services.Sources.NativeMethods;

namespace PcTidy.Features.Services.Sources;

/// <summary>
/// Memory (from Windows' process list, no handle needed) and CPU time (one limited-rights query per process) of the
/// processes that host running services. Never opens a process with rights that could change it.
/// </summary>
public sealed class WindowsProcessUsage : IProcessUsageSource
{
    public IReadOnlyDictionary<int, ProcessUsage> Read(IReadOnlyCollection<int> pids)
    {
        var result = new Dictionary<int, ProcessUsage>();
        foreach (var pid in pids.Where(p => p > 4).Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                long privateBytes = 0;
                try { privateBytes = p.PrivateMemorySize64; }
                catch (Exception ex) when (IsGone(ex)) { }
                result[pid] = new ProcessUsage(pid, privateBytes, CpuTime(pid));
            }
            catch (Exception ex) when (IsGone(ex) || ex is ArgumentException)
            {
                // Ended while we looked, or not readable: no usage shown.
            }
        }
        return result;
    }

    private static TimeSpan? CpuTime(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(handle, out _, out _, out var kernel, out var user) ? TimeSpan.FromTicks(kernel + user) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static bool IsGone(Exception ex)
        => ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException;
}
