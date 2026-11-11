using System.IO;
using Microsoft.Win32;
using PcTidy.Core;

namespace PcTidy.UI.Flow.Resume;

/// <summary>
/// The two read-only facts about Windows a resumable scan needs: which build and update revision is installed (to see
/// whether an update landed in between) and whether Windows Update is waiting to restart the PC. Implementations only
/// ever READ; nothing here writes or changes any Windows or update setting.
/// </summary>
public interface IWindowsStateReader
{
    /// <summary>"CurrentBuild.UBR" (for example "10000.1"), or null if it can't be read.</summary>
    string? WindowsBuild();
    /// <summary>True when Windows Update (or servicing) says a restart is pending. False when unknown.</summary>
    bool RestartPending();
}

/// <summary>
/// The real PC: reads HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion (CurrentBuild, UBR) and checks whether
/// HKLM\...\WindowsUpdate\Auto Update\RebootRequired or HKLM\...\Component Based Servicing\RebootPending exist.
/// Keys are opened read-only (<c>writable: false</c>); never used in practice or demo mode.
/// </summary>
public sealed class RegistryWindowsStateReader : IWindowsStateReader
{
    public const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    public const string WindowsUpdateRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    public const string ServicingRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    private static int _reads;

    /// <summary>How many times any instance looked at the registry (tests check practice mode never does).</summary>
    public static int Reads => Volatile.Read(ref _reads);

    public string? WindowsBuild()
    {
        Interlocked.Increment(ref _reads);
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = lm.OpenSubKey(CurrentVersionKey, writable: false);
            if (key?.GetValue("CurrentBuild") is not string build || build.Length == 0) return null;
            var ubr = key.GetValue("UBR") is int u ? u.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
            return build.Trim() + "." + ubr;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    public bool RestartPending()
    {
        Interlocked.Increment(ref _reads);
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var wu = lm.OpenSubKey(WindowsUpdateRebootKey, writable: false)) if (wu is not null) return true;
            using (var cbs = lm.OpenSubKey(ServicingRebootKey, writable: false)) if (cbs is not null) return true;
            return false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>Practice PC, demo and tests: fixed answers, no registry at all.</summary>
public sealed class SimulatedWindowsState : IWindowsStateReader
{
    public SimulatedWindowsState(string? build = "practice", bool restartPending = false)
    {
        Build = build;
        Pending = restartPending;
    }

    /// <summary>From a practice PC's simulated facts (Sim\sim.json), never the real one.</summary>
    public static SimulatedWindowsState From(ISystemInfo? simulated)
        => new(simulated is null ? "practice" : simulated.WindowsBuild.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0", simulated?.RebootPending ?? false);

    public string? Build { get; set; }
    public bool Pending { get; set; }
    public int Reads { get; private set; }

    public string? WindowsBuild() { Reads++; return Build; }
    public bool RestartPending() { Reads++; return Pending; }
}
