using Microsoft.Win32;
using PcTidy.Core.Model;
using CoreHive = PcTidy.Core.Model.RegistryHive;
using Win32Hive = Microsoft.Win32.RegistryHive;

namespace PcTidy.Platform;

/// <summary>Thrown when an existing value has a type PC Tidy couldn't put back exactly; such values are left alone.</summary>
public sealed class UnsupportedRegistryValueException(string message) : Exception(message);

/// <summary>
/// Exact-state registry reads and writes shared by the registry and startup handlers.
/// Every change is captured first as a <see cref="RegistryPriorState"/> (key existed? value existed? kind, data),
/// so undo puts back what was really there, including "nothing".
/// </summary>
internal static class RegistryAccess
{
    public static RegistryKey OpenBase(CoreHive hive, bool view64)
        => RegistryKey.OpenBaseKey(hive == CoreHive.LocalMachine ? Win32Hive.LocalMachine : Win32Hive.CurrentUser,
            view64 ? RegistryView.Registry64 : RegistryView.Registry32);

    public static string HiveShort(CoreHive hive) => hive == CoreHive.LocalMachine ? "HKLM" : "HKCU";
    public static string HiveLong(CoreHive hive) => hive == CoreHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

    public static string CleanKeyPath(string keyPath) => keyPath.Trim().Trim('\\');

    public static bool KeyExists(CoreHive hive, bool view64, string keyPath)
    {
        using var root = OpenBase(hive, view64);
        using var key = root.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    /// <summary>Reads the exact current state of one value. Throws <see cref="UnsupportedRegistryValueException"/> for odd types.</summary>
    public static RegistryPriorState Capture(CoreHive hive, bool view64, string keyPath, string valueName)
    {
        using var root = OpenBase(hive, view64);
        using var key = root.OpenSubKey(keyPath, writable: false);
        if (key is null) return new RegistryPriorState { KeyExisted = false, ValueExisted = false };
        if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            return new RegistryPriorState { KeyExisted = true, ValueExisted = false };

        var win32Kind = key.GetValueKind(valueName);
        var kind = RegistryValueCodec.FromWin32(win32Kind)
            ?? throw new UnsupportedRegistryValueException("This setting has an unusual value type, so PC Tidy leaves it alone.");
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        return new RegistryPriorState { KeyExisted = true, ValueExisted = true, Kind = kind, Data = RegistryValueCodec.Format(kind, value) };
    }

    /// <summary>True if the captured state holds exactly this kind and data.</summary>
    public static bool Matches(RegistryPriorState state, RegistryKind kind, string? data)
        => state.ValueExisted && state.Kind == kind
           && string.Equals(state.Data, RegistryValueCodec.Normalize(kind, data), StringComparison.Ordinal);

    /// <summary>
    /// The top-most key on the path that doesn't exist yet (the one CreateSubKey will create), or null if the key exists.
    /// Undo removes keys PC Tidy created, but only up to this one and only while they are empty.
    /// </summary>
    public static string? FirstMissingAncestor(CoreHive hive, bool view64, string keyPath)
    {
        using var root = OpenBase(hive, view64);
        var parts = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return path;
        }
        return null;
    }

    public static void SetValue(CoreHive hive, bool view64, string keyPath, string valueName, RegistryKind kind, string? data)
    {
        var value = RegistryValueCodec.Parse(kind, data);
        using var root = OpenBase(hive, view64);
        using var key = root.CreateSubKey(keyPath, writable: true)
            ?? throw new UnauthorizedAccessException("Windows wouldn't open this setting for writing.");
        key.SetValue(valueName, value, RegistryValueCodec.ToWin32(kind));
    }

    public static void DeleteValue(CoreHive hive, bool view64, string keyPath, string valueName)
    {
        using var root = OpenBase(hive, view64);
        using var key = root.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Puts a value back to its captured state: the old data, or absent. Then removes keys PC Tidy created, if empty.</summary>
    public static void Restore(CoreHive hive, bool view64, string keyPath, string valueName, RegistryPriorState prior, string? createdKey)
    {
        if (prior.ValueExisted && prior.Kind is { } kind)
            SetValue(hive, view64, keyPath, valueName, kind, prior.Data);
        else
            DeleteValue(hive, view64, keyPath, valueName);

        if (!prior.KeyExisted && createdKey is not null)
            DeleteEmptyKeys(hive, view64, keyPath, createdKey);
    }

    /// <summary>Deletes <paramref name="keyPath"/> and its parents up to <paramref name="stopAt"/> (inclusive), only while each is empty.</summary>
    public static void DeleteEmptyKeys(CoreHive hive, bool view64, string keyPath, string stopAt)
    {
        if (!(keyPath.Equals(stopAt, StringComparison.OrdinalIgnoreCase) || keyPath.StartsWith(stopAt + "\\", StringComparison.OrdinalIgnoreCase)))
            return;
        using var root = OpenBase(hive, view64);
        var current = keyPath;
        while (true)
        {
            using (var key = root.OpenSubKey(current, writable: false))
            {
                if (key is not null && (key.ValueCount > 0 || key.SubKeyCount > 0)) return; // someone else uses it
            }
            root.DeleteSubKey(current, throwOnMissingSubKey: false);
            if (current.Equals(stopAt, StringComparison.OrdinalIgnoreCase)) return;
            var cut = current.LastIndexOf('\\');
            if (cut <= 0) return;
            current = current[..cut];
        }
    }

    public static string Describe(RegistryPriorState state)
        => !state.ValueExisted ? "not set" : Short(state.Data);

    public static string Short(string? data)
    {
        var d = data ?? "";
        return d.Length <= 40 ? (d.Length == 0 ? "(empty)" : d) : d[..37] + "...";
    }
}
