using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Running;

/// <summary>What "Running now" may offer for an app.</summary>
public enum AppClass
{
    /// <summary>A normal app with a window in your session: "Close" is offered (it asks the app politely).</summary>
    Closable,
    /// <summary>Background helpers and tray apps: shown, not offered (there's no window to close politely).</summary>
    Background,
    /// <summary>Windows itself, security or anti-cheat: never offered (hard deny-list).</summary>
    Protected,
}

/// <summary>
/// Research §4 rule: Windows core processes and security software can't be ended; "Close" only asks an app with a
/// main window to close (WM_CLOSE), and "End task" isn't offered at all. The SafetyGuard has its own deny-list too.
/// </summary>
public static class BackgroundAppRules
{
    public static AppClass Classify(ProcessSnapshot p, FileFacts facts, int currentSession, string windowsDir)
    {
        if (IsProtected(p, facts, windowsDir)) return AppClass.Protected;
        if (p.SessionId != currentSession || p.Pid == Environment.ProcessId) return AppClass.Background;
        return p.HasMainWindow ? AppClass.Closable : AppClass.Background;
    }

    public static bool IsProtected(ProcessSnapshot p, FileFacts facts, string windowsDir)
    {
        if (p.Pid <= 4 || p.SessionId == 0) return true; // the kernel and everything in the services session
        if (SpeedProtectedNames.Matches(p.Name, SpeedProtectedNames.NeverCloseProcesses)) return true;
        if (SpeedProtectedNames.IsProtectedPublisher(facts.Company, facts.Signer)) return true;
        // Programs inside the Windows folder are part of Windows (Notepad, Paint and co. live elsewhere on Windows 11).
        return p.ImagePath is { } path && PathUtil.IsUnder(path, windowsDir) && !IsUserFacingWindowsApp(p.Name);
    }

    /// <summary>Windows programs that are ordinary apps you open and close yourself.</summary>
    private static readonly string[] UserFacingWindowsApps = { "notepad", "mspaint", "wordpad", "write", "charmap", "magnify", "osk", "mstsc" };

    private static bool IsUserFacingWindowsApp(string name) => SpeedProtectedNames.Matches(name, UserFacingWindowsApps);
}
