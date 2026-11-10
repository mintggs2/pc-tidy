using System.Security;
using Microsoft.Win32;
using Hive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Speed.Common;

/// <summary>
/// Read-only registry access for the Speed scanners. Keys are always opened read-only in the 64-bit view;
/// 32-bit entries are read through their explicit <c>WOW6432Node</c> paths. Tests swap in a
/// <see cref="RedirectedRegistryView"/> that reads a fake tree under <c>HKCU\Software\PCTidyTest\speed</c>.
/// </summary>
public interface IRegistryView
{
    /// <summary>Opens a key read-only; null when it doesn't exist or can't be read.</summary>
    RegistryKey? Open(Hive hive, string path);

    /// <summary>Full path as regedit shows it, for "Copy registry path".</summary>
    string Describe(Hive hive, string path);
}

/// <summary>The real registry, read-only.</summary>
public sealed class WindowsRegistryView : IRegistryView
{
    public static readonly WindowsRegistryView Instance = new();

    public RegistryKey? Open(Hive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Hive.CurrentUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine,
                RegistryView.Registry64);
            return root.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (RegistryRead.IsReadProblem(ex))
        {
            return null;
        }
    }

    public string Describe(Hive hive, string path)
        => (hive == Hive.CurrentUser ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\") + path.Trim('\\');
}

/// <summary>
/// Reads a fake registry: <c>HKCU\&lt;root&gt;\HKCU\...</c> stands for HKCU and <c>HKCU\&lt;root&gt;\HKLM\...</c> for HKLM.
/// Used by tests and by the sandbox, so nothing real is ever read as if it were the test PC.
/// </summary>
public sealed class RedirectedRegistryView : IRegistryView
{
    public const string DefaultRoot = @"Software\PCTidyTest\speed";

    public RedirectedRegistryView(string root = DefaultRoot) => Root = root.Trim('\\');

    public string Root { get; }

    public RegistryKey? Open(Hive hive, string path) => WindowsRegistryView.Instance.Open(Hive.CurrentUser, Map(hive, path));

    public string Describe(Hive hive, string path) => WindowsRegistryView.Instance.Describe(Hive.CurrentUser, Map(hive, path));

    public string Map(Hive hive, string path) => $@"{Root}\{(hive == Hive.CurrentUser ? "HKCU" : "HKLM")}\{path.Trim('\\')}";
}

/// <summary>Safe read helpers: a key that vanishes or can't be read gives "nothing", never an exception.</summary>
public static class RegistryRead
{
    public static bool IsReadProblem(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException or ArgumentException;

    public static string[] SubKeys(this RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (IsReadProblem(ex)) { return Array.Empty<string>(); }
    }

    public static string[] Values(this RegistryKey key)
    {
        try { return key.GetValueNames(); }
        catch (Exception ex) when (IsReadProblem(ex)) { return Array.Empty<string>(); }
    }

    public static RegistryKey? Child(this RegistryKey key, string name)
    {
        try { return key.OpenSubKey(name, writable: false); }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    public static bool HasValue(this RegistryKey key, string name)
        => key.Values().Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A string value (REG_SZ / REG_EXPAND_SZ, expanded unless asked not to). Null if missing or another type.</summary>
    public static string? Text(this RegistryKey key, string? name, bool expand = true)
    {
        try
        {
            var v = key.GetValue(name, null, expand ? RegistryValueOptions.None : RegistryValueOptions.DoNotExpandEnvironmentNames);
            return v as string;
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    public static int? Dword(this RegistryKey key, string name)
    {
        try { return key.GetValue(name) is int i ? i : null; }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    public static byte[]? Bytes(this RegistryKey key, string name)
    {
        try { return key.GetValue(name) as byte[]; }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    /// <summary>Opens <paramref name="path"/>, runs <paramref name="read"/> and closes the key. <paramref name="missing"/> if it isn't there.</summary>
    public static T With<T>(this IRegistryView reg, Hive hive, string path, Func<RegistryKey, T> read, T missing)
    {
        using var key = reg.Open(hive, path);
        if (key is null) return missing;
        try { return read(key); }
        catch (Exception ex) when (IsReadProblem(ex)) { return missing; }
    }
}
