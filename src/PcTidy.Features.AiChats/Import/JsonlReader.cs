using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Streams the records of a JSON-lines transcript (plain .jsonl, or zstd-compressed .jsonl.zst) one line at a time.
/// Opened read-only and shared, so an agent still writing its file is never disturbed. Lines that aren't valid JSON
/// (a half-written last line, a format change) are counted and skipped.
/// </summary>
internal static class JsonlReader
{
    public static bool IsCompressed(string path) => path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);

    public static Stream OpenRead(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (!IsCompressed(path)) return file;
        try { return new DecompressionStream(file, leaveOpen: false); }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Each line's root element (a clone: safe to keep). <paramref name="badLines"/> counts the skipped ones.</summary>
    public static async IAsyncEnumerable<JsonElement> ReadAsync(string path, Counter badLines, [EnumeratorCancellation] CancellationToken ct, int maxLines = int.MaxValue)
    {
        await using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        int lines = 0;
        while (lines < maxLines)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ZstdException or InvalidDataException or IOException)
            {
                badLines.Value++; // damaged compressed data: keep what was read so far
                yield break;
            }
            if (line is null) yield break;
            lines++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement? element = null;
            try
            {
                using var doc = JsonDocument.Parse(line, JsonUtil.Lenient);
                element = doc.RootElement.Clone();
            }
            catch (JsonException) { badLines.Value++; }
            if (element is { ValueKind: JsonValueKind.Object } e) yield return e;
        }
    }

    internal sealed class Counter { public int Value; }
}
