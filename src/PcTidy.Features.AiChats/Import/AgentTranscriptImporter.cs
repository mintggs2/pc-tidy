using System.Text.Json;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Reads local agent transcripts into chats. Both formats are undocumented and change often, so parsing is lenient:
/// unknown record types are ignored, damaged lines skipped and counted. Read-only and shared (a running agent is
/// never disturbed).
/// </summary>
public static class AgentTranscriptImporter
{
    private const int MaxToolText = 20_000;

    public static Task<ImportedChat?> ReadAsync(AgentSession session, IReadOnlyDictionary<string, string>? codexTitles, ImportReport report, CancellationToken ct)
        => session.Source == ChatSource.Codex ? ReadCodexAsync(session, codexTitles, report, ct) : ReadClaudeCodeAsync(session, report, ct);

    // ------------------------------------------------------------------ Claude Code

    internal sealed record Record(string? Uuid, string? Parent, ChatMessage Message, string? Cwd);

    /// <summary>
    /// Claude Code: user/assistant records with uuid/parentUuid; "summary" records give the title. The thread is rebuilt
    /// from the newest message up through parentUuid (logicalParentUuid across compactions); when that chain is broken
    /// the file order is used. Subagent (sidechain) records are left out of the main thread.
    /// </summary>
    public static async Task<ImportedChat?> ReadClaudeCodeAsync(AgentSession session, ImportReport report, CancellationToken ct)
    {
        var bad = new JsonlReader.Counter();
        var records = new List<Record>();
        string? summary = null, customTitle = null, model = null;
        var cwds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var e in JsonlReader.ReadAsync(session.TranscriptPath, bad, ct).ConfigureAwait(false))
            {
                var type = e.Str("type");
                if (type == "summary") { summary = e.Str("summary") ?? summary; continue; }
                if (type is "custom-title" or "title") { customTitle = e.Str("customTitle") ?? e.Str("title") ?? customTitle; continue; }
                if (type is not ("user" or "assistant")) continue; // file-history snapshots, system notes, future types
                if (e.Bool("isMeta") || e.Bool("isSidechain")) continue;
                if (e.Str("cwd") is { Length: > 0 } cwd) cwds[cwd] = cwds.GetValueOrDefault(cwd) + 1;
                var msg = e.Prop("message");
                if (msg?.Str("model") is { Length: > 0 } m && !m.StartsWith('<')) model = m;
                if (ClaudeCodeMessage(type, msg) is not { } message) continue;
                records.Add(new Record(e.Str("uuid"), e.Str("parentUuid") ?? e.Str("logicalParentUuid"),
                    message with { CreatedUtc = e.Time("timestamp"), Model = message.Role == ChatRole.Assistant ? model : null }, e.Str("cwd")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Note($"A Claude Code chat couldn't be read ({ex.Message}).");
            report.SkippedChats++;
            return null;
        }
        report.SkippedRecords += bad.Value;
        var messages = Thread(records);
        var cwdUsed = cwds.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? session.Cwd;
        return new ImportedChat
        {
            Source = ChatSource.ClaudeCode,
            SourceId = session.SessionId,
            Title = ChatGptParser.Clean(customTitle) ?? ChatGptParser.Clean(summary) ?? ChatGptParser.FirstLine(messages) ?? "Claude Code session",
            CreatedUtc = messages.FirstOrDefault(m => m.CreatedUtc is not null)?.CreatedUtc ?? session.CreatedUtc,
            UpdatedUtc = messages.LastOrDefault(m => m.CreatedUtc is not null)?.CreatedUtc ?? session.LastWriteUtc,
            Model = model,
            Project = cwdUsed,
            Messages = messages,
            OriginKind = OriginKind.TranscriptFile,
            Origin = session.TranscriptPath,
            OriginBytes = session.TotalBytes,
        };
    }

    private static ChatMessage? ClaudeCodeMessage(string type, JsonElement? msg)
    {
        if (msg is not { } m) return null;
        var role = (m.Str("role") ?? type) == "assistant" ? ChatRole.Assistant : ChatRole.User;
        string text;
        bool onlyToolResults = false;
        switch (m.Prop("content"))
        {
            case { ValueKind: JsonValueKind.String } s:
                text = s.GetString() ?? "";
                break;
            case { ValueKind: JsonValueKind.Array } a:
                var blocks = a.EnumerateArray().ToList();
                onlyToolResults = blocks.Count > 0 && blocks.All(b => b.Str("type") == "tool_result");
                text = JsonUtil.JoinBlocks(blocks.Select(ClaudeWebParser.Block));
                break;
            default:
                return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (onlyToolResults || role == ChatRole.User && IsToolNoise(text)) role = ChatRole.Tool;
        return new ChatMessage(role, role == ChatRole.Tool ? JsonUtil.Truncate(text, MaxToolText) : text);
    }

    /// <summary>Local command echoes ("&lt;local-command-stdout&gt;") aren't something the user typed.</summary>
    private static bool IsToolNoise(string text)
        => text.StartsWith("<local-command-stdout>", StringComparison.Ordinal) || text.StartsWith("<command-name>", StringComparison.Ordinal)
           || text.StartsWith("<bash-stdout>", StringComparison.Ordinal);

    /// <summary>Newest message → parents → root, reversed. Falls back to file order when the chain covers too little.</summary>
    internal static List<ChatMessage> Thread(IReadOnlyList<Record> records)
    {
        if (records.Count == 0) return new List<ChatMessage>();
        var byUuid = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
            if (records[i].Uuid is { } u) byUuid[u] = i;
        if (byUuid.Count == 0) return records.Select(r => r.Message).ToList();

        var chain = new List<int>();
        var seen = new HashSet<int>();
        int? cur = records.Count - 1;
        while (cur is { } c && seen.Add(c))
        {
            chain.Add(c);
            cur = records[c].Parent is { } p && byUuid.TryGetValue(p, out var pi) ? pi : null;
        }
        // Parents pointing at records that aren't messages (tool bookkeeping) break the chain: then file order is truer.
        if (chain.Count < records.Count * 0.6) return records.Select(r => r.Message).ToList();
        chain.Reverse();
        return chain.Select(i => records[i].Message).ToList();
    }

    // ------------------------------------------------------------------ Codex

    /// <summary>
    /// Codex rollouts: "session_meta" (id, cwd), "turn_context" (model), "response_item" (messages, reasoning, tool
    /// calls and outputs). Older rollouts have the items directly. "event_msg" repeats what the items say and is skipped.
    /// </summary>
    public static async Task<ImportedChat?> ReadCodexAsync(AgentSession session, IReadOnlyDictionary<string, string>? titles, ImportReport report, CancellationToken ct)
    {
        var bad = new JsonlReader.Counter();
        var messages = new List<ChatMessage>();
        string? id = null, cwd = null, model = null;
        DateTime? started = null;
        try
        {
            await foreach (var e in JsonlReader.ReadAsync(session.TranscriptPath, bad, ct).ConfigureAwait(false))
            {
                var wrapped = e.Prop("payload") is { ValueKind: JsonValueKind.Object };
                var rec = wrapped ? e.Prop("payload")!.Value : e;
                var outer = e.Str("type");
                var time = e.Time("timestamp") ?? rec.Time("timestamp");
                if (outer == "session_meta" || !wrapped && outer is null && rec.Str("id") is not null && messages.Count == 0)
                {
                    id ??= rec.Str("id");
                    cwd ??= rec.Str("cwd");
                    started ??= rec.Time("timestamp") ?? time;
                    continue;
                }
                if (outer == "turn_context")
                {
                    cwd ??= rec.Str("cwd");
                    model = rec.Str("model") ?? model;
                    continue;
                }
                if (outer is "event_msg" or "compacted") continue;
                if (wrapped && outer != "response_item") continue; // unknown future record types
                if (CodexItem(rec, time, model) is { } m) messages.Add(m);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Note($"A Codex chat couldn't be read ({ex.Message}).");
            report.SkippedChats++;
            return null;
        }
        report.SkippedRecords += bad.Value;
        var sessionId = id is not null && Guid.TryParse(id, out _) ? id : session.SessionId;
        var title = titles is not null && titles.TryGetValue(sessionId, out var t) ? t : null;
        return new ImportedChat
        {
            Source = ChatSource.Codex,
            SourceId = sessionId,
            Title = ChatGptParser.Clean(title) ?? ChatGptParser.FirstLine(messages) ?? "Codex session",
            CreatedUtc = started ?? messages.FirstOrDefault(m => m.CreatedUtc is not null)?.CreatedUtc ?? session.CreatedUtc,
            UpdatedUtc = messages.LastOrDefault(m => m.CreatedUtc is not null)?.CreatedUtc ?? session.LastWriteUtc,
            Model = model,
            Project = cwd ?? session.Cwd,
            Messages = messages,
            OriginKind = OriginKind.TranscriptFile,
            Origin = session.TranscriptPath,
            OriginBytes = session.TotalBytes,
        };
    }

    private static ChatMessage? CodexItem(JsonElement item, DateTime? time, string? model)
    {
        switch (item.Str("type"))
        {
            case "message":
                var role = item.Str("role");
                if (role is not ("user" or "assistant")) return null; // developer/system prompts
                var text = JsonUtil.JoinBlocks(item.Array("content").Select(c => c.Str("text") ?? (c.ValueKind == JsonValueKind.String ? c.GetString() : null)));
                if (string.IsNullOrWhiteSpace(text) || role == "user" && IsCodexContext(text)) return null;
                return new ChatMessage(role == "user" ? ChatRole.User : ChatRole.Assistant, text, time, role == "assistant" ? model : null);
            case "reasoning":
                var summary = JsonUtil.JoinBlocks(item.Array("summary").Select(s => s.Str("text")));
                return summary.Length == 0 ? null : new ChatMessage(ChatRole.Assistant, "*Thinking:* " + JsonUtil.Truncate(summary, 4000), time, model);
            case "function_call" or "custom_tool_call" or "local_shell_call":
                var args = item.Str("arguments") ?? item.Str("input") ?? (item.Prop("action") is { } a ? JsonUtil.Raw(a, MaxToolText) : "");
                return new ChatMessage(ChatRole.Tool, $"**Tool:** `{item.Str("name") ?? "shell"}`" + (string.IsNullOrWhiteSpace(args) ? "" : $"\n```\n{JsonUtil.Truncate(args, MaxToolText)}\n```"), time);
            case "function_call_output" or "custom_tool_call_output":
                var output = item.Prop("output") is { } o ? o.ValueKind == JsonValueKind.String ? o.GetString() : o.Str("content") ?? JsonUtil.Raw(o, MaxToolText) : null;
                return string.IsNullOrWhiteSpace(output) ? null : new ChatMessage(ChatRole.Tool, $"```output\n{JsonUtil.Truncate(output.TrimEnd(), MaxToolText)}\n```", time);
            default:
                return null;
        }
    }

    /// <summary>Codex prepends its environment and AGENTS.md instructions as "user" messages: not something the user typed.</summary>
    private static bool IsCodexContext(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("<environment_context>", StringComparison.Ordinal) || t.StartsWith("<user_instructions>", StringComparison.Ordinal)
            || t.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal);
    }
}
