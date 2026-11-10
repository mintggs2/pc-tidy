using System.Text.Json;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// One Claude.ai conversation (an element of conversations.json). Claude's export doesn't mark branches, so the
/// messages are kept in time order. Content blocks (text, thinking, tool use, tool results) become Markdown; attachments
/// keep their extracted text (Claude's export includes it), uploaded images are name-only.
/// </summary>
internal static class ClaudeWebParser
{
    private const int MaxToolText = 20_000;

    public static ImportedChat? Parse(JsonElement conv, ExportArchive archive, IReadOnlyDictionary<string, string> projectNames)
    {
        if (conv.Prop("chat_messages") is not { ValueKind: JsonValueKind.Array } list) return null;
        var messages = new List<(ChatMessage Message, int Order)>();
        var attachments = new List<ChatAttachment>();
        int order = 0;
        foreach (var m in list.EnumerateArray())
        {
            order++;
            var role = (m.Str("sender") ?? m.Str("role")) switch
            {
                "human" or "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.Tool,
            };
            var text = Content(m);
            foreach (var a in m.Array("attachments"))
            {
                var name = a.Str("file_name") ?? a.Str("name") ?? "attachment";
                var extracted = a.Str("extracted_content");
                attachments.Add(new ChatAttachment(name, a.Long("file_size") ?? (extracted?.Length ?? 0), ExtractedText: string.IsNullOrEmpty(extracted) ? null : extracted));
                text = JsonUtil.JoinBlocks(new[] { text, $"[attached: {name}]" });
            }
            foreach (var f in m.Array("files"))
            {
                var name = f.Str("file_name") ?? f.Str("name");
                if (name is null) continue;
                attachments.Add(new ChatAttachment(name, 0));
                text = JsonUtil.JoinBlocks(new[] { text, $"[file: {name}]" });
            }
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add((new ChatMessage(role, text, m.Time("created_at")), order));
        }

        // Branches aren't marked in this export: keep a stable time order (file order breaks ties).
        var ordered = messages.OrderBy(x => x.Message.CreatedUtc ?? DateTime.MaxValue).ThenBy(x => x.Order).Select(x => x.Message).ToList();
        var id = conv.Str("uuid") ?? conv.Str("id") ?? ChatIds.Synthetic(conv.Str("name"), conv.Str("created_at"));
        string? project = null;
        var projectId = conv.Str("project_uuid") ?? (conv.Prop("project") is { } p ? p.Str("uuid") : null);
        if (projectId is not null && projectNames.TryGetValue(projectId, out var pn)) project = pn;
        project ??= conv.Prop("project") is { } po ? po.Str("name") : null;

        return new ImportedChat
        {
            Source = ChatSource.ClaudeWeb,
            SourceId = id,
            Title = ChatGptParser.Clean(conv.Str("name")) ?? ChatGptParser.FirstLine(ordered) ?? "Untitled chat",
            CreatedUtc = conv.Time("created_at") ?? ordered.FirstOrDefault()?.CreatedUtc,
            UpdatedUtc = conv.Time("updated_at") ?? ordered.LastOrDefault()?.CreatedUtc,
            Model = conv.Str("model"),
            Project = project,
            Messages = ordered,
            Attachments = attachments,
            OriginKind = archive.Kind,
            Origin = archive.Path,
        };
    }

    private static string Content(JsonElement m)
    {
        var blocks = m.Array("content").ToList();
        if (blocks.Count == 0) return m.Str("text") ?? "";
        var parts = new List<string?>();
        foreach (var b in blocks) parts.Add(Block(b));
        var joined = JsonUtil.JoinBlocks(parts);
        return joined.Length > 0 ? joined : m.Str("text") ?? "";
    }

    internal static string? Block(JsonElement b)
    {
        switch (b.Str("type"))
        {
            case "text":
                return b.Str("text");
            case "thinking":
                return b.Str("thinking") is { Length: > 0 } t ? "*Thinking:* " + JsonUtil.Truncate(t, 4000) : null;
            case "redacted_thinking":
                return null;
            case "tool_use":
                var input = b.Prop("input") is { } i ? JsonUtil.Raw(i, MaxToolText) : "";
                return $"**Tool:** `{b.Str("name") ?? "tool"}`" + (input.Length > 0 ? $"\n```json\n{input}\n```" : "");
            case "tool_result":
                return ToolResult(b);
            case "image":
                return "[image]";
            case "voice_note":
                return b.Str("text");
            case null:
                return b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            default:
                return b.Str("text") ?? $"```data\n{JsonUtil.Raw(b)}\n```";
        }
    }

    private static string? ToolResult(JsonElement b)
    {
        string? text = b.Prop("content") switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Array } a => JsonUtil.JoinBlocks(a.EnumerateArray().Select(x => x.Str("text") ?? (x.ValueKind == JsonValueKind.String ? x.GetString() : null))),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : $"```output\n{JsonUtil.Truncate(text.TrimEnd(), MaxToolText)}\n```";
    }
}
