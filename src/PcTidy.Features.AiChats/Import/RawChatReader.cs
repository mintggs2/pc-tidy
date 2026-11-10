using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Reads chats again from where they were imported (the export zip/folder or the transcript), unmasked. Used only when
/// the user switched on "keep keys and passwords in exports"; the result goes straight into the exported notes and is
/// never stored by PC Tidy. Read-only, like every importer.
/// </summary>
public static class RawChatReader
{
    /// <summary>Chats found, by library id. Chats whose original is gone are simply missing from the result.</summary>
    public static async Task<IReadOnlyDictionary<string, ImportedChat>> ReadAsync(
        IEnumerable<(string Id, OriginKind Kind, string Origin, ChatSource Source)> wanted, CancellationToken ct)
    {
        var found = new Dictionary<string, ImportedChat>(StringComparer.Ordinal);
        foreach (var group in wanted.GroupBy(w => (w.Kind, w.Origin)))
        {
            var ids = group.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            var report = new ImportReport();
            try
            {
                if (group.Key.Kind == OriginKind.TranscriptFile)
                {
                    if (!File.Exists(group.Key.Origin)) continue;
                    var first = group.First();
                    var info = new FileInfo(group.Key.Origin);
                    var session = new AgentSession
                    {
                        Source = first.Source,
                        TranscriptPath = group.Key.Origin,
                        SessionId = first.Source == ChatSource.Codex ? AgentChatInventory.CodexIdFromName(group.Key.Origin) : Path.GetFileNameWithoutExtension(group.Key.Origin),
                        LastWriteUtc = info.LastWriteTimeUtc,
                        CreatedUtc = info.CreationTimeUtc,
                    };
                    if (await AgentTranscriptImporter.ReadAsync(session, null, report, ct).ConfigureAwait(false) is { } chat)
                        found[first.Id] = chat;
                    continue;
                }
                if (!File.Exists(group.Key.Origin) && !Directory.Exists(group.Key.Origin)) continue;
                await foreach (var chat in ExportImporter.ReadAsync(group.Key.Origin, report, ct).ConfigureAwait(false))
                {
                    if (!ids.Remove(chat.LibraryId)) continue;
                    found[chat.LibraryId] = chat;
                    if (ids.Count == 0) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* stays masked */ }
        }
        return found;
    }
}
