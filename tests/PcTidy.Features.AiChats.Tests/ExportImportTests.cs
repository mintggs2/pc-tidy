using System.Text.Json.Nodes;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Model;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>ChatGPT and Claude.ai export zips (made-up fixtures in testbed\aichats).</summary>
public class ExportImportTests
{
    private static async Task<List<ImportedChat>> Read(string path, ImportReport? report = null)
    {
        var list = new List<ImportedChat>();
        await foreach (var c in ExportImporter.ReadAsync(path, report ?? new ImportReport(), CancellationToken.None)) list.Add(c);
        return list;
    }

    [Fact]
    public async Task ChatGpt_visible_branch_is_rebuilt_from_current_node_and_alternates_are_kept()
    {
        using var t = new TempFolder("branch");
        var zip = FakeExports.Zip(Path.Combine(t.Path, "chatgpt.zip"),
            ("conversations.json", FakeExports.Json(FakeExports.BranchyChat())),
            ("chat.html", "<html></html>"), ("user.json", "{}"));

        var chat = Assert.Single(await Read(zip));
        Assert.Equal(ChatSource.ChatGpt, chat.Source);
        Assert.Equal("Baking bread at home", chat.Title);
        Assert.Equal("gpt-4o", chat.Model);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant }, chat.Messages.Select(m => m.Role));
        Assert.Equal("How do I bake bread?", chat.Messages[0].Text);
        Assert.Equal("Use flour, water, salt and yeast.", chat.Messages[1].Text); // the regenerated answer is the visible one
        Assert.Contains("```python", chat.Messages[3].Text);
        Assert.Contains("starter ready", chat.Messages[4].Text);
        Assert.Contains("```data", chat.Messages[5].Text); // unknown content type shown as raw data
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("helpful assistant") || m.Text.Contains("likes baking"));

        Assert.Equal(2, chat.Alternates.Count);
        var cake = chat.Alternates.Single(b => b.Messages[0].Text.Contains("cake"));
        Assert.Equal(0, cake.ForkAt);
        Assert.Equal(2, cake.Messages.Count);
        var firstAnswer = chat.Alternates.Single(b => b.Messages[0].Text == "Use flour.");
        Assert.Equal(1, firstAnswer.ForkAt);
    }

    [Fact]
    public void Without_current_node_the_newest_branch_is_followed()
    {
        var conv = FakeExports.BranchyChat();
        var nodes = ((JsonObject)conv["mapping"]!).ToDictionary(kv => kv.Key, kv => System.Text.Json.JsonDocument.Parse(kv.Value!.ToJsonString()).RootElement);
        var path = ChatGptParser.VisiblePath(nodes, currentNode: "missing");
        Assert.Equal(new[] { "root", "sys", "ctx", "u1", "a1b", "u2", "a2", "a3", "a4" }, path);
    }

    [Fact]
    public async Task ChatGpt_split_files_are_read_in_manifest_order_and_a_chat_in_two_files_counts_once()
    {
        using var t = new TempFolder("split");
        var a = FakeExports.SimpleChat("aaaa0000-0000-4000-8000-000000000001", "Tomatoes", "When do I plant tomatoes?", "In spring.");
        var b = FakeExports.SimpleChat("bbbb0000-0000-4000-8000-000000000002", "Roses", "How to prune roses?", "In late winter.");
        var c = FakeExports.SimpleChat("cccc0000-0000-4000-8000-000000000003", "Compost", "What goes in compost?", "Greens and browns.");
        var zip = FakeExports.Zip(Path.Combine(t.Path, "split.zip"),
            ("conversations-000.json", FakeExports.Json(a, b)),
            ("conversations-001.json", FakeExports.Json(c, a)),
            ("export_manifest.json", "{\"files\":[\"conversations-001.json\",\"conversations-000.json\"]}"));

        var report = new ImportReport();
        var chats = await Read(zip, report);
        Assert.Equal(new[] { "Compost", "Tomatoes", "Roses" }, chats.Select(x => x.Title));
        Assert.Equal(3, report.Chats);
        Assert.Equal(0, report.SkippedChats);
    }

    [Fact]
    public async Task ChatGpt_image_attachments_are_found_in_the_zip_by_file_id()
    {
        using var t = new TempFolder("attach");
        var conv = FakeExports.SimpleChat("dddd0000-0000-4000-8000-000000000004", "What plant is this", "What is this plant?", "A fern.", imageFileId: "file-AbC123xyz");
        var zip = FakeExports.ZipBytes(Path.Combine(t.Path, "img.zip"),
            ("conversations.json", System.Text.Encoding.UTF8.GetBytes(FakeExports.Json(conv))),
            ("file-AbC123xyz-fern.png", new byte[] { 1, 2, 3, 4 }));

        var chat = Assert.Single(await Read(zip));
        var att = Assert.Single(chat.Attachments);
        Assert.Equal("fern.png", att.Name);
        Assert.Equal("file-AbC123xyz-fern.png", att.ArchiveEntry);
        Assert.Contains("[image: fern.png]", chat.Messages[0].Text);
    }

    [Fact]
    public async Task An_extracted_export_folder_works_too_and_attachments_point_at_the_real_file()
    {
        using var t = new TempFolder("folder");
        var conv = FakeExports.SimpleChat("eeee0000-0000-4000-8000-000000000005", "Fern", "What is this plant?", "A fern.", imageFileId: "file-Zz9");
        var folder = Path.Combine(t.Path, "export");
        AiSandbox.Put(Path.Combine(folder, "conversations.json"), FakeExports.Json(conv));
        var image = AiSandbox.Put(Path.Combine(folder, "file-Zz9-leaf.jpg"), new byte[] { 9, 9 });

        var chat = Assert.Single(await Read(folder));
        Assert.Equal(OriginKind.ExportFolder, chat.OriginKind);
        Assert.Equal(image, Assert.Single(chat.Attachments).FilePath);
    }

    [Fact]
    public async Task A_damaged_conversations_file_keeps_the_chats_before_the_damage()
    {
        using var t = new TempFolder("damaged");
        var good = FakeExports.Json(FakeExports.SimpleChat("ffff0000-0000-4000-8000-000000000006", "Kept", "Q?", "A."));
        var zip = FakeExports.Zip(Path.Combine(t.Path, "bad.zip"), ("conversations.json", good.TrimEnd(']') + ", {\"title\": \"broken\", \"mapping\": {"));
        var report = new ImportReport();
        var chats = await Read(zip, report);
        Assert.Equal("Kept", Assert.Single(chats).Title);
        Assert.Contains(report.Notes, n => n.Contains("damaged"));
    }

    [Fact]
    public async Task Claude_export_reads_messages_in_time_order_with_projects_blocks_and_attachments()
    {
        using var t = new TempFolder("claude");
        var chat1 = FakeExports.ClaudeChat("1a1a1a1a-0000-4000-8000-000000000001", "Raised beds", "p-1",
            ("human", "How deep should a raised bed be?", 1), ("assistant", "About 30 cm.", 3), ("human", "And the width?", 2));
        // Richer content blocks and an attachment with extracted text.
        var msgs = (JsonArray)chat1["chat_messages"]!;
        ((JsonObject)msgs[1]!)["content"] = new JsonArray(
            new JsonObject { ["type"] = "thinking", ["thinking"] = "Depth depends on roots." },
            new JsonObject { ["type"] = "text", ["text"] = "About 30 cm." },
            new JsonObject { ["type"] = "tool_use", ["name"] = "web_search", ["input"] = new JsonObject { ["q"] = "raised bed depth" } });
        ((JsonObject)msgs[0]!)["attachments"] = new JsonArray(new JsonObject { ["file_name"] = "plan.txt", ["file_size"] = 12, ["extracted_content"] = "bed 1: carrots" });
        var chat2 = FakeExports.ClaudeChat("2b2b2b2b-0000-4000-8000-000000000002", "", null, ("human", "Hello there, what is a good name for a cat?", 5), ("assistant", "Miso.", 6));
        var zip = FakeExports.Zip(Path.Combine(t.Path, "claude.zip"),
            ("conversations.json", FakeExports.Json(chat1, chat2)),
            ("projects.json", "[{\"uuid\":\"p-1\",\"name\":\"Garden plans\",\"docs\":[]}]"),
            ("users.json", "[]"));

        var chats = await Read(zip);
        Assert.Equal(2, chats.Count);
        var beds = chats[0];
        Assert.Equal(ChatSource.ClaudeWeb, beds.Source);
        Assert.Equal("Garden plans", beds.Project);
        Assert.Equal(3, beds.Messages.Count);
        Assert.StartsWith("How deep", beds.Messages[0].Text);           // minute 1
        Assert.Contains("[attached: plan.txt]", beds.Messages[0].Text);
        Assert.Equal("And the width?", beds.Messages[1].Text);           // minute 2 (it was last in the file)
        Assert.StartsWith("*Thinking:*", beds.Messages[2].Text);         // minute 3
        Assert.Contains("About 30 cm.", beds.Messages[2].Text);
        Assert.Contains("**Tool:** `web_search`", beds.Messages[2].Text);
        var att = Assert.Single(beds.Attachments);
        Assert.Equal("bed 1: carrots", att.ExtractedText);
        Assert.Equal("Hello there, what is a good name for a cat?", chats[1].Title); // untitled: first question becomes the title
    }

    [Fact]
    public void Something_that_isnt_an_export_is_recognised()
    {
        using var t = new TempFolder("notexport");
        var zip = FakeExports.Zip(Path.Combine(t.Path, "photos.zip"), ("a.txt", "hello"));
        Assert.False(ExportImporter.LooksLikeExport(zip));
        var notZip = AiSandbox.Put(Path.Combine(t.Path, "x.zip"), "not a zip");
        Assert.False(ExportImporter.LooksLikeExport(notZip));
    }
}
