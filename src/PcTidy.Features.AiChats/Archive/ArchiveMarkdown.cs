using PcTidy.Core;
using PcTidy.Features.AiChats.Export;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;

namespace PcTidy.Features.AiChats.Archive;

/// <summary>Readable Markdown copies of agent chats for the archive folder (masked like everything else). Read-only.</summary>
public static class ArchiveMarkdown
{
    public static async Task<IReadOnlyDictionary<string, string>> BuildAsync(IKnownLocations k, IReadOnlyList<AgentSession> sessions, CancellationToken ct, SecretMasker? masker = null)
    {
        masker ??= SecretMasker.Default;
        var titles = sessions.Any(s => s.Source == ChatSource.Codex) ? await AgentChatInventory.CodexTitlesAsync(k, ct).ConfigureAwait(false) : null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var report = new ImportReport();
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (await AgentTranscriptImporter.ReadAsync(s, titles, report, ct).ConfigureAwait(false) is not { } chat) continue;
            var masked = ChatIngest.Prepare(chat, masker);
            result[s.LibraryId] = MarkdownNote.Render(ToDetail(masked), new NoteLinks(Array.Empty<string>(), Array.Empty<AttachmentLink>(), SecretsKept: false));
        }
        return result;
    }

    internal static ChatDetail ToDetail(MaskedChat c) => new(
        new ChatSummary
        {
            Id = c.Id, Source = c.Source, Title = c.Title, CreatedUtc = c.CreatedUtc, UpdatedUtc = c.UpdatedUtc, Model = c.Model, Project = c.Project,
            MessageCount = c.Messages.Count, CharCount = c.CharCount, SizeBytes = c.SizeBytes, SecretCount = c.Secrets.Count,
            AlternateCount = c.Alternates.Count, AttachmentCount = c.Attachments.Count, OriginKind = c.OriginKind, Origin = c.Origin, ContentHash = c.ContentHash,
        },
        c.Messages, c.Alternates, c.Attachments, c.Terms);
}
