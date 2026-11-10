using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Setup.Install;

namespace PcTidy.Setup.Windows;

/// <summary>
/// Running copies of PC Tidy / Rescue started from the install folder, in this session or another account's.
/// Asks them to close; never kills. The program path is read with limited query rights
/// (<c>QueryFullProcessImageName</c>), not <c>MainModule</c>, so the window and the admin part see the same list
/// wherever Windows allows it.
/// </summary>
public sealed class WindowsAppProcesses : IAppProcesses
{
    public IReadOnlyList<RunningApp> FindRunning(string installDir)
    {
        var list = new List<RunningApp>();
        var self = Environment.ProcessId;
        int mySession = CurrentSession();
        foreach (var name in new[] { Path.GetFileNameWithoutExtension(Product.AppExe), Path.GetFileNameWithoutExtension(Product.RescueExe) })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == self) continue;
                        var path = ImagePath(p.Id);
                        if (path is null || !FolderSafety.IsSameOrUnder(path, installDir)) continue;
                        int session = p.SessionId;
                        bool other = session != mySession;
                        list.Add(new RunningApp(p.Id, name, path) { InOtherSession = other, Account = other ? SessionAccount(session) : null });
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited meanwhile */ }
                }
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<RunningApp>> CloseAsync(IReadOnlyList<RunningApp> apps, TimeSpan wait, CancellationToken ct)
    {
        var still = new List<RunningApp>();
        foreach (var app in apps)
        {
            // A window in another account's session can't be reached from here: that account has to close it.
            if (app.InOtherSession) { still.Add(app); continue; }
            try
            {
                using var p = Process.GetProcessById(app.ProcessId);
                p.CloseMainWindow();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(wait);
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { still.Add(app); }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already gone */ }
        }
        return still;
    }

    public void Launch(string exePath)
        => Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" });

    public bool RunQuiet(string exePath, string argument, TimeSpan wait)
    {
        if (!File.Exists(exePath)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false, CreateNoWindow = true, ArgumentList = { argument },
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            });
            return p is not null && p.WaitForExit(wait) && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    // ------------------------------------------------------------------ read-only Windows lookups

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int WtsUserName = 5;

    /// <summary>The program file of a process, or null when Windows doesn't let this account look (or it exited).</summary>
    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static int CurrentSession()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    /// <summary>The user name signed in to a session, or null when Windows doesn't say.</summary>
    private static string? SessionAccount(int sessionId)
    {
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsUserName, out var buffer, out _) || buffer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(buffer) is { Length: > 0 } name ? name : null; }
            finally { WTSFreeMemory(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
