using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>A running process as seen right before closing it.</summary>
public sealed record ProcessFacts(int Pid, string Name, DateTime StartUtc, int SessionId);

/// <summary>Running apps and their windows. Real: Win32 (EnumWindows + PostMessage WM_CLOSE). Tests: a fake.</summary>
public interface IAppWindowControl
{
    bool TouchesRealSystem { get; }
    int CurrentSessionId { get; }
    ProcessFacts? Query(int pid);
    /// <summary>Posts WM_CLOSE to the process's visible top-level windows. Returns how many got the message.</summary>
    int PostClose(int pid);
    /// <summary>True when that exact process (same start time) is gone within the timeout.</summary>
    Task<bool> WaitForExitAsync(ProcessFacts process, TimeSpan timeout, CancellationToken ct);
}

public sealed class WindowsAppWindowControl : IAppWindowControl
{
    private const uint WmClose = 0x0010;
    private const uint GwOwner = 4;

    public bool TouchesRealSystem => true;
    public int CurrentSessionId => Process.GetCurrentProcess().SessionId;

    public ProcessFacts? Query(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new ProcessFacts(pid, p.ProcessName, p.StartTime.ToUniversalTime(), p.SessionId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public int PostClose(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows.Count(w => PostMessageW(w, WmClose, IntPtr.Zero, IntPtr.Zero));
    }

    public async Task<bool> WaitForExitAsync(ProcessFacts process, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var now = Query(process.Pid);
            if (now is null || Math.Abs((now.StartUtc - process.StartUtc).TotalSeconds) > 1) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// CloseApp. Args: "pid", "processName", optional "startedUtc" (round-trip time the scan saw). Asks the app to close
/// the way clicking X does (WM_CLOSE to its main windows), so it can ask you to save. PC Tidy never kills a process.
/// Refused: Windows itself, security software, anti-cheat, PC Tidy, another sign-in session, and a PID that now belongs
/// to a different program (name or start time changed). Not reversible: you open the app again yourself.
/// </summary>
public sealed class CloseAppHandler(IAppWindowControl apps) : SystemActionHandler
{
    internal static readonly TimeSpan WaitForClose = TimeSpan.FromSeconds(20);

    /// <summary>Same list as the SafetyGuard (a trailing * matches a prefix).</summary>
    internal static readonly string[] NeverClose =
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm",
        "explorer", "sihost", "fontdrvhost", "ctfmon", "conhost", "taskhostw", "spoolsv", "audiodg", "LogonUI", "RuntimeBroker",
        "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "MsMpEng", "NisSrv", "SecurityHealthService",
        "SecurityHealthSystray", "MpDefenderCoreService", "SgrmBroker", "TrustedInstaller", "TiWorker", "MsiExec", "wuauclt",
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "vgc", "vgtray", "FACEIT*", "ESEA*", "PnkBstr*", "PcTidy*",
    };

    public CloseAppHandler() : this(new WindowsAppWindowControl()) { }

    protected override ActionKind Kind => ActionKind.CloseApp;
    protected override bool TouchesRealSystem => apps.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static int Pid(PlanItem item) => int.Parse(item.Arg("pid").Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
    private static string Name(PlanItem item) => Path.GetFileNameWithoutExtension(item.Arg("processName").Trim());

    internal static bool IsNeverClosed(string name)
        => NeverClose.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase));

    protected override string? Validate(PlanItem item)
    {
        if (!int.TryParse(item.Arg("pid").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4) return "This step doesn't name a running app.";
        var name = Name(item);
        if (name.Length == 0 || name.Length > 260 || name.Any(c => char.IsControl(c) || c is '\\' or '/' or ':')) return "This step doesn't name a running app.";
        if (pid == Environment.ProcessId || IsNeverClosed(name)) return "Windows, security software and anti-cheat programs are never closed by PC Tidy.";
        var started = item.Arg("startedUtc").Trim();
        if (started.Length > 0 && !DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return "The app's start time is not valid.";
        return null;
    }

    protected override string Describe(PlanItem item) => $"Would ask {Name(item)} (process {Pid(item)}) to close, like clicking X (WM_CLOSE). It is never forced";

    protected override async Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var name = Name(step.Item);
        var process = apps.Query(Pid(step.Item));
        if (process is null) return step.Skipped($"{name} isn't running any more.");
        if (!process.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return step.Skipped("That app already closed (another program now has its number).");
        if (DateTime.TryParse(step.Item.Arg("startedUtc").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expected)
            && Math.Abs((process.StartUtc - expected.ToUniversalTime()).TotalSeconds) > 1)
            return step.Skipped($"{name} was restarted since the scan, so PC Tidy left it alone.");
        if (process.SessionId != apps.CurrentSessionId) return step.Skipped("This app belongs to another sign-in, so PC Tidy left it alone.");

        if (apps.PostClose(process.Pid) == 0) return step.Skipped($"{name} has no window to close. PC Tidy never force-closes apps.");
        return await apps.WaitForExitAsync(process, WaitForClose, ct).ConfigureAwait(false)
            ? step.Done($"{name} closed.")
            : step.Skipped($"{name} is still open. It may be asking you to save something. PC Tidy never force-closes apps.");
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Skipped("Open the app again yourself if you need it. Nothing else was changed."));

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(apps.Query(Pid(step.Item)) is not { } p || !p.Name.Equals(Name(step.Item), StringComparison.OrdinalIgnoreCase));
}
