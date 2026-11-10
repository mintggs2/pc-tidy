using System.Runtime.InteropServices;
using System.Text;

namespace PcTidy.Features.Search.Backends;

/// <summary>Where a running Everything and its SDK library are (both must exist for PC Tidy to use it).</summary>
public sealed record EverythingInstall(string ExePath, string SdkPath);

/// <summary>Finds a running Everything. Tests inject their own.</summary>
public interface IEverythingLocator
{
    /// <summary>The running Everything with its SDK library, or null (not installed, not running, or no SDK file).</summary>
    EverythingInstall? Locate();
}

/// <summary>
/// Read-only detection: Everything's IPC window (the one its SDK talks to) tells us it's running; the window's
/// process gives Everything.exe's folder, where <c>Everything64.dll</c> (the SDK) must sit. The window proves nothing
/// (any process can register that class name): the backend loads the SDK only if <see cref="EverythingSdkTrust"/> accepts it.
/// Nothing is started, installed or downloaded: no Everything means PC Tidy's own index, silently.
/// </summary>
public sealed class EverythingLocator : IEverythingLocator
{
    public const string SdkFileName = "Everything64.dll";

    // Everything 1.4 and the 1.5 alpha register different IPC window classes.
    private static readonly string[] IpcWindowClasses = { "EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)" };

    public EverythingInstall? Locate()
    {
        try
        {
            foreach (var cls in IpcWindowClasses)
            {
                var hwnd = FindWindowW(cls, null);
                if (hwnd == IntPtr.Zero) continue;
                var exe = ProcessPathOf(hwnd);
                if (exe is null || Path.GetDirectoryName(exe) is not { Length: > 0 } dir) continue;
                // Only the running Everything's own folder. Whether the library there may be loaded is decided by
                // EverythingSdkTrust (admin-only folder, voidtools signature): any process can own such a window.
                var sdk = Path.Combine(dir, SdkFileName);
                if (File.Exists(sdk)) return new EverythingInstall(exe, sdk);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Can't tell: treat as "not available".
        }
        return null;
    }

    private static string? ProcessPathOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
