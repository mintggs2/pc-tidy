using System.Globalization;
using System.Security;
using Microsoft.Win32;
using Win32Hive = Microsoft.Win32.RegistryHive;

namespace PcTidy.Features.Privacy.Common;

/// <summary>One DWORD as read: missing (key or value absent), a number, or a value of another type (left alone).</summary>
public readonly record struct DwordRead(bool Exists, uint? Value)
{
    public static readonly DwordRead Missing = new(false, null);
    /// <summary>Present but not a DWORD: PC Tidy doesn't touch it.</summary>
    public bool IsOtherType => Exists && Value is null;
}

/// <summary>
/// Read-only access to the current user's registry (HKCU, 64-bit view), optionally redirected below
/// <c>HKCU\Software\PCTidyTest</c> so tests and the simulation sandbox never read or write a real setting.
/// <see cref="Map"/> gives the physical key for both the reads and the plan items built from them, so a plan always
/// writes exactly where the scan read. Unreadable or vanished keys read as missing, never as an exception.
/// </summary>
public sealed class UserRegistry
{
    private UserRegistry(string? root) => Root = root;

    /// <summary>The real HKCU.</summary>
    public static readonly UserRegistry Real = new(null);

    /// <summary>Everything below <paramref name="testRoot"/>, which must be under HKCU\Software\PCTidyTest.</summary>
    public static UserRegistry Redirected(string testRoot)
    {
        var root = testRoot.Trim().Trim('\\');
        if (!root.StartsWith(PrivacyIds.TestRegistryArea + "\\", StringComparison.OrdinalIgnoreCase) || root.Split('\\').Any(p => p.Trim() is "." or ".."))
            throw new ArgumentException(@"Redirected registry roots must be below HKCU\Software\PCTidyTest.", nameof(testRoot));
        return new UserRegistry(root);
    }

    /// <summary>The test root, or null for the real HKCU.</summary>
    public string? Root { get; }

    /// <summary>Logical HKCU key (as Windows names it) → the key really read and written.</summary>
    public string Map(string keyPath) => Root is null ? keyPath.Trim('\\') : $@"{Root}\{keyPath.Trim('\\')}";

    public bool KeyExists(string keyPath)
    {
        using var key = Open(keyPath);
        return key is not null;
    }

    public IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return Array.Empty<string>(); }
    }

    public DwordRead ReadDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Open(keyPath);
            if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) return DwordRead.Missing;
            if (key.GetValueKind(valueName) != RegistryValueKind.DWord) return new DwordRead(true, null);
            var raw = key.GetValue(valueName);
            return raw is null ? DwordRead.Missing : new DwordRead(true, unchecked((uint)Convert.ToInt32(raw, CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return DwordRead.Missing; }
    }

    public string? ReadString(string keyPath, string valueName)
    {
        try
        {
            using var key = Open(keyPath);
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    private RegistryKey? Open(string keyPath)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(Win32Hive.CurrentUser, RegistryView.Registry64);
            return root.OpenSubKey(Map(keyPath), writable: false);
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return null; }
    }

    private static bool IsReadProblem(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or ObjectDisposedException;
}
