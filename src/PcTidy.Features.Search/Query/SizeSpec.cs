using System.Globalization;

namespace PcTidy.Features.Search.Query;

/// <summary>
/// Parses <c>size:</c> values, culture-invariant: <c>&gt;1gb</c>, <c>&gt;=500mb</c>, <c>&lt;10kb</c>, <c>=0</c>, <c>1.5gb</c>,
/// <c>1mb..10mb</c>, and Everything's names: empty, tiny, small, medium, large, huge, gigantic. Units are binary (1 KB = 1024 B).
/// </summary>
public static class SizeSpec
{
    private const long KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;

    private static readonly Dictionary<string, (long Min, long Max)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["empty"] = (0, 0),
        ["tiny"] = (0, 10 * KB),
        ["small"] = (10 * KB + 1, 100 * KB),
        ["medium"] = (100 * KB + 1, MB),
        ["large"] = (MB + 1, 16 * MB),
        ["huge"] = (16 * MB + 1, 128 * MB),
        ["gigantic"] = (128 * MB + 1, long.MaxValue),
    };

    public static bool TryParse(string value, out long min, out long max)
    {
        min = 0; max = long.MaxValue;
        var v = value.Trim();
        if (v.Length == 0) return false;
        if (Named.TryGetValue(v, out var named)) { (min, max) = named; return true; }

        var range = v.IndexOf("..", StringComparison.Ordinal);
        if (range > 0)
        {
            if (!TryBytes(v[..range], out var a) || !TryBytes(v[(range + 2)..], out var b)) return false;
            (min, max) = a <= b ? (a, b) : (b, a);
            return true;
        }

        string op = v.StartsWith(">=") || v.StartsWith("<=") ? v[..2] : v[0] is '>' or '<' or '=' ? v[..1] : "";
        if (!TryBytes(v[op.Length..], out var n)) return false;
        switch (op)
        {
            case ">": min = n == long.MaxValue ? n : n + 1; break;
            case ">=": min = n; break;
            case "<":
                if (n == 0) (min, max) = (1, 0); // nothing is smaller than 0 bytes: an empty range
                else max = n - 1;
                break;
            case "<=": max = n; break;
            default: min = max = n; break;
        }
        return true;
    }

    /// <summary>"1.5gb" → bytes. Decimal point is always '.', whatever the PC's region.</summary>
    public static bool TryBytes(string text, out long bytes)
    {
        bytes = 0;
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        int split = 0;
        while (split < t.Length && (char.IsAsciiDigit(t[split]) || t[split] == '.')) split++;
        if (split == 0) return false;
        if (!double.TryParse(t[..split], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return false;
        long unit = t[split..].Trim() switch
        {
            "" or "b" or "bytes" => 1,
            "k" or "kb" or "kib" => KB,
            "m" or "mb" or "mib" => MB,
            "g" or "gb" or "gib" => GB,
            "t" or "tb" or "tib" => TB,
            _ => -1,
        };
        if (unit < 0) return false;
        var result = number * unit;
        if (double.IsNaN(result) || result < 0) return false;
        bytes = result >= long.MaxValue ? long.MaxValue : (long)Math.Round(result);
        return true;
    }
}
