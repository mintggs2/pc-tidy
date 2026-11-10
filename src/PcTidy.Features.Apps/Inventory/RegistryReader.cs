using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>
/// Read-only access to the registry places the Apps section looks at (Uninstall lists, UserAssist, leftover keys).
/// Injectable so tests can point every read at a fake tree under HKCU\Software\PCTidyTest\apps.
/// Implementations only ever open keys read-only.
/// </summary>
public interface IRegistryReader
{
    /// <summary>Opens a key read-only, or null if it doesn't exist / can't be read.</summary>
    /// <param name="view64">True = 64-bit view; false = the 32-bit (WOW6432Node) view. Ignored for HKCU.</param>
    RegistryKey? Open(Hive hive, bool view64, string path);
}

/// <summary>The real registry of this PC (read-only).</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();

    public RegistryKey? Open(Hive hive, bool view64, string path)
    {
        try
        {
            var baseHive = hive == Hive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var view = hive == Hive.CurrentUser ? RegistryView.Default : view64 ? RegistryView.Registry64 : RegistryView.Registry32;
            using var root = RegistryKey.OpenBaseKey(baseHive, view);
            return root.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a fake registry laid out under one HKCU key (tests): &lt;root&gt;\HKLM64\..., &lt;root&gt;\HKLM32\..., &lt;root&gt;\HKCU\...
/// Only HKCU is ever opened, so a test can never read or depend on the real machine hive.
/// </summary>
public sealed class RedirectedRegistryReader : IRegistryReader
{
    /// <param name="hkcuRoot">Key under HKCU, e.g. "Software\PCTidyTest\apps\run1".</param>
    public RedirectedRegistryReader(string hkcuRoot) => Root = hkcuRoot.Trim('\\');

    public string Root { get; }

    public static string Prefix(Hive hive, bool view64)
        => hive == Hive.CurrentUser ? "HKCU" : view64 ? "HKLM64" : "HKLM32";

    public RegistryKey? Open(Hive hive, bool view64, string path)
    {
        try { return Registry.CurrentUser.OpenSubKey($@"{Root}\{Prefix(hive, view64)}\{path.Trim('\\')}", writable: false); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Small never-throwing read helpers.</summary>
internal static class RegistryValues
{
    public static string? String(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.None) switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                string[] multi => multi.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim(),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>DWORD/QWORD, or a string holding a number (some installers write "1" as text).</summary>
    public static long? Number(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int i => (uint)i,
                long l => l,
                string s when long.TryParse(s.Trim(), out var v) => v,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static byte[]? Binary(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as byte[]; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static string[] SubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }

    public static string[] ValueNames(RegistryKey key)
    {
        try { return key.GetValueNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }

    public static RegistryKey? OpenChild(RegistryKey key, string name)
    {
        try { return key.OpenSubKey(name, writable: false); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>When the key was last changed (RegQueryInfoKey). Used as an approximate install date.</summary>
    public static DateTime? LastWriteUtc(RegistryKey key)
    {
        try
        {
            var rc = RegQueryInfoKeyW(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var fileTime);
            if (rc != 0 || fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException or System.Security.SecurityException) { return null; }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);
}
