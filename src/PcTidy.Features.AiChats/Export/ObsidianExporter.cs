using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Export;

public sealed record ExportRequest
{
    /// <summary>The folder the user picked (inside an Obsidian vault). Notes go into "&lt;source&gt;" subfolders.</summary>
    public required string Folder { get; init; }
    public required IReadOnlyList<string> ChatIds { get; init; }
    /// <summary>Off by default: notes keep the masked text. On: the originals are read again and written as they were.</summary>
    public bool KeepSecrets { get; init; }
}

/// <summary>What an export will do. Nothing has been written yet: <see cref="Items"/> go through the confirm dialog.</summary>
public sealed record ExportPlan(
    IReadOnlyList<PlanItem> Items, int NewNotes, int UpdatedNotes, int UnchangedNotes, int AttachmentFiles, IReadOnlyList<string> Notes)
{
    public ActionPlan ToActionPlan() => new() { FeatureId = AiChatsIds.FeatureId, Title = "Export chats to Obsidian", Items = PlanBuilder.Normalize(Items) };
}

/// <summary>
/// Plans an export of library chats into an Obsidian folder: one note per chat (new = WriteTextFile; a note PC Tidy
/// wrote before, recognised by its pctidy-id = EditTextFile), wikilinks between related chats, attachments beside the
/// note when the export has them. A file PC Tidy didn't write is never overwritten: its name is simply avoided.
/// Read-only: only looks at the folder and the library.
/// </summary>
public sealed class ObsidianExporter
{
    private const int MaxRelated = 5;
    private const double MinSimilarity = 0.2;
    private static readonly HashSet<string> TextAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".json", ".html", ".htm", ".xml", ".log" };

    private readonly ChatLibraryStore _store;

    public ObsidianExporter(ChatLibraryStore store) => _store = store;

    public async Task<ExportPlan> PlanAsync(ExportRequest request, CancellationToken ct)
    {
        var folder = PathUtil.Normalize(request.Folder);
        var notes = new List<string>();
        var chats = new List<ChatDetail>();
        foreach (var id in request.ChatIds.Distinct(StringComparer.Ordinal))
            if (await _store.GetAsync(id, ct).ConfigureAwait(false) is { } d) chats.Add(d);
        if (chats.Count == 0) return new ExportPlan(Array.Empty<PlanItem>(), 0, 0, 0, 0, new[] { "Pick at least one chat to export." });

        if (request.KeepSecrets) chats = await Unmasked(chats, notes, ct).ConfigureAwait(false);

        var existing = await Task.Run(() => ExistingNotes.Scan(folder, ct), ct).ConfigureAwait(false);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string p) => existing.IsTaken(p) || reserved.Contains(p) || File.Exists(p) || Directory.Exists(p);

        // 1. Every note's final path first, so links can point at the right names.
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chat in chats.OrderBy(c => c.Summary.Date ?? DateTime.MaxValue).ThenBy(c => c.Summary.Id, StringComparer.Ordinal))
        {
            var id = chat.Summary.Id;
            if (existing.ById.TryGetValue(id, out var note)) { paths[id] = note.Path; continue; }
            var path = NoteNames.Free(DesiredPath(folder, chat.Summary), Taken);
            reserved.Add(path);
            paths[id] = path;
        }

        // 2. Related chats: similar words or the same topic/project, among chats that have (or will have) a note.
        var terms = await _store.TermsAsync(ct).ConfigureAwait(false);
        var linkable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, note) in existing.ById) linkable[id] = note.Path;
        foreach (var (id, path) in paths) linkable[id] = path;
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in chats) groups[c.Summary.Id] = c.Summary.Group;
        var index = new RelatedIndex(terms, groups, linkable, ct);

        var items = new List<PlanItem>();
        int added = 0, updated = 0, unchanged = 0, attachmentFiles = 0;
        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();
            var id = chat.Summary.Id;
            var path = paths[id];
            var related = Related(id, chat.Summary.Group, terms, groups, linkable, index);
            var (attachmentLinks, attachmentItems) = Attachments(chat, path, Taken, reserved);
            var content = MarkdownNote.Render(chat, new NoteLinks(related, attachmentLinks, request.KeepSecrets));
            var name = Path.GetFileName(path);

            if (existing.ById.TryGetValue(id, out var before))
            {
                if (string.Equals(Normalize(before.Content), Normalize(content), StringComparison.Ordinal)) unchanged++;
                else
                {
                    items.Add(Plans.EditText(before.Path, content, before.LastWriteUtc, $"Update the note \"{name}\" (PC Tidy wrote it before)"));
                    updated++;
                }
            }
            else
            {
                items.Add(Plans.WriteText(path, content, $"Save the note \"{name}\" in {Path.GetFileName(Path.GetDirectoryName(path))}"));
                added++;
            }
            items.AddRange(attachmentItems);
            attachmentFiles += attachmentItems.Count(i => i.Kind != ActionKind.CreateDirectory);
        }
        if (unchanged > 0) notes.Add($"{unchanged} note{(unchanged == 1 ? " is" : "s are")} already up to date.");
        return new ExportPlan(items, added, updated, unchanged, attachmentFiles, notes);
    }

    /// <summary>&lt;folder&gt;\ChatGPT\2026-01-02 Title.md; agent chats one level deeper by project.</summary>
    internal static string DesiredPath(string folder, ChatSummary s)
    {
        var dir = Path.Combine(folder, ChatSources.Display(s.Source));
        if (s.IsAgent) dir = Path.Combine(dir, NoteNames.Safe(AgentProjects.ShortName(s.Project), "No project"));
        var date = s.Date is { } d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " : "";
        return Path.Combine(dir, NoteNames.Safe(date + s.Title) + ".md");
    }

    /// <summary>How many of a chat's most frequent words are indexed, and how many candidates are scored in full.</summary>
    private const int IndexedTerms = 20, MaxCandidates = 200;

    /// <summary>
    /// Word → chats, over each linkable chat's most frequent words, and group → exported chats. Only chats that share
    /// one of those words (or the group) are scored, so exporting thousands of chats isn't every-chat-against-every-chat.
    /// </summary>
    private sealed class RelatedIndex
    {
        private readonly Dictionary<string, List<string>> _byTerm = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _byGroup = new(StringComparer.OrdinalIgnoreCase);

        public RelatedIndex(IReadOnlyDictionary<string, Dictionary<string, int>> terms, IReadOnlyDictionary<string, string> groups,
            IReadOnlyDictionary<string, string> linkable, CancellationToken ct)
        {
            foreach (var id in linkable.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (!terms.TryGetValue(id, out var t)) continue;
                foreach (var term in Top(t))
                    (_byTerm.TryGetValue(term, out var list) ? list : _byTerm[term] = new List<string>()).Add(id);
            }
            foreach (var (id, group) in groups)
                if (linkable.ContainsKey(id) && !string.Equals(group, TopicLabeler.OtherTopic, StringComparison.OrdinalIgnoreCase))
                    (_byGroup.TryGetValue(group, out var list) ? list : _byGroup[group] = new List<string>()).Add(id);
        }

        public static IEnumerable<string> Top(IReadOnlyDictionary<string, int> t)
            => t.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(IndexedTerms).Select(kv => kv.Key);

        public IEnumerable<string> Candidates(string id, IReadOnlyDictionary<string, int> mine, string group)
        {
            var shared = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in Top(mine))
                if (_byTerm.TryGetValue(term, out var ids))
                    foreach (var other in ids)
                        if (other != id) shared[other] = shared.GetValueOrDefault(other) + 1;
            var byWords = shared.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxCandidates).Select(kv => kv.Key);
            var byGroup = _byGroup.TryGetValue(group, out var g) ? g.Where(o => o != id).Take(MaxCandidates) : Enumerable.Empty<string>();
            return byWords.Concat(byGroup).Distinct(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<string> Related(string id, string group, IReadOnlyDictionary<string, Dictionary<string, int>> terms,
        IReadOnlyDictionary<string, string> groups, IReadOnlyDictionary<string, string> linkable, RelatedIndex index)
    {
        if (!terms.TryGetValue(id, out var mine)) mine = new();
        return index.Candidates(id, mine, group)
            .Select(other =>
            {
                var score = terms.TryGetValue(other, out var t) ? TopicLabeler.Similarity(mine, t) : 0;
                if (groups.TryGetValue(other, out var g) && string.Equals(g, group, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(group, TopicLabeler.OtherTopic, StringComparison.OrdinalIgnoreCase)) score += 0.3;
                return (Id: other, Score: score);
            })
            .Where(x => x.Score >= MinSimilarity)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(MaxRelated)
            .Select(x => Path.GetFileNameWithoutExtension(linkable[x.Id]))
            .ToList();
    }

    /// <summary>
    /// Attachments go in "&lt;note name&gt; files\" beside the note: Claude's extracted text as a text file, files from an
    /// extracted export folder copied (CopyPath). Files inside a zip are listed by name. Existing files are never replaced.
    /// </summary>
    private static (IReadOnlyList<AttachmentLink> Links, IReadOnlyList<PlanItem> Items) Attachments(
        ChatDetail chat, string notePath, Func<string, bool> taken, HashSet<string> reserved)
    {
        var links = new List<AttachmentLink>();
        var items = new List<PlanItem>();
        if (chat.Attachments.Count == 0) return (links, items);
        var noteName = Path.GetFileNameWithoutExtension(notePath);
        var dirName = noteName + " files";
        var dir = Path.Combine(Path.GetDirectoryName(notePath)!, dirName);
        bool folderPlanned = false;
        foreach (var a in chat.Attachments)
        {
            var safe = NoteNames.Safe(a.Name, "attachment");
            if (a.ExtractedText is { Length: > 0 } text)
            {
                if (!TextAttachmentExtensions.Contains(Path.GetExtension(safe))) safe += ".txt";
                var target = Path.Combine(dir, safe);
                links.Add(new AttachmentLink(a.Name, dirName + "/" + safe));
                if (!taken(target))
                {
                    reserved.Add(target);
                    items.Add(Plans.WriteText(target, text, $"Save the attachment \"{safe}\" beside its note"));
                }
            }
            else if (a.FilePath is { } source && File.Exists(source))
            {
                var target = Path.Combine(dir, safe);
                links.Add(new AttachmentLink(a.Name, dirName + "/" + safe));
                if (taken(target)) continue;
                reserved.Add(target);
                if (!folderPlanned && !Directory.Exists(dir)) { items.Add(Plans.CreateFolder(dir)); folderPlanned = true; }
                var info = new FileInfo(source);
                items.Add(Plans.Copy(source, target, info.Length, info.LastWriteTimeUtc, $"Copy the attachment \"{safe}\" beside its note"));
            }
            else links.Add(new AttachmentLink(a.Name, null));
        }
        return (links, items);
    }

    /// <summary>Swaps in the original (unmasked) text where the original can still be read; otherwise keeps the masked text.</summary>
    private static async Task<List<ChatDetail>> Unmasked(List<ChatDetail> chats, List<string> notes, CancellationToken ct)
    {
        var raw = await RawChatReader.ReadAsync(chats.Select(c => (c.Summary.Id, c.Summary.OriginKind, c.Summary.Origin, c.Summary.Source)), ct).ConfigureAwait(false);
        int missing = 0;
        var result = chats.Select(c =>
        {
            if (!raw.TryGetValue(c.Summary.Id, out var r)) { missing++; return c; }
            return c with
            {
                Summary = c.Summary with { Title = r.Title },
                Messages = r.Messages,
                Alternates = r.Alternates,
                Attachments = r.Attachments,
            };
        }).ToList();
        if (missing > 0) notes.Add($"{missing} chat{(missing == 1 ? "'s" : "s'")} original export couldn't be found, so {(missing == 1 ? "it stays" : "they stay")} masked.");
        return result;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
