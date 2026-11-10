using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcTidy.Features.Cleanup.SystemState;

/// <summary>Real Windows 10 22H2 / 11 implementation. Every registry key is opened read-only.</summary>
internal sealed class WindowsSystemState : ISystemState
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string ReserveManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager";

    private static readonly string[] RestartPendingKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
    };

    public bool IsRestartPending() => RestartPendingKeys.Any(KeyExists);

    public RecycleBinInfo? QueryRecycleBin(CancellationToken ct)
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        // null root = every drive's bin for the current user. Read-only query.
        return NativeMethods.SHQueryRecycleBin(null, ref info) == 0 ? new RecycleBinInfo(info.i64Size, info.i64NumItems) : null;
    }

    public PowerState ReadPower()
    {
        var enabled = ReadDword(PowerKey, "HibernateEnabled");
        var type = ReadDword(PowerKey, "HiberFileType");
        bool? hasBattery = null;
        if (NativeMethods.GetSystemPowerStatus(out var ps))
            hasBattery = ps.BatteryFlag switch { 128 => false, 255 => null, _ => true }; // 128 = no battery, 255 = unknown
        long? memory = null;
        var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref ms)) memory = (long)ms.ullTotalPhys;
        return new PowerState(enabled is null ? null : enabled != 0, type, hasBattery, memory);
    }

    public IReadOnlyList<string>? PagingFiles()
    {
        try
        {
            using var key = OpenLocalMachine(MemoryManagementKey);
            return key?.GetValue("PagingFiles") switch
            {
                string[] lines => lines,
                string line => new[] { line },
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool? ReservedStorageOn()
    {
        // ActiveScenario is non-zero while reserved storage is in use; ShippedWithReserves is the factory default.
        var active = ReadDword(ReserveManagerKey, "ActiveScenario");
        if (active is not null) return active != 0;
        var shipped = ReadDword(ReserveManagerKey, "ShippedWithReserves");
        return shipped is null ? null : shipped != 0;
    }

    public bool? CompactOsOn()
    {
        // CompactOS stores system binaries WOF-compressed, which shows up as a reparse point on them.
        // Reading attributes is harmless and works for standard users; no command output is parsed.
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var samples = new[] { Path.Combine(windows, "explorer.exe"), Path.Combine(windows, "System32", "ntoskrnl.exe") };
            return samples.All(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Shadow storage lives in System Volume Information, which only admins can read.</summary>
    public long? RestoreStorageBytes(CancellationToken ct) => null;

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = OpenLocalMachine(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static int? ReadDword(string keyPath, string name)
    {
        try
        {
            using var key = OpenLocalMachine(keyPath);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static RegistryKey? OpenLocalMachine(string path)
    {
        using var hklm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
        return hklm.OpenSubKey(path, writable: false);
    }
}
