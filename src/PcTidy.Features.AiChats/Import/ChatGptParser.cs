using System.Text;
using System.Text.Json;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// One ChatGPT conversation (an element of conversations.json or conversations-NNN.json). A chat is a tree of nodes
/// (editing a question or regenerating an answer adds a branch): the visible chat is the path from the root to
/// <c>current_node</c>; the other branches are kept as alternate versions. System, hidden and custom-instruction nodes
/// are skipped; content types PC Tidy doesn't know are shown as raw data.
/// </summary>
internal static class ChatGptParser
{
    private const int MaxAlternates = 25;
    private const int MaxToolText = 20_000;

    public static ImportedChat? Parse(JsonElement conv, ExportArchive archive, AttachmentIndex files, ImportReport report)
    {
        if (conv.Prop("mapping") is not { ValueKind: JsonValueKind.Object } mapping) return null;
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in mapping.EnumerateObject()) nodes[p.Name] = p.Value;

        var path = VisiblePath(nodes, conv.Str("current_node"));
        var attachments = new List<ChatAttachment>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<ChatMessage>();
        var renderedBefore = new int[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            if (Render(nodes[path[i]], archive, files, attachments, seenFiles) is { } m) messages.Add(m);
            renderedBefore[i] = messages.Count;
        }

        var alternates = new List<ChatBranch>();
        for (int i = 0; i < path.Count - 1 && alternates.Count < MaxAlternates; i++)
        {
            var next = path[i + 1];
            foreach (var child in Children(nodes[path[i]]))
            {
                if (child == next || !nodes.ContainsKey(child)) continue;
                var branch = new List<ChatMessage>();
                foreach (var nodeId in WalkDown(nodes, child))
                    if (Render(nodes[nodeId], archive, files, attachments, seenFiles) is { } m) branch.Add(m);
                if (branch.Count > 0) alternates.Add(new ChatBranch(renderedBefore[i], branch));
                if (alternates.Count >= MaxAlternates) break;
            }
        }

        var id = conv.Str("conversation_id") ?? conv.Str("id") ?? ChatIds.Synthetic(conv.Str("title"), conv.Str("create_time"));
        var title = Clean(conv.Str("title")) ?? FirstLine(messages) ?? "Untitled chat";
        return new ImportedChat
        {
            Source = ChatSource.ChatGpt,
            SourceId = id,
            Title = title,
            CreatedUtc = conv.Time("create_time") ?? messages.FirstOrDefault()?.CreatedUtc,
            UpdatedUtc = conv.Time("update_time") ?? messages.LastOrDefault()?.CreatedUtc,
            Model = conv.Str("default_model_slug") ?? messages.LastOrDefault(m => m.Model is not null)?.Model,
            Project = null,
            Messages = messages,
            Alternates = alternates,
            Attachments = attachments,
            OriginKind = archive.Kind,
            Origin = archive.Path,
        };
    }

    /// <summary>Root → current_node. Without a usable current_node: from the root, always the newest child.</summary>
    internal static List<string> VisiblePath(Dictionary<string, JsonElement> nodes, string? currentNode)
    {
        var path = new List<string>();
        if (currentNode is not null && nodes.ContainsKey(currentNode))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var cur = currentNode; cur is not null && nodes.ContainsKey(cur) && seen.Add(cur); cur = nodes[cur].Str("parent"))
                path.Add(cur);
            path.Reverse();
            return path;
        }
        var root = nodes.FirstOrDefault(kv => kv.Value.Str("parent") is not { } p || !nodes.ContainsKey(p)).Key;
        return root is null ? path : WalkDown(nodes, root).ToList();
    }

    /// <summary>From a node down, always following the newest (last) child.</summary>
    private static IEnumerable<string> WalkDown(Dictionary<string, JsonElement> nodes, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = start; cur is not null && nodes.ContainsKey(cur) && seen.Add(cur); cur = Children(nodes[cur]).LastOrDefault(nodes.ContainsKey))
            yield return cur;
    }

    private static IEnumerable<string> Children(JsonElement node)
        => node.Array("children").Select(JsonUtil.AsString).OfType<string>();

    private static ChatMessage? Render(JsonElement node, ExportArchive archive, AttachmentIndex files, List<ChatAttachment> attachments, HashSet<string> seenFiles)
    {
        if (node.Prop("message") is not { } msg) return null;
        var role = msg.Path("author", "role") is { } r ? JsonUtil.AsString(r) : null;
        if (role is null or "system") return null;
        if (msg.Path("metadata", "is_visually_hidden_from_conversation") is { ValueKind: JsonValueKind.True }) return null;
        if (msg.Prop("content") is not { } content) return null;
        var type = content.Str("content_type") ?? "text";
        if (type is "user_editable_context" or "model_editable_context") return null; // custom instructions / memory

        var text = type switch
        {
            "text" => Parts(content, archive, files, attachments, seenFiles),
            "multimodal_text" => Parts(content, archive, files, attachments, seenFiles),
            "code" => Fence(content.Str("language") is { Length: > 0 } lang && lang != "unknown" ? lang : "", content.Str("text")),
            "execution_output" => Fence("output", content.Str("text")),
            "tether_quote" => Quote(content.Str("title"), content.Str("text")),
            "tether_browsing_display" => content.Str("result") ?? content.Str("summary"),
            "reasoning_recap" => content.Str("content") is { Length: > 0 } recap ? "*" + recap + "*" : null,
            "thoughts" => Thoughts(content),
            "system_error" => content.Str("text") is { } err ? "⚠ " + err : null,
            _ => Fence("data", JsonUtil.Raw(content)),
        };

        foreach (var a in msg.Path("metadata") is { } meta ? meta.Array("attachments") : Enumerable.Empty<JsonElement>())
            AddAttachment(a.Str("id"), a.Str("name"), a.Long("size"), archive, files, attachments, seenFiles);

        if (string.IsNullOrWhiteSpace(text)) return null;
        var chatRole = role switch { "user" => ChatRole.User, "assistant" => ChatRole.Assistant, _ => ChatRole.Tool };
        if (chatRole == ChatRole.Tool) text = JsonUtil.Truncate(text, MaxToolText);
        return new ChatMessage(chatRole, text, msg.Time("create_time"), msg.Path("metadata", "model_slug") is { } slug ? JsonUtil.AsString(slug) : null);
    }

    private static string Parts(JsonElement content, ExportArchive archive, AttachmentIndex files, List<ChatAttachment> attachments, HashSet<string> seenFiles)
    {
        var pieces = new List<string?>();
        foreach (var part in content.Array("parts"))
        {
            if (part.ValueKind == JsonValueKind.String) { pieces.Add(part.GetString()); continue; }
            if (part.ValueKind != JsonValueKind.Object) continue;
            switch (part.Str("content_type"))
            {
                case "image_asset_pointer":
                    var pointer = part.Str("asset_pointer");
                    var fileId = pointer is null ? null : pointer[(pointer.LastIndexOf('/') + 1)..];
                    var name = AddAttachment(fileId, null, part.Long("size_bytes"), archive, files, attachments, seenFiles);
                    pieces.Add($"[image: {name ?? fileId ?? "picture"}]");
                    break;
                case "audio_transcription":
                    pieces.Add(part.Str("text"));
                    break;
                case "audio_asset_pointer" or "real_time_user_audio_video_asset_pointer":
                    pieces.Add("[voice message]");
                    break;
                default:
                    if (part.Str("text") is { Length: > 0 } t) pieces.Add(t);
                    break;
            }
        }
        return JsonUtil.JoinBlocks(pieces);
    }

    /// <summary>Records an attachment once per chat; returns its display name.</summary>
    private static string? AddAttachment(string? id, string? name, long? size, ExportArchive archive, AttachmentIndex files, List<ChatAttachment> attachments, HashSet<string> seenFiles)
    {
        if (id is null && name is null) return null;
        var entry = id is null ? null : files.Find(id);
        entry ??= name is null ? null : files.FindByName(name);
        var display = name ?? (entry is null ? id! : AttachmentIndex.DisplayName(entry));
        if (!seenFiles.Add(entry ?? id ?? display)) return display;
        attachments.Add(new ChatAttachment(display, entry is null ? size ?? 0 : archive.EntrySize(entry),
            ArchiveEntry: archive.Kind == OriginKind.ExportZip ? entry : null,
            FilePath: entry is null ? null : archive.FilePathOf(entry)));
        return display;
    }

    private static string? Thoughts(JsonElement content)
    {
        var lines = content.Array("thoughts").Select(t => t.Str("summary") ?? t.Str("content")).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return lines.Count == 0 ? null : "*Thinking:* " + string.Join(" · ", lines);
    }

    private static string? Fence(string lang, string? text)
        => string.IsNullOrWhiteSpace(text) ? null : $"```{lang}\n{JsonUtil.Truncate(text.TrimEnd(), MaxToolText)}\n```";

    private static string? Quote(string? title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("> **").Append(title.Trim()).Append("**\n");
        foreach (var line in text.Split('\n')) sb.Append("> ").Append(line.TrimEnd('\r')).Append('\n');
        return sb.ToString().TrimEnd();
    }

    internal static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= 200 ? t : t[..200];
    }

    internal static string? FirstLine(IEnumerable<ChatMessage> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(first)) return null;
        var line = first.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = line.TrimStart('#', '>', ' ', '*');
        return Clean(line.Length > 80 ? line[..80].TrimEnd() + "…" : line);
    }
}

/// <summary>
/// Finds an export's attachment files by id. ChatGPT stores them as "file-&lt;id&gt;-&lt;original name&gt;" (or
/// "file_&lt;id&gt;-…") anywhere in the zip.
/// </summary>
internal sealed class AttachmentIndex
{
    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

    public AttachmentIndex(ExportArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            var name = ExportArchive.FileName(entry);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
            if (IdOf(name) is { } id) _byId.TryAdd(id, entry);
            _byName.TryAdd(name, entry);
        }
    }

    public string? Find(string id) => _byId.TryGetValue(id, out var e) ? e : null;

    public string? FindByName(string name) => _byName.TryGetValue(name, out var e) ? e : null;

    private static string? IdOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("file-", StringComparison.OrdinalIgnoreCase))
        {
            var cut = stem.IndexOf('-', 5);
            return cut > 5 ? stem[..cut] : stem;
        }
        if (stem.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
        {
            var cut = stem.IndexOf('-');
            return cut > 5 ? stem[..cut] : stem;
        }
        return null;
    }

    /// <summary>"file-AbC-holiday.png" → "holiday.png".</summary>
    public static string DisplayName(string entry)
    {
        var name = ExportArchive.FileName(entry);
        if (IdOf(name) is { } id && name.Length > id.Length + 1 && name[id.Length] == '-') return name[(id.Length + 1)..];
        return name;
    }
}
