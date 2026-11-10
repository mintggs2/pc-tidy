using Microsoft.Win32;
using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.App.Shell;

/// <summary>
/// "System Protection is off" at start (real PC only, never in the sandbox or the demo): without it Windows makes no
/// restore point before PC Tidy's system changes. Read-only (two registry values); closing the notice hides it for
/// the rest of this session.
/// </summary>
public static class SystemProtectionNotice
{
    public const string Title = "System Protection is off";
    public const string Message = "System Protection is off, so PC Tidy can't make restore points before system changes. "
                                  + "Undo still puts back what PC Tidy changed, but there's no Windows restore point to fall back on.";
    public const string ButtonText = "Open System protection";

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    private static bool? _off;
    private static bool _dismissed;

    /// <summary>True when the notice should show now: real PC, protection off (or blocked by policy), not closed this session.</summary>
    public static bool ShouldShow(bool isSandboxOrDemo) => !isSandboxOrDemo && !_dismissed && (_off ??= ReadIsOff());

    /// <summary>The user closed it: not shown again until PC Tidy starts again.</summary>
    public static void Dismiss() => _dismissed = true;

    /// <summary>Testable rule: off when Windows says protection is off for the Windows drive, or a policy disables it.</summary>
    public static bool IsOff(bool? protectionOn, int? disableSrPolicy) => protectionOn == false || disableSrPolicy == 1;

    private static bool ReadIsOff()
    {
        try
        {
            var on = new WindowsSystemProbe(new RegistryReader(RegistryRedirect.None)).SystemProtectionOn;
            return IsOff(on, ReadPolicy());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static int? ReadPolicy()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(PolicyKey);
            return key?.GetValue("DisableSR") is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
