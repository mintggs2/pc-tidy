using System.Management;
using Microsoft.Win32;

namespace PcTidy.Features.Boost.Detection;

/// <summary>The processor's brand string from HARDWARE\DESCRIPTION (read-only; the maker's own text, the same in every language).</summary>
public static class CpuBrandReader
{
    public static string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Steam's own "a game is running" flag.</summary>
public interface ISteamStatus
{
    /// <summary>HKCU\Software\Valve\Steam\RunningAppID: the running game's app id, 0 or null when none.</summary>
    int? RunningAppId();
}

/// <summary>Reads Steam's per-user registry value (read-only). Null when Steam isn't installed.</summary>
public sealed class RegistrySteamStatus : ISteamStatus
{
    public int? RunningAppId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: false);
            return key?.GetValue("RunningAppID") is int id ? id : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>No Steam flag (sandbox and PCs without Steam).</summary>
public sealed class NoSteamStatus : ISteamStatus
{
    public static readonly NoSteamStatus Instance = new();
    public int? RunningAppId() => null;
}

/// <summary>How a user-picked app was started, so it can be reopened the same way after the game.</summary>
public interface ICommandLineReader
{
    /// <summary>The full command line of a process of the same user, or null when Windows won't say.</summary>
    string? CommandLine(int pid);
}

/// <summary>
/// Win32_Process.CommandLine through WMI (read-only). Only ever asked about apps the user picked to close: never
/// the game, a launcher, anti-cheat or Windows (the caller checks <see cref="BoostDenyList"/> first).
/// </summary>
public sealed class WmiCommandLineReader : ICommandLineReader
{
    public string? CommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
                using (o)
                    return o["CommandLine"] as string;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // WMI unavailable or the process ended: reopen without arguments.
        }
        return null;
    }
}

/// <summary>Sandbox and tests: no command lines.</summary>
public sealed class NoCommandLineReader : ICommandLineReader
{
    public static readonly NoCommandLineReader Instance = new();
    public string? CommandLine(int pid) => null;
}
