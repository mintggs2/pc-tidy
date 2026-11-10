using Win32 = Microsoft.Win32;

namespace PcTidy.Features.Schedule.Common;

/// <summary>A registry value as read: its kind and data, or "not there".</summary>
public sealed record RegistryReading(bool KeyExists, bool ValueExists, Win32.RegistryValueKind Kind, object? Data)
{
    public static readonly RegistryReading NoKey = new(false, false, Win32.RegistryValueKind.Unknown, null);
    public static readonly RegistryReading NoValue = new(true, false, Win32.RegistryValueKind.Unknown, null);
}

/// <summary>Read-only registry access (opened with writable: false only).</summary>
public interface IRegistryReader
{
    RegistryReading Read(RegistryHive hive, string keyPath, string valueName, bool view64 = true);
    IReadOnlyList<string> ValueNames(RegistryHive hive, string keyPath, bool view64 = true);
}

public sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();

    public RegistryReading Read(RegistryHive hive, string keyPath, string valueName, bool view64 = true)
    {
        try
        {
            using var root = Win32.RegistryKey.OpenBaseKey(Map(hive), view64 ? Win32.RegistryView.Registry64 : Win32.RegistryView.Registry32);
            using var key = root.OpenSubKey(keyPath, writable: false);
            if (key is null) return RegistryReading.NoKey;
            var data = key.GetValue(valueName, null, Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
            return data is null ? RegistryReading.NoValue : new RegistryReading(true, true, key.GetValueKind(valueName), data);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RegistryReading.NoKey;
        }
    }

    public IReadOnlyList<string> ValueNames(RegistryHive hive, string keyPath, bool view64 = true)
    {
        try
        {
            using var root = Win32.RegistryKey.OpenBaseKey(Map(hive), view64 ? Win32.RegistryView.Registry64 : Win32.RegistryView.Registry32);
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static Win32.RegistryHive Map(RegistryHive hive)
        => hive == RegistryHive.LocalMachine ? Win32.RegistryHive.LocalMachine : Win32.RegistryHive.CurrentUser;
}
