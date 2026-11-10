using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>
/// Made-up secrets for masking tests. Built from pieces at run time so no complete key-shaped string sits in the
/// source (secret scanners on the repo stay quiet), and random bodies are seeded, so every run is identical.
/// </summary>
internal static class FakeSecrets
{
    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Body(int length, string alphabet = Alnum, int seed = 1)
    {
        var r = new Random(seed * 7919 + length);
        var sb = new StringBuilder(length);
        while (sb.Length < length)
        {
            var c = alphabet[r.Next(alphabet.Length)];
            // keep bodies away from placeholder words and long runs
            if (sb.Length > 0 && sb[^1] == c) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Anthropic => "sk-" + "ant-" + "api03-" + Body(93, Alnum + "_-", 2) + "AA";
    public static string OpenAiProject => "sk-" + "proj-" + Body(48, Alnum + "_-", 3);
    public static string OpenAiLegacy => "sk-" + Body(20, Alnum, 4) + "T3Blbk" + "FJ" + Body(20, Alnum, 5);
    public static string GitHubFineGrained => "github" + "_pat_" + Body(82, Alnum + "_", 6);
    public static string GitHubClassic => "gh" + "p_" + Body(36, Alnum, 7);
    public static string Aws => "AK" + "IA" + Body(16, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", 8);
    public static string Google => "AI" + "za" + "S" + Body(34, Alnum + "_", 9);
    public static string Slack => "xo" + "xb-" + Body(12, "0123456789", 10) + "-" + Body(13, "0123456789", 11) + "-" + Body(24, Alnum, 12);
    public static string SlackWebhook => "https://hooks.slack" + ".com/services/T" + Body(8, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 13) + "/B" + Body(8, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 14) + "/" + Body(24, Alnum, 15);
    public static string Stripe => "sk" + "_live_" + Body(24, Alnum, 16);
    public static string HuggingFace => "hf" + "_" + Body(34, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 17);
    public static string Npm => "np" + "m_" + Body(36, Alnum, 18);
    public static string AzureStorage => "Account" + "Key=" + Body(86, Alnum + "+/", 19) + "==";
    public static string AzureAdSecret => Body(3, Alnum, 20) + "8" + "Q~" + Body(34, Alnum, 21);
    public static string Discord => "M" + Body(25, Alnum, 22) + "." + Body(6, Alnum, 23) + "." + Body(27, Alnum + "_-", 24);
    public static string Jwt(DateTime expiresUtc)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = (long)(expiresUtc - DateTime.UnixEpoch).TotalSeconds;
        return B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + B64("{\"sub\":\"tester-account\",\"exp\":" + exp + "}") + "." + Body(43, Alnum + "_-", 25);
    }
    public static string Pem => "-----BEGIN " + "PRIVATE KEY-----\n" + Body(64, Alnum + "+/", 26) + "\n" + Body(64, Alnum + "+/", 27) + "\n-----END " + "PRIVATE KEY-----";
    // Obviously not a real password, but it avoids the masker's placeholder words ("fake", "example", ...) so it still counts as live.
    public static string Password => "pass" + "word=" + "Not-A-Real-" + "Passw0rd-42!";
}

/// <summary>Builds made-up ChatGPT and Claude export zips (and extracted folders) in the shapes their real exports use.</summary>
internal static class FakeExports
{
    public static readonly double T0 = (new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    // ------------------------------------------------------------------ ChatGPT

    public sealed class ChatGptBuilder
    {
        private readonly JsonObject _mapping = new();
        private int _clock;
        public string Id { get; }
        public string Title { get; }
        public string? CurrentNode { get; set; }

        public ChatGptBuilder(string id, string title)
        {
            Id = id;
            Title = title;
            _mapping["root"] = new JsonObject { ["id"] = "root", ["message"] = null, ["parent"] = null, ["children"] = new JsonArray() };
        }

        /// <summary>Adds a node under <paramref name="parent"/>. content: a string (text) or a full content object.</summary>
        public ChatGptBuilder Node(string id, string parent, string role, JsonNode content, JsonObject? metadata = null)
        {
            var c = content is JsonValue v && v.TryGetValue<string>(out var s)
                ? new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(s) }
                : content;
            _mapping[id] = new JsonObject
            {
                ["id"] = id,
                ["message"] = new JsonObject
                {
                    ["id"] = id,
                    ["author"] = new JsonObject { ["role"] = role },
                    ["create_time"] = T0 + ++_clock * 60,
                    ["content"] = c,
                    ["metadata"] = metadata ?? new JsonObject(),
                },
                ["parent"] = parent,
                ["children"] = new JsonArray(),
            };
            ((JsonArray)_mapping[parent]!["children"]!).Add(id);
            CurrentNode = id;
            return this;
        }

        public JsonObject Build() => new()
        {
            ["title"] = Title,
            ["create_time"] = T0,
            ["update_time"] = T0 + 3600,
            ["mapping"] = JsonNode.Parse(_mapping.ToJsonString()),
            ["current_node"] = CurrentNode,
            ["conversation_id"] = Id,
            ["id"] = Id,
            ["default_model_slug"] = "gpt-4o",
        };
    }

    /// <summary>
    /// The branch fixture: the user edited the first question once (cake → bread) and regenerated the first answer.
    /// Visible: u1 "bread", a1b, u2, a2. Alternates: [u1x, a1x] from message 1, [a1] from message 2.
    /// Also a hidden system node and a custom-instructions node that must be skipped.
    /// </summary>
    public static JsonObject BranchyChat(string id = "c0ffee00-0000-4000-8000-000000000001")
    {
        var b = new ChatGptBuilder(id, "Baking bread at home");
        b.Node("sys", "root", "system", "You are a helpful assistant.", new JsonObject { ["is_visually_hidden_from_conversation"] = true });
        b.Node("ctx", "sys", "user", new JsonObject { ["content_type"] = "user_editable_context", ["user_profile"] = "Tester likes baking" });
        b.Node("u1x", "ctx", "user", "How do I bake a cake?");
        b.Node("a1x", "u1x", "assistant", "Use sugar, eggs and flour.");
        b.Node("u1", "ctx", "user", "How do I bake bread?");
        b.Node("a1", "u1", "assistant", "Use flour.");
        b.Node("a1b", "u1", "assistant", "Use flour, water, salt and yeast.", new JsonObject { ["model_slug"] = "gpt-4o" });
        b.Node("u2", "a1b", "user", "And sourdough bread?");
        b.Node("a2", "u2", "assistant", new JsonObject { ["content_type"] = "code", ["language"] = "python", ["text"] = "starter = feed(flour, water)" });
        b.Node("a3", "a2", "tool", new JsonObject { ["content_type"] = "execution_output", ["text"] = "starter ready" });
        b.Node("a4", "a3", "assistant", new JsonObject { ["content_type"] = "brand_new_type", ["payload"] = 42 });
        b.CurrentNode = "a4";
        return b.Build();
    }

    public static JsonObject SimpleChat(string id, string title, string question, string answer, string? imageFileId = null)
    {
        var b = new ChatGptBuilder(id, title);
        if (imageFileId is null) b.Node("u", "root", "user", question);
        else b.Node("u", "root", "user", new JsonObject
        {
            ["content_type"] = "multimodal_text",
            ["parts"] = new JsonArray(
                new JsonObject { ["content_type"] = "image_asset_pointer", ["asset_pointer"] = "file-service://" + imageFileId, ["size_bytes"] = 4 },
                question),
        });
        b.Node("a", "u", "assistant", answer);
        return b.Build();
    }

    // ------------------------------------------------------------------ Claude

    public static JsonObject ClaudeChat(string uuid, string name, string? projectUuid, params (string Sender, string Text, int Minute)[] messages)
    {
        var list = new JsonArray();
        foreach (var (sender, text, minute) in messages)
            list.Add(new JsonObject
            {
                ["uuid"] = Guid.NewGuid().ToString(),
                ["sender"] = sender,
                ["text"] = text,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["created_at"] = new DateTime(2026, 4, 2, 10, minute, 0, DateTimeKind.Utc).ToString("o"),
                ["attachments"] = new JsonArray(),
                ["files"] = new JsonArray(),
            });
        var o = new JsonObject
        {
            ["uuid"] = uuid,
            ["name"] = name,
            ["created_at"] = "2026-04-02T10:00:00Z",
            ["updated_at"] = "2026-04-02T11:00:00Z",
            ["chat_messages"] = list,
        };
        if (projectUuid is not null) o["project_uuid"] = projectUuid;
        return o;
    }

    // ------------------------------------------------------------------ zips

    public static string Zip(string path, params (string Name, string Text)[] entries)
        => ZipBytes(path, entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Text))).ToArray());

    public static string ZipBytes(string path, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    public static string Json(params JsonNode[] items) => new JsonArray(items.Select(i => JsonNode.Parse(i.ToJsonString())).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

/// <summary>Made-up Claude Code and Codex folders (transcripts plus the settings/credentials files that must never be opened).</summary>
internal static class FakeAgents
{
    public const string ClaudeSession = "11111111-2222-4333-8444-555555555555";
    public const string CodexSession = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    public const string CodexOldSession = "99999999-8888-4777-8666-555555555555";
    public const string ProjectCwd = @"C:\Users\tester\source\my-app";

    public static string Line(object o) => JsonSerializer.Serialize(o);

    /// <summary>A Claude Code project with one session (lenient-parsing cases inside) and its sidecar folder.</summary>
    public static string ClaudeCodeSession(string claudeDir, DateTime writtenUtc, string session = ClaudeSession, string? secret = null)
    {
        var dir = Path.Combine(claudeDir, "projects", "C--Users-tester-source-my-app");
        var lines = new List<string>
        {
            Line(new { type = "summary", summary = "Fix the login button", leafUuid = "u4" }),
            Line(new { type = "file-history-snapshot", messageId = "x", snapshot = new { files = 3 } }),
            Line(new { parentUuid = (string?)null, isSidechain = false, type = "user", cwd = ProjectCwd, sessionId = session, uuid = "u1", timestamp = "2026-09-01T10:00:00Z",
                message = new { role = "user", content = "The login button does nothing" + (secret is null ? "" : " and my key is " + secret) } }),
            Line(new { parentUuid = "u1", type = "assistant", cwd = ProjectCwd, uuid = "u2", timestamp = "2026-09-01T10:01:00Z",
                message = new { role = "assistant", model = "claude-test-model", content = new object[] { new { type = "text", text = "Let me run the tests." }, new { type = "tool_use", id = "t1", name = "Bash", input = new { command = "npm test" } } } } }),
            "{not valid json at all",
            Line(new { parentUuid = "u2", type = "user", cwd = ProjectCwd, uuid = "u3", timestamp = "2026-09-01T10:02:00Z",
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = "3 passing" } } } }),
            Line(new { type = "brand-new-record-type", foo = 1 }),
            Line(new { parentUuid = "u3", isSidechain = true, type = "assistant", uuid = "s1", timestamp = "2026-09-01T10:02:30Z",
                message = new { role = "assistant", content = new object[] { new { type = "text", text = "Subagent chatter" } } } }),
            Line(new { parentUuid = "u3", isMeta = true, type = "user", uuid = "m1", timestamp = "2026-09-01T10:02:40Z", message = new { role = "user", content = "Caveat: meta" } }),
            Line(new { parentUuid = "u3", type = "assistant", cwd = ProjectCwd, uuid = "u4", timestamp = "2026-09-01T10:03:00Z",
                message = new { role = "assistant", model = "claude-test-model", content = new object[] { new { type = "text", text = "Fixed it: the click handler was never attached." } } } }),
        };
        var file = AiSandbox.Put(Path.Combine(dir, session + ".jsonl"), string.Join("\n", lines) + "\n", writtenUtc);
        AiSandbox.Put(Path.Combine(dir, session, "subagents", "agent-1.jsonl"),
            Line(new { type = "assistant", uuid = "x1", message = new { role = "assistant", content = "sub" } }) + "\n", writtenUtc);
        AiSandbox.Put(Path.Combine(dir, session, "tool-results", "big-output.txt"), new string('x', 2000), writtenUtc);
        return file;
    }

    /// <summary>The files around the transcripts that PC Tidy must never touch.</summary>
    public static IReadOnlyList<string> ClaudeConfigFiles(string claudeDir) => new[]
    {
        AiSandbox.Put(Path.Combine(claudeDir, "settings.json"), "{ \"cleanupPeriodDays\": 30 }"),
        AiSandbox.Put(Path.Combine(claudeDir, ".credentials.json"), "{ \"note\": \"made-up test file\" }"),
        AiSandbox.Put(Path.Combine(claudeDir, "CLAUDE.md"), "# memory (made up)\n"),
    };

    public static string CodexRollout(string codexDir, DateTime writtenUtc, string id = CodexSession)
    {
        var lines = new[]
        {
            Line(new { timestamp = "2026-09-14T10:00:00Z", type = "session_meta", payload = new { id, timestamp = "2026-09-14T10:00:00Z", cwd = @"C:\Users\tester\source\parser" } }),
            Line(new { timestamp = "2026-09-14T10:00:01Z", type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<environment_context>cwd</environment_context>" } } } }),
            Line(new { timestamp = "2026-09-14T10:00:02Z", type = "turn_context", payload = new { cwd = @"C:\Users\tester\source\parser", model = "gpt-test-codex" } }),
            Line(new { timestamp = "2026-09-14T10:00:03Z", type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Split the parser into two files" } } } }),
            Line(new { timestamp = "2026-09-14T10:00:04Z", type = "event_msg", payload = new { type = "user_message", message = "Split the parser into two files" } }),
            Line(new { timestamp = "2026-09-14T10:00:05Z", type = "response_item", payload = new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "Looking at parser.rs" } } } }),
            Line(new { timestamp = "2026-09-14T10:00:06Z", type = "response_item", payload = new { type = "function_call", name = "shell", arguments = "{\"command\":[\"ls\"]}" } }),
            Line(new { timestamp = "2026-09-14T10:00:07Z", type = "response_item", payload = new { type = "function_call_output", output = "parser.rs\nmain.rs" } }),
            Line(new { timestamp = "2026-09-14T10:00:08Z", type = "response_item", payload = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Done: lexer.rs and parser.rs." } } } }),
            Line(new { timestamp = "2026-09-14T10:00:09Z", type = "future_thing", payload = new { x = 1 } }),
        };
        var dir = Path.Combine(codexDir, "sessions", "2026", "09", "14");
        return AiSandbox.Put(Path.Combine(dir, $"rollout-2026-09-14T10-00-00-{id}.jsonl"), string.Join("\n", lines) + "\n", writtenUtc);
    }

    /// <summary>An older Codex rollout: items without the payload wrapper, compressed with zstd.</summary>
    public static string CodexOldCompressed(string codexDir, DateTime writtenUtc, string id = CodexOldSession)
    {
        var lines = new[]
        {
            Line(new { id, timestamp = "2025-05-07T17:24:21Z", instructions = "(made-up instructions)" }),
            Line(new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Write a haiku about disks" } } }),
            Line(new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Spinning platters hum" } } }),
        };
        using var compressor = new ZstdSharp.Compressor();
        var data = compressor.Wrap(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")).ToArray();
        var dir = Path.Combine(codexDir, "sessions", "2025", "05", "07");
        return AiSandbox.Put(Path.Combine(dir, $"rollout-2025-05-07T17-24-21-{id}.jsonl.zst"), data, writtenUtc);
    }

    public static string CodexIndex(string codexDir, params (string Id, string Title)[] titles)
        => AiSandbox.Put(Path.Combine(codexDir, "session_index.jsonl"), string.Join("\n", titles.Select(t => Line(new { id = t.Id, thread_name = t.Title }))) + "\n");
}
