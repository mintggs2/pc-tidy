using System.Globalization;
using Microsoft.Win32;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// Converts between the invariant strings in <see cref="RegistryValueSpec.Data"/> / <see cref="RegistryPriorState.Data"/>
/// and real registry values. Format: DWord/QWord = unsigned decimal (parse also accepts 0x hex and negative decimal);
/// Binary = hex bytes (spaces, commas and dashes ignored); MultiString = lines joined with \n.
/// </summary>
public static class RegistryValueCodec
{
    public static RegistryValueKind ToWin32(RegistryKind kind) => kind switch
    {
        RegistryKind.String => RegistryValueKind.String,
        RegistryKind.ExpandString => RegistryValueKind.ExpandString,
        RegistryKind.DWord => RegistryValueKind.DWord,
        RegistryKind.QWord => RegistryValueKind.QWord,
        RegistryKind.Binary => RegistryValueKind.Binary,
        RegistryKind.MultiString => RegistryValueKind.MultiString,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Null for kinds PC Tidy can't record exactly (REG_NONE, resource lists...).</summary>
    public static RegistryKind? FromWin32(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => RegistryKind.String,
        RegistryValueKind.ExpandString => RegistryKind.ExpandString,
        RegistryValueKind.DWord => RegistryKind.DWord,
        RegistryValueKind.QWord => RegistryKind.QWord,
        RegistryValueKind.Binary => RegistryKind.Binary,
        RegistryValueKind.MultiString => RegistryKind.MultiString,
        _ => null,
    };

    /// <summary>Invariant string → object for RegistryKey.SetValue. Throws FormatException on bad data.</summary>
    public static object Parse(RegistryKind kind, string? data)
    {
        data ??= "";
        switch (kind)
        {
            case RegistryKind.String:
            case RegistryKind.ExpandString:
                return data;
            case RegistryKind.DWord:
                return unchecked((int)ParseUnsigned(data, uint.MaxValue, int.MinValue));
            case RegistryKind.QWord:
                return unchecked((long)ParseUnsigned(data, ulong.MaxValue, long.MinValue));
            case RegistryKind.Binary:
                return ParseHex(data);
            case RegistryKind.MultiString:
                return data.Length == 0 ? Array.Empty<string>() : data.Replace("\r", "").Split('\n');
            default:
                throw new FormatException("Unknown registry kind.");
        }
    }

    /// <summary>Registry value object → invariant string (exactly what <see cref="Parse"/> reads back).</summary>
    public static string Format(RegistryKind kind, object value) => kind switch
    {
        RegistryKind.String or RegistryKind.ExpandString => value as string ?? "",
        RegistryKind.DWord => unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        RegistryKind.QWord => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        RegistryKind.Binary => Convert.ToHexString(value as byte[] ?? Array.Empty<byte>()),
        RegistryKind.MultiString => string.Join("\n", value as string[] ?? Array.Empty<string>()),
        _ => throw new FormatException("Unknown registry kind."),
    };

    /// <summary>Canonical form of a data string, so "0x10" and "16" compare equal.</summary>
    public static string Normalize(RegistryKind kind, string? data) => Format(kind, Parse(kind, data));

    private static ulong ParseUnsigned(string s, ulong max, long minSigned)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex <= max) return hex;
        }
        else if (s.StartsWith('-'))
        {
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var neg) && neg >= minSigned)
                return max == uint.MaxValue ? unchecked((uint)(int)neg) : unchecked((ulong)neg);
        }
        else if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) && dec <= max)
        {
            return dec;
        }
        throw new FormatException($"'{s}' isn't a valid number for this setting.");
    }

    private static byte[] ParseHex(string s)
    {
        var compact = new string(s.Where(c => !char.IsWhiteSpace(c) && c is not ',' and not '-').ToArray());
        if (compact.Length % 2 != 0) throw new FormatException("Binary data must be whole bytes.");
        try
        {
            return Convert.FromHexString(compact);
        }
        catch (FormatException)
        {
            throw new FormatException("Binary data must be hex bytes.");
        }
    }
}
