using System.Globalization;
using System.Text;
using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Scripts;

/// <summary>
/// Reads a .reg file (REGEDIT4 or "Windows Registry Editor Version 5.00") as text: key headers, key deletions
/// (<c>[-HKEY…]</c>), named and default values of every type, value deletions (<c>"x"=-</c>) and hex continuation lines.
/// Nothing is imported: the result is a list of the changes regedit WOULD make.
/// </summary>
public static class RegFileParser
{
    public sealed record Result(IReadOnlyList<RegistryWrite> Writes, IReadOnlyList<(int Line, string Text)> Unreadable, bool HasHeader);

    public static Result Parse(IReadOnlyList<string> lines)
    {
        var writes = new List<RegistryWrite>();
        var unreadable = new List<(int, string)>();
        bool header = false;
        string? rawHive = null, key = null;
        RegistryHive? hive = null;

        foreach (var (lineNo, text) in JoinContinuations(lines))
        {
            var t = text.Trim();
            if (t.Length == 0 || t.StartsWith(';')) continue;
            if (t.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase) || t.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase))
            {
                header = true;
                continue;
            }
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                var inner = t[1..^1].Trim();
                bool delete = inner.StartsWith('-');
                if (delete) inner = inner[1..];
                if (!RegistryText.TrySplit(inner, out var rh, out var h, out var k))
                {
                    unreadable.Add((lineNo, t));
                    rawHive = key = null;
                    continue;
                }
                rawHive = rh; hive = h; key = k;
                writes.Add(new RegistryWrite(lineNo, t, h, rh, k, null, delete ? RegistryOp.DeleteKey : RegistryOp.CreateKey));
                if (delete) rawHive = key = null; // values after a deleted key belong to no key
                continue;
            }
            if (key is null || rawHive is null)
            {
                unreadable.Add((lineNo, t));
                continue;
            }
            if (!TryParseValue(t, out var name, out var op, out var kind, out var data))
            {
                unreadable.Add((lineNo, t));
                continue;
            }
            writes.Add(new RegistryWrite(lineNo, t, hive, rawHive, key, name, op, kind, data));
        }
        return new Result(writes, unreadable, header);
    }

    /// <summary>Joins lines ending with a backslash (hex data spread over several lines).</summary>
    private static IEnumerable<(int Line, string Text)> JoinContinuations(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        int start = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (sb.Length == 0) start = i + 1;
            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith('\\') && !trimmed.TrimStart().StartsWith('['))
            {
                sb.Append(trimmed[..^1].Trim());
                continue;
            }
            sb.Append(sb.Length == 0 ? line : line.Trim());
            yield return (start, sb.ToString());
            sb.Clear();
        }
        if (sb.Length > 0) yield return (start, sb.ToString());
    }

    /// <summary>Parses <c>"Name"=…</c> or <c>@=…</c>.</summary>
    private static bool TryParseValue(string t, out string name, out RegistryOp op, out RegistryKind? kind, out string? data)
    {
        name = "";
        op = RegistryOp.SetValue;
        kind = null;
        data = null;
        int pos;
        if (t.StartsWith('@'))
        {
            pos = 1;
        }
        else if (t.StartsWith('"'))
        {
            if (!ReadQuoted(t, 0, out name, out pos)) return false;
        }
        else return false;

        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
        if (pos >= t.Length || t[pos] != '=') return false;
        var value = t[(pos + 1)..].Trim();

        if (value == "-")
        {
            op = RegistryOp.DeleteValue;
            return true;
        }
        if (value.StartsWith('"'))
        {
            if (!ReadQuoted(value, 0, out var s, out _)) return false;
            kind = RegistryKind.String;
            data = s;
            return true;
        }
        if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            kind = RegistryKind.DWord;
            data = uint.TryParse(value[6..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var d)
                ? d.ToString(CultureInfo.InvariantCulture) : null;
            return data is not null;
        }
        if (value.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
        {
            var colon = value.IndexOf(':');
            if (colon < 0) return false;
            var type = value[3..colon].Trim().Trim('(', ')').ToLowerInvariant();
            var bytes = ParseHex(value[(colon + 1)..]);
            if (bytes is null) return false;
            (kind, data) = type switch
            {
                "" => (RegistryKind.Binary, Convert.ToHexString(bytes)),
                "2" => (RegistryKind.ExpandString, Utf16(bytes)),
                "7" => (RegistryKind.MultiString, Utf16(bytes).Replace('\0', '\n').Trim('\n')),
                "b" => (RegistryKind.QWord, bytes.Length >= 8 ? BitConverter.ToUInt64(bytes, 0).ToString(CultureInfo.InvariantCulture) : null),
                "4" => (RegistryKind.DWord, bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0).ToString(CultureInfo.InvariantCulture) : null),
                _ => (RegistryKind.Binary, Convert.ToHexString(bytes)),
            };
            return data is not null;
        }
        return false;
    }

    /// <summary>Reads a "quoted" token with .reg escapes (\\ and \") starting at <paramref name="start"/>.</summary>
    private static bool ReadQuoted(string t, int start, out string text, out int end)
    {
        var sb = new StringBuilder();
        for (int i = start + 1; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && (t[i + 1] == '\\' || t[i + 1] == '"'))
            {
                sb.Append(t[i + 1]);
                i++;
                continue;
            }
            if (c == '"')
            {
                text = sb.ToString();
                end = i + 1;
                return true;
            }
            sb.Append(c);
        }
        text = sb.ToString();
        end = t.Length;
        return false;
    }

    private static byte[]? ParseHex(string s)
    {
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
        return bytes;
    }

    private static string Utf16(byte[] bytes) => Encoding.Unicode.GetString(bytes, 0, bytes.Length - bytes.Length % 2).TrimEnd('\0');
}
