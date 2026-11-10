using System.Globalization;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Scripts;

public enum RegistryOp { SetValue, DeleteValue, DeleteKey, CreateKey }

/// <summary>
/// One registry change read from a script's text (a .reg line, <c>reg add</c>, <c>Set-ItemProperty</c>…).
/// <see cref="Hive"/> is null for hives PC Tidy has no verdicts for (HKCR, HKU, HKCC); <see cref="RawHive"/> keeps the text.
/// </summary>
public sealed record RegistryWrite(
    int Line,
    string Evidence,
    RegistryHive? Hive,
    string RawHive,
    string KeyPath,
    string? ValueName,
    RegistryOp Op,
    RegistryKind? Kind = null,
    string? Data = null)
{
    public string FullKey => $@"{RawHive}\{KeyPath}";
    public string Label => ValueName is null ? FullKey : $@"{FullKey} → {(ValueName.Length == 0 ? "(Default)" : ValueName)}";
}

/// <summary>Registry path and data helpers shared by the .reg parser and the command scanner.</summary>
public static class RegistryText
{
    /// <summary>
    /// Splits "HKEY_LOCAL_MACHINE\SOFTWARE\X", "HKLM:\SOFTWARE\X", "Registry::HKLM\X", "HKCU\X" into a short hive name
    /// and a normalised key path (ControlSet001 → CurrentControlSet, no leading or trailing backslash).
    /// </summary>
    public static bool TrySplit(string text, out string rawHive, out RegistryHive? hive, out string keyPath)
    {
        rawHive = "";
        hive = null;
        keyPath = "";
        var t = text.Trim().Trim('"', '\'').Trim();
        if (t.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase)) t = t["Registry::".Length..];
        if (t.StartsWith("Microsoft.PowerShell.Core\\Registry::", StringComparison.OrdinalIgnoreCase)) t = t["Microsoft.PowerShell.Core\\Registry::".Length..];
        var cut = t.IndexOfAny(new[] { '\\', '/' });
        var head = (cut < 0 ? t : t[..cut]).TrimEnd(':').ToUpperInvariant();
        var rest = cut < 0 ? "" : t[(cut + 1)..];
        (rawHive, hive) = head switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => ("HKLM", RegistryHive.LocalMachine),
            "HKCU" or "HKEY_CURRENT_USER" => ("HKCU", RegistryHive.CurrentUser),
            "HKCR" or "HKEY_CLASSES_ROOT" => ("HKCR", (RegistryHive?)null),
            "HKU" or "HKEY_USERS" => ("HKU", (RegistryHive?)null),
            "HKCC" or "HKEY_CURRENT_CONFIG" => ("HKCC", (RegistryHive?)null),
            _ => ("", (RegistryHive?)null),
        };
        if (rawHive.Length == 0) return false;
        keyPath = NormalizeKey(rest);
        return true;
    }

    public static string NormalizeKey(string key)
    {
        var k = key.Replace('/', '\\').Trim().Trim('\\');
        while (k.Contains(@"\\", StringComparison.Ordinal)) k = k.Replace(@"\\", @"\", StringComparison.Ordinal);
        // ControlSet001/002 are the same keys Windows maps to CurrentControlSet.
        var parts = k.Split('\\');
        if (parts.Length > 1 && parts[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            && parts[1].StartsWith("ControlSet00", StringComparison.OrdinalIgnoreCase))
            parts[1] = "CurrentControlSet";
        return string.Join('\\', parts);
    }

    /// <summary>"0x26", "38", "00000026" (with <paramref name="hexDefault"/>) → "38". Null when not a number.</summary>
    public static string? NormalizeNumber(string? text, bool hexDefault = false)
    {
        if (text is null) return null;
        var t = text.Trim().Trim('"', '\'');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? Fit(h) : null;
        if (hexDefault)
            return ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx) ? Fit(hx) : null;
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            return d < 0 ? unchecked((uint)(int)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static string Fit(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The data in the catalog's invariant format (numbers as unsigned decimal), so it can be compared with tweak values.</summary>
    public static string? Comparable(string? data) => NormalizeNumber(data) ?? data?.Trim().Trim('"', '\'');
}
