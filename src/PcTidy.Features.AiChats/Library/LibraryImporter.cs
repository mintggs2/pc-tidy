using PcTidy.Core;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;

namespace PcTidy.Features.AiChats.Library;

public sealed record ImportSummary(int Chats, int Added, int WithSecrets, ImportReport Report)
{
    /// <summary>"Imported 214 chats (12 new). 3 had keys or passwords: they're masked."</summary>
    public string Message
    {
        get
        {
            if (Chats == 0) return Report.Notes.FirstOrDefault() ?? "No chats were found.";
            var text = $"Imported {Chats:N0} chat{(Chats == 1 ? "" : "s")} ({Added:N0} new).";
            if (WithSecrets > 0) text += $" {WithSecrets:N0} contained keys or passwords: they're masked, and listed under Exposed secrets.";
            if (Report.SkippedRecords > 0) text += $" {Report.SkippedRecords:N0} damaged line{(Report.SkippedRecords == 1 ? " was" : "s were")} skipped.";
            return text;
        }
    }
}

/// <summary>
/// Brings chats into the library: export zips/folders (ChatGPT, Claude) and local agent transcripts (Claude Code,
/// Codex). Streams chats in small batches, masks each one (<see cref="ChatIngest"/>) and hands it to the store.
/// Sources are only read.
/// </summary>
public sealed class LibraryImporter
{
    private const int BatchSize = 40;
    private readonly ChatLibraryStore _store;
    private readonly SecretMasker _masker;

    public LibraryImporter(ChatLibraryStore store, SecretMasker? masker = null)
    {
        _store = store;
        _masker = masker ?? SecretMasker.Default;
    }

    public async Task<ImportSummary> ImportExportAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        var report = new ImportReport();
        var batch = new List<MaskedChat>();
        int chats = 0, added = 0, withSecrets = 0;
        await foreach (var chat in ExportImporter.ReadAsync(path, report, ct).ConfigureAwait(false))
        {
            var masked = ChatIngest.Prepare(chat, _masker);
            if (masked.Secrets.Count > 0) withSecrets++;
            batch.Add(masked);
            chats++;
            if (batch.Count >= BatchSize)
            {
                added += await _store.UpsertAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
                progress?.Report($"Imported {chats:N0} chats…");
            }
        }
        added += await _store.UpsertAsync(batch, ct).ConfigureAwait(false);
        if (chats > 0) await _store.RelabelTopicsAsync(ct).ConfigureAwait(false);
        return new ImportSummary(chats, added, withSecrets, report);
    }

    /// <summary>Indexes local agent chats (all of them, or the given sessions). Transcripts are opened read-only and shared.</summary>
    public async Task<ImportSummary> ImportAgentChatsAsync(IKnownLocations k, IReadOnlyList<AgentSession>? sessions, IProgress<string>? progress, CancellationToken ct)
    {
        var report = new ImportReport();
        sessions ??= await AgentChatInventory.ListAsync(k, ct).ConfigureAwait(false);
        var titles = sessions.Any(s => s.Source == ChatSource.Codex) ? await AgentChatInventory.CodexTitlesAsync(k, ct).ConfigureAwait(false) : null;
        var batch = new List<MaskedChat>();
        int chats = 0, added = 0, withSecrets = 0;
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var chat = await AgentTranscriptImporter.ReadAsync(s, titles, report, ct).ConfigureAwait(false);
            if (chat is null) continue;
            var masked = ChatIngest.Prepare(chat, _masker);
            if (masked.Secrets.Count > 0) withSecrets++;
            batch.Add(masked);
            chats++;
            if (batch.Count >= BatchSize)
            {
                added += await _store.UpsertAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
                progress?.Report($"Imported {chats:N0} of {sessions.Count:N0} agent chats…");
            }
        }
        added += await _store.UpsertAsync(batch, ct).ConfigureAwait(false);
        report.Chats = chats;
        return new ImportSummary(chats, added, withSecrets, report);
    }
}
