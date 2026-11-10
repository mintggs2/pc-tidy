using System.Globalization;
using System.Text;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Export;

/// <summary>What a note links to besides the chat itself.</summary>
internal sealed record NoteLinks(IReadOnlyList<string> Related, IReadOnlyList<AttachmentLink> Attachments, bool SecretsKept);

/// <summary>An attachment as the note shows it: embedded/linked when it sits beside the note, else just named.</summary>
internal sealed record AttachmentLink(string Name, string? VaultLink);

/// <summary>
/// One Markdown note per chat, Obsidian-flavoured: YAML frontmatter (title, date, source, model, project, tags,
/// pctidy-id), the visible conversation, alternate versions, attachments, and [[wikilinks]] to related chats.
/// The output only depends on the chat (no "exported at" time), so re-exporting an unchanged chat changes nothing.
/// </summary>
internal static class MarkdownNote
{
    public static string Render(ChatDetail chat, NoteLinks links)
    {
        var s = chat.Summary;
        var sb = new StringBuilder();
        sb.Append("---\n");
        Yaml(sb, "title", s.Title);
        if (s.CreatedUtc is { } created) sb.Append("date: ").Append(created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        if (s.UpdatedUtc is { } updated) sb.Append("updated: ").Append(updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("source: ").Append(ChatSources.Key(s.Source)).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.Model)) Yaml(sb, "model", s.Model);
        if (!string.IsNullOrWhiteSpace(s.Project)) Yaml(sb, "project", s.IsAgent ? AgentProjects.ShortName(s.Project) : s.Project);
        if (!s.IsAgent && !string.IsNullOrWhiteSpace(s.Topic)) Yaml(sb, "topic", s.Topic);
        sb.Append("tags:\n");
        foreach (var tag in Tags(s)) sb.Append("  - ").Append(tag).Append('\n');
        sb.Append(AiChatsIds.NoteMarker).Append(": ").Append(s.Id).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# ").Append(OneLine(s.Title)).Append("\n\n");
        sb.Append("> [!info] ").Append(ChatSources.Display(s.Source)).Append(" chat, ")
          .Append(chat.Messages.Count.ToString(CultureInfo.InvariantCulture)).Append(chat.Messages.Count == 1 ? " message" : " messages")
          .Append(links.SecretsKept ? ". Keys and passwords are kept exactly as they were in the chat: keep this note private." : ". Keys and passwords are masked (like sk-ant-…9f2a).")
          .Append(" Exported by PC Tidy.\n\n");

        if (links.Related.Count > 0)
            sb.Append("**Related:** ").Append(string.Join(" · ", links.Related.Select(r => "[[" + r + "]]"))).Append("\n\n");

        AppendMessages(sb, s.Source, chat.Messages, 0);

        if (chat.Alternates.Count > 0)
        {
            sb.Append("---\n\n## Alternate versions\n\n");
            for (int i = 0; i < chat.Alternates.Count; i++)
            {
                var b = chat.Alternates[i];
                sb.Append("> [!note]- Version ").Append((i + 2).ToString(CultureInfo.InvariantCulture))
                  .Append(" (instead of message ").Append((b.ForkAt + 1).ToString(CultureInfo.InvariantCulture)).Append(" onwards)\n");
                var inner = new StringBuilder();
                AppendMessages(inner, s.Source, b.Messages, 0);
                foreach (var line in inner.ToString().TrimEnd().Split('\n')) sb.Append("> ").Append(line).Append('\n');
                sb.Append('\n');
            }
        }

        if (links.Attachments.Count > 0)
        {
            sb.Append("## Attachments\n\n");
            foreach (var a in links.Attachments)
                sb.Append("- ").Append(a.VaultLink is null ? OneLine(a.Name) + " (in your export zip)" : (IsImage(a.Name) ? "!" : "") + "[[" + a.VaultLink + "]]").Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendMessages(StringBuilder sb, ChatSource source, IReadOnlyList<ChatMessage> messages, int start)
    {
        for (int i = start; i < messages.Count; i++)
        {
            var m = messages[i];
            sb.Append("### ").Append(ChatSources.RoleName(source, m.Role));
            if (m.CreatedUtc is { } t) sb.Append(" · ").Append(t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC");
            sb.Append("\n\n").Append(m.Text.TrimEnd()).Append("\n\n");
        }
    }

    /// <summary>ai-chat, the source, the topic/project and the user's own tags (Obsidian tag syntax).</summary>
    internal static IEnumerable<string> Tags(ChatSummary s)
    {
        var tags = new List<string> { "ai-chat", ChatSources.Key(s.Source) };
        if (ChatLibraryStore.CleanTag(s.Group) is { } g && g != "other" && g != "no-project") tags.Add(g);
        foreach (var t in s.Tags) if (ChatLibraryStore.CleanTag(t) is { } c) tags.Add(c);
        return tags.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void Yaml(StringBuilder sb, string key, string? value)
        => sb.Append(key).Append(": \"").Append(OneLine(value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");

    private static string OneLine(string s) => string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static bool IsImage(string name)
        => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg";
}
