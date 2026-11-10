using System.Globalization;
using System.Security;
using Microsoft.Win32;
using CoreHive = PcTidy.Core.Model.RegistryHive;
using Win32Hive = Microsoft.Win32.RegistryHive;

namespace PcTidy.Features.Tweaks.Reading;

/// <summary>
/// Where registry reads (and the keys in the PlanItems built from them) really point.
/// The real PC uses <see cref="None"/>. Tests and the simulation sandbox redirect every key into
/// <c>HKCU\Software\PCTidyTest\…</c>, laid out as <c>&lt;root&gt;\HKLM\&lt;key&gt;</c> and <c>&lt;root&gt;\HKCU\&lt;key&gt;</c>,
/// so the whole read → plan → apply → undo path can run without touching a real setting.
/// </summary>
public sealed class RegistryRedirect
{
    public const string TestArea = @"Software\PCTidyTest";

    private RegistryRedirect(string? root) => Root = root;

    public static readonly RegistryRedirect None = new(null);

    /// <summary>Redirects into <paramref name="testRoot"/>, which must be under HKCU\Software\PCTidyTest.</summary>
    public static RegistryRedirect ToTestRoot(string testRoot)
    {
        var root = testRoot.Trim().Trim('\\');
        if (!root.StartsWith(TestArea + "\\", StringComparison.OrdinalIgnoreCase) || root.Split('\\').Any(p => p is "." or ".."))
            throw new ArgumentException(@"Redirected registry roots must be below HKCU\Software\PCTidyTest.", nameof(testRoot));
        return new RegistryRedirect(root);
    }

    /// <summary>The test root, or null on the real PC.</summary>
    public string? Root { get; }
    public bool IsRedirected => Root is not null;

    /// <summary>Logical hive + key (what the catalog says) → physical hive + key (what is really read or written).</summary>
    public (CoreHive Hive, string KeyPath) Map(CoreHive hive, string keyPath)
        => Root is null ? (hive, keyPath.Trim('\\')) : (CoreHive.CurrentUser, $@"{Root}\{RegistryNames.Short(hive)}\{keyPath.Trim('\\')}");
}

/// <summary>One value as read: <see cref="Kind"/> null = a type PC Tidy doesn't handle (left alone).</summary>
public sealed record RegValue(RegistryKind? Kind, string Data);

/// <summary>Read-only registry access for the tweak scanners (logical keys; the redirect is applied inside).</summary>
public interface IRegistryReader
{
    RegistryRedirect Redirect { get; }
    bool KeyExists(CoreHive hive, string keyPath);
    /// <summary>Null when the key or the value doesn't exist (or can't be read).</summary>
    RegValue? Read(CoreHive hive, string keyPath, string valueName);
    IReadOnlyList<string> ValueNames(CoreHive hive, string keyPath);
    IReadOnlyList<string> SubKeyNames(CoreHive hive, string keyPath);
}

/// <summary>
/// <see cref="IRegistryReader"/> over <see cref="RegistryKey"/> read APIs only (64-bit view). Unreadable keys
/// (access denied, vanished mid-scan) read as missing. Data uses the invariant format of
/// <see cref="RegistryValueSpec.Data"/>: DWord/QWord unsigned decimal, Binary hex, MultiString lines joined with \n.
/// </summary>
public sealed class RegistryReader : IRegistryReader
{
    public RegistryReader(RegistryRedirect redirect) => Redirect = redirect;

    public RegistryRedirect Redirect { get; }

    public bool KeyExists(CoreHive hive, string keyPath)
    {
        using var key = Open(hive, keyPath);
        return key is not null;
    }

    public RegValue? Read(CoreHive hive, string keyPath, string valueName)
    {
        try
        {
            using var key = Open(hive, keyPath);
            if (key is null) return null;
            if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) return null;
            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            return kind switch
            {
                RegistryValueKind.String => new RegValue(RegistryKind.String, raw as string ?? ""),
                RegistryValueKind.ExpandString => new RegValue(RegistryKind.ExpandString, raw as string ?? ""),
                RegistryValueKind.DWord => new RegValue(RegistryKind.DWord, unchecked((uint)Convert.ToInt32(raw, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.QWord => new RegValue(RegistryKind.QWord, unchecked((ulong)Convert.ToInt64(raw, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.Binary => new RegValue(RegistryKind.Binary, Convert.ToHexString(raw as byte[] ?? Array.Empty<byte>())),
                RegistryValueKind.MultiString => new RegValue(RegistryKind.MultiString, string.Join("\n", raw as string[] ?? Array.Empty<string>())),
                _ => new RegValue(null, ""),
            };
        }
        catch (Exception ex) when (IsReadProblem(ex))
        {
            return null;
        }
    }

    public IReadOnlyList<string> ValueNames(CoreHive hive, string keyPath)
    {
        try
        {
            using var key = Open(hive, keyPath);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> SubKeyNames(CoreHive hive, string keyPath)
    {
        try
        {
            using var key = Open(hive, keyPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (IsReadProblem(ex)) { return Array.Empty<string>(); }
    }

    private RegistryKey? Open(CoreHive hive, string keyPath)
    {
        var (h, path) = Redirect.Map(hive, keyPath);
        try
        {
            using var root = RegistryKey.OpenBaseKey(h == CoreHive.LocalMachine ? Win32Hive.LocalMachine : Win32Hive.CurrentUser, RegistryView.Registry64);
            return root.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (IsReadProblem(ex))
        {
            return null;
        }
    }

    private static bool IsReadProblem(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or ObjectDisposedException;
}

/// <summary>Registry data helpers shared by the scanners (same normalisation the platform handler uses).</summary>
public static class RegData
{
    /// <summary>True when two data strings of <paramref name="kind"/> mean the same value ("0x10" = "16"; strings exact).</summary>
    public static bool SameValue(RegistryKind kind, string a, string b)
    {
        if (kind is RegistryKind.DWord or RegistryKind.QWord)
            return TryNumber(a, out var x) && TryNumber(b, out var y) && x == y;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public static bool TryNumber(string s, out ulong value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        if (s.StartsWith('-') && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var neg))
        {
            value = unchecked((uint)(int)neg);
            return true;
        }
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Short display form: numbers also in hex when large, strings quoted, long text cut.</summary>
    public static string Show(RegistryKind? kind, string data)
    {
        if (kind is RegistryKind.DWord or RegistryKind.QWord && TryNumber(data, out var n))
            return n > 9 ? $"{n} (0x{n:X})" : n.ToString(CultureInfo.InvariantCulture);
        var d = data.Replace("\n", " | ");
        if (d.Length == 0) return "(empty)";
        return d.Length <= 60 ? $"\"{d}\"" : $"\"{d[..57]}…\"";
    }

    public static string KindName(RegistryKind? kind) => kind switch
    {
        RegistryKind.DWord => "number (DWORD)",
        RegistryKind.QWord => "number (QWORD)",
        RegistryKind.String => "text",
        RegistryKind.ExpandString => "text with variables",
        RegistryKind.MultiString => "list of text",
        RegistryKind.Binary => "binary data",
        _ => "unusual type",
    };
}
