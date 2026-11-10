using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Reads a ChatGPT or Claude.ai data export (the zip as downloaded, or the folder it was extracted to), one chat at a
/// time: conversations are streamed straight out of the zip, so a 500 MB export never sits in memory whole.
/// Handles ChatGPT's single conversations.json and its newer split files (conversations-000.json, … plus a manifest),
/// and Claude's conversations.json + projects.json. Read-only: the export is never changed.
/// </summary>
public static partial class ExportImporter
{
    /// <summary>A conversations file that isn't a plain JSON list is parsed whole only below this size.</summary>
    private const long MaxWholeFileBytes = 256L * 1024 * 1024;

    [GeneratedRegex(@"^conversations[-_]\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitFilePattern();

    /// <summary>True when the zip/folder contains a conversations file this importer understands.</summary>
    public static bool LooksLikeExport(string path)
    {
        try
        {
            using var archive = ExportArchive.Open(path);
            return ConversationFiles(archive).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    public static async IAsyncEnumerable<ImportedChat> ReadAsync(string path, ImportReport report, [EnumeratorCancellation] CancellationToken ct)
    {
        using var archive = ExportArchive.Open(path);
        var files = ConversationFiles(archive);
        if (files.Count == 0)
        {
            report.Note("No conversations file was found in it. Pick the zip from ChatGPT's or Claude's \"Export data\" email.");
            yield break;
        }
        var projects = await ReadProjectsAsync(archive, report, ct).ConfigureAwait(false);
        var attachments = new AttachmentIndex(archive);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            await foreach (var conv in Conversations(archive, file, report, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                ImportedChat? chat;
                try
                {
                    chat = conv.Prop("mapping") is not null ? ChatGptParser.Parse(conv, archive, attachments, report)
                        : conv.Prop("chat_messages") is not null ? ClaudeWebParser.Parse(conv, archive, projects)
                        : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
                {
                    chat = null;
                }
                if (chat is null) { report.SkippedChats++; continue; }
                if (!seen.Add(chat.LibraryId)) continue; // the same chat in two files of one export
                report.Chats++;
                yield return chat;
            }
        }
        if (report.SkippedChats > 0) report.Note($"{report.SkippedChats} entr{(report.SkippedChats == 1 ? "y" : "ies")} in the export weren't chats PC Tidy could read, so they were skipped.");
    }

    /// <summary>The newer split files (in manifest order when there is one), else the single conversations.json.</summary>
    internal static IReadOnlyList<string> ConversationFiles(ExportArchive archive)
    {
        var split = archive.ByFileName(n => SplitFilePattern().IsMatch(n));
        if (split.Count == 0)
            return archive.FirstNamed("conversations.json") is { } single ? new[] { single } : Array.Empty<string>();
        var order = ManifestOrder(archive);
        return split.OrderBy(e => order.TryGetValue(ExportArchive.FileName(e), out var i) ? i : int.MaxValue)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>File names in the order a manifest lists them (any small *manifest*.json; its exact shape is unknown).</summary>
    private static Dictionary<string, int> ManifestOrder(ExportArchive archive)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manifest = archive.ByFileName(n => n.Contains("manifest", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (manifest is null || archive.EntrySize(manifest) > 16 * 1024 * 1024) return order;
        try
        {
            using var s = archive.OpenEntry(manifest);
            using var doc = JsonDocument.Parse(s, JsonUtil.Lenient);
            void Visit(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String when e.GetString() is { } v && v.EndsWith(".json", StringComparison.OrdinalIgnoreCase):
                        order.TryAdd(ExportArchive.FileName(v.Replace('\\', '/')), order.Count);
                        break;
                    case JsonValueKind.Array:
                        foreach (var x in e.EnumerateArray()) Visit(x);
                        break;
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject()) Visit(p.Value);
                        break;
                }
            }
            Visit(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { /* name order instead */ }
        return order;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadProjectsAsync(ExportArchive archive, ImportReport report, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (archive.FirstNamed("projects.json") is not { } entry) return names;
        await foreach (var p in Conversations(archive, entry, report, ct).ConfigureAwait(false))
            if (p.Str("uuid") is { } id && p.Str("name") is { Length: > 0 } name) names[id] = name;
        return names;
    }

    /// <summary>
    /// Streams the elements of a top-level JSON list. A file wrapped in an object ({"conversations": […]}) is read whole
    /// when it's small enough; a damaged file stops at the damage with a note (chats before it are kept).
    /// </summary>
    private static async IAsyncEnumerable<JsonElement> Conversations(ExportArchive archive, string entry, ImportReport report, [EnumeratorCancellation] CancellationToken ct)
    {
        var first = FirstToken(archive, entry);
        if (first == '[')
        {
            await using var stream = archive.OpenEntry(entry);
            var items = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, JsonUtil.StreamOptions, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    JsonElement current;
                    try
                    {
                        if (!await items.MoveNextAsync().ConfigureAwait(false)) break;
                        current = items.Current;
                    }
                    catch (JsonException)
                    {
                        report.Note($"{ExportArchive.FileName(entry)} is damaged part-way through; the chats before the damage were imported.");
                        break;
                    }
                    if (current.ValueKind == JsonValueKind.Object) yield return current;
                }
            }
            finally { await items.DisposeAsync().ConfigureAwait(false); }
            yield break;
        }
        if (first != '{' || archive.EntrySize(entry) > MaxWholeFileBytes)
        {
            if (first is not null) report.Note($"{ExportArchive.FileName(entry)} isn't in a format PC Tidy knows, so it was skipped.");
            yield break;
        }
        JsonDocument? doc = null;
        try
        {
            await using var stream = archive.OpenEntry(entry);
            doc = await JsonDocument.ParseAsync(stream, JsonUtil.Lenient, ct).ConfigureAwait(false);
        }
        catch (JsonException) { report.Note($"{ExportArchive.FileName(entry)} couldn't be read (it seems damaged)."); }
        if (doc is null) yield break;
        using (doc)
        {
            var list = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array).Value;
            if (list.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e.Clone();
        }
    }

    /// <summary>The first non-whitespace character of an entry ('[' / '{'), skipping a UTF-8 byte-order mark.</summary>
    private static char? FirstToken(ExportArchive archive, string entry)
    {
        try
        {
            using var s = archive.OpenEntry(entry);
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b is 0xEF or 0xBB or 0xBF or ' ' or '\t' or '\r' or '\n') continue;
                return (char)b;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        return null;
    }
}
