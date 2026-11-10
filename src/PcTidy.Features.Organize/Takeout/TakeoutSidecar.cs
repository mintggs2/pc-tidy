using System.Globalization;
using System.Text.Json;

namespace PcTidy.Features.Organize.Takeout;

/// <summary>
/// One Google Photos "sidecar": the <c>photo.jpg.json</c> (or <c>photo.jpg.supplemental-metadata.json</c>) file
/// Google Takeout writes next to every photo, holding the original name and when the photo was taken.
/// Times are Unix seconds (UTC) stored as strings; they're parsed culture-invariant, so the result is the same
/// in every language and region.
/// </summary>
public sealed record TakeoutSidecar(string Path, string? Title, DateTime? PhotoTakenUtc, DateTime? CreationUtc)
{
    /// <summary>Sidecars bigger than this aren't Google's photo info files.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Reads and parses a sidecar (read-only). Null when it isn't a photo sidecar or can't be read.</summary>
    public static TakeoutSidecar? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxBytes) return null;
            using var reader = new StreamReader(fs);
            return Parse(path, reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses sidecar JSON. Anything that isn't an object with a title or a photo time gives null.</summary>
    public static TakeoutSidecar? Parse(string path, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var taken = Timestamp(root, "photoTakenTime");
            var created = Timestamp(root, "creationTime");
            // An album's metadata.json has a title but no photo times; photo sidecars always carry at least one.
            if (taken is null && created is null) return null;
            return new TakeoutSidecar(path, title, taken, created);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>"photoTakenTime": { "timestamp": "1569000000", … }</c> → UTC time; 0 or missing → null.</summary>
    private static DateTime? Timestamp(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty("timestamp", out var ts)) return null;
        long seconds;
        if (ts.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(ts.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return null;
        }
        else if (ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out seconds)) return null;
        // 0 means "unknown"; anything outside 1900–2200 is damaged data.
        if (seconds <= 0 || seconds > 7_258_118_400) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }
}
