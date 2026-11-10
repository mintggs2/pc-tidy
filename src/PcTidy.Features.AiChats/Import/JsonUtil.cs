using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Lenient readers for undocumented, frequently changing JSON (chat exports, agent transcripts): a missing or
/// oddly-typed field is null, never an exception.
/// </summary>
internal static class JsonUtil
{
    public static JsonElement? Prop(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static JsonElement? Path(this JsonElement e, params string[] names)
    {
        JsonElement? cur = e;
        foreach (var n in names)
        {
            cur = cur?.Prop(n);
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? Str(this JsonElement e, string name) => e.Prop(name) is { } v ? AsString(v) : null;

    public static string? AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static bool Bool(this JsonElement e, string name) => e.Prop(name) is { ValueKind: JsonValueKind.True };

    public static long? Long(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public static IEnumerable<JsonElement> Array(this JsonElement e, string name)
        => e.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>A time given as unix seconds (ChatGPT, float), unix milliseconds, or an ISO 8601 string.</summary>
    public static DateTime? Time(this JsonElement e, string name) => e.Prop(name) is { } v ? ParseTime(v) : null;

    public static DateTime? ParseTime(JsonElement v)
    {
        try
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return FromUnix(d);
            if (v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return FromUnix(n);
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Seconds or milliseconds since 1970 (values past year 5000 in seconds are taken as milliseconds).</summary>
    public static DateTime? FromUnix(double value)
    {
        if (double.IsNaN(value) || value <= 0) return null;
        var seconds = value > 95_617_584_000 ? value / 1000 : value;
        if (seconds > 253_402_300_799) return null;
        return DateTime.UnixEpoch.AddSeconds(seconds);
    }

    /// <summary>Short raw JSON for content PC Tidy doesn't know how to show (shown as data, never interpreted).</summary>
    public static string Raw(JsonElement e, int max = 2000)
    {
        var raw = e.GetRawText();
        return raw.Length <= max ? raw : raw[..max] + " …";
    }

    public static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max:N0} more characters not shown)";

    /// <summary>Joins non-empty pieces with blank lines.</summary>
    public static string JoinBlocks(IEnumerable<string?> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(p.Trim('\n', '\r'));
        }
        return sb.ToString();
    }

    public static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    public static readonly JsonSerializerOptions StreamOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
        DefaultBufferSize = 64 * 1024,
    };
}
