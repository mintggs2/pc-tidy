using System.Diagnostics;
using System.Security.Principal;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>Read-only facts about other processes (who runs them, are they still the same process).</summary>
internal static class ProcessIdentity
{
    /// <summary>User SID of a process, or null if it can't be read (e.g. it belongs to another account).</summary>
    public static SecurityIdentifier? UserOf(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User;
            }
            finally { CloseHandle(token); }
        }
        catch
        {
            return null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Full path of a process's program file (QueryFullProcessImageName), or null if it can't be read.</summary>
    public static string? ImagePath(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(32 * 1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>True if the process runs exactly this program file (same full path, ignoring case).</summary>
    public static bool RunsImage(int processId, string? expectedImage)
        => expectedImage is { Length: > 0 } && ImagePath(processId) is { } actual
           && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase);

    /// <summary>Start time of a process (UTC), or null if unknown.</summary>
    public static DateTime? StartTimeUtc(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return p.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if a process with this id runs and (when <paramref name="startedUtc"/> is known) is the same
    /// process as before, so a recycled PID doesn't count as "still alive".
    /// </summary>
    public static bool IsAlive(int processId, DateTime? startedUtc)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            if (p.HasExited) return false;
            if (startedUtc is { } s && StartTimeUtc(processId) is { } now && Math.Abs((now - s).TotalSeconds) > 1) return false;
            return true;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch
        {
            return true; // exists but can't be inspected (another account): treat as alive
        }
    }
}
