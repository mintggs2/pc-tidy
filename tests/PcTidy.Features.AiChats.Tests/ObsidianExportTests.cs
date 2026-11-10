using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Export;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>Export plans: frontmatter, wikilinks, attachments, and the rule that only PC Tidy's own notes are ever updated.</summary>
public class ObsidianExportTests
{
    private static ImportedChat Chat(string id, string title, string q, string a, ChatSource source = ChatSource.ChatGpt, string? project = null,
        IReadOnlyList<ChatAttachment>? attachments = null) => new()
    {
        Source = source,
        SourceId = id,
        Title = title,
        CreatedUtc = new DateTime(2026, 5, 4, 9, 30, 0, DateTimeKind.Utc),
        UpdatedUtc = new DateTime(2026, 5, 5, 9, 30, 0, DateTimeKind.Utc),
        Model = "gpt-4o",
        Project = project,
        Messages = new[] { new ChatMessage(ChatRole.User, q), new ChatMessage(ChatRole.Assistant, a) },
        Attachments = attachments ?? Array.Empty<ChatAttachment>(),
        OriginKind = OriginKind.ExportZip,
        Origin = @"C:\nowhere\export.zip",
    };

    private sealed class Setup : IDisposable
    {
        public TempFolder Temp { get; } = new("export");
        public ChatLibraryStore Store { get; }
        public string Vault => Path.Combine(Temp.Path, "Vault", "AI chats");

        public Setup() => Store = new ChatLibraryStore(Path.Combine(Temp.Path, "Data"));

        public async Task Add(params ImportedChat[] chats)
        {
            await Store.UpsertAsync(chats.Select(c => ChatIngest.Prepare(c, SecretMasker.Default)).ToList(), CancellationToken.None);
            await Store.RelabelTopicsAsync(CancellationToken.None);
        }

        public Task<ExportPlan> Plan(params string[] ids)
            => new ObsidianExporter(Store).PlanAsync(new ExportRequest { Folder = Vault, ChatIds = ids }, CancellationToken.None);

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task New_notes_have_frontmatter_masked_text_and_the_pctidy_marker()
    {
        using var s = new Setup();
        var key = FakeSecrets.OpenAiProject;
        await s.Add(Chat("n1", "Sourdough: starter \"tips\"", "My starter smells odd, also my key is " + key, "That's normal in week one."));
        await s.Store.AddTagAsync("chatgpt-n1", "baking", CancellationToken.None);

        var plan = await s.Plan("chatgpt-n1");
        var item = Assert.Single(plan.Items);
        Assert.Equal(ActionKind.WriteTextFile, item.Kind);
        Assert.Equal(NameClashPolicy.Skip, item.ClashPolicy);
        Assert.Equal(Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Sourdough starter tips.md"), item.DestinationPath);
        var note = item.Args!["content"];
        Assert.StartsWith("---\n", note);
        Assert.Contains("title: \"Sourdough: starter \\\"tips\\\"\"\n", note);
        Assert.Contains("date: 2026-05-04\n", note);
        Assert.Contains("source: chatgpt\n", note);
        Assert.Contains("model: \"gpt-4o\"\n", note);
        Assert.Contains("  - ai-chat\n  - chatgpt\n", note);
        Assert.Contains("  - baking\n", note);
        Assert.Contains("pctidy-id: chatgpt-n1\n---\n", note);
        Assert.Contains("### You\n\nMy starter smells odd", note);
        Assert.Contains("sk-proj-…" + key[^4..], note);
        Assert.DoesNotContain(key, note);
        Assert.Equal(1, plan.NewNotes);
    }

    [Fact]
    public async Task A_file_PC_Tidy_did_not_write_is_never_overwritten_its_name_is_avoided()
    {
        using var s = new Setup();
        await s.Add(Chat("o1", "Roses", "How to prune roses?", "In late winter."));
        var foreign = AiSandbox.Put(Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Roses.md"), "# My own rose notes\n");
        var otherTool = AiSandbox.Put(Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Roses (2).md"), "---\npctidy-id: chatgpt-someone-else\n---\nnot this chat\n");

        var plan = await s.Plan("chatgpt-o1");
        var item = Assert.Single(plan.Items);
        Assert.Equal(ActionKind.WriteTextFile, item.Kind);
        Assert.Equal(Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Roses (3).md"), item.DestinationPath);
        Assert.DoesNotContain(plan.Items, i => i.SourcePath == foreign || i.SourcePath == otherTool || i.DestinationPath == foreign || i.DestinationPath == otherTool);
    }

    [Fact]
    public async Task A_note_PC_Tidy_wrote_before_is_updated_in_place_or_left_alone_when_unchanged()
    {
        using var s = new Setup();
        await s.Add(Chat("u1", "Compost", "What goes in compost?", "Greens and browns."));
        var first = Assert.Single((await s.Plan("chatgpt-u1")).Items);
        // The user moved the note to another folder of the vault after the first export: it's still found by its id.
        var moved = AiSandbox.Put(Path.Combine(s.Vault, "Garden", "compost chat.md"), first.Args!["content"]);

        var again = await s.Plan("chatgpt-u1");
        Assert.Empty(again.Items);
        Assert.Equal(1, again.UnchangedNotes);

        await s.Add(Chat("u1", "Compost", "What goes in compost?", "Greens, browns and air."));
        var update = Assert.Single((await s.Plan("chatgpt-u1")).Items);
        Assert.Equal(ActionKind.EditTextFile, update.Kind);
        Assert.Equal(moved, update.SourcePath);
        Assert.Contains("Greens, browns and air.", update.Args!["content"]);
        Assert.Equal(File.GetLastWriteTimeUtc(moved), update.ExpectedLastWriteUtc);
    }

    [Fact]
    public async Task Related_chats_are_linked_with_wikilinks_and_agent_chats_go_by_project()
    {
        using var s = new Setup();
        await s.Add(
            Chat("w1", "Python list sorting", "How do I sort a python list by key?", "Use sorted(items, key=...)."),
            Chat("w2", "Python sorting stability", "Is python sorting stable?", "Yes, sorted() is stable."),
            Chat("w3", "Train times", "When is the next train?", "Check the timetable."),
            Chat("cc1", "Fix login", "Login button broken", "Fixed the handler.", ChatSource.ClaudeCode, @"C:\Users\tester\source\my-app"));

        var plan = await s.Plan("chatgpt-w1", "chatgpt-w2", "chatgpt-w3", "claude-code-cc1");
        Assert.Equal(4, plan.NewNotes);
        string NoteOf(string title) => plan.Items.Single(i => i.DestinationPath!.Contains(title)).Args!["content"];
        Assert.Contains("[[2026-05-04 Python sorting stability]]", NoteOf("Python list sorting"));
        Assert.Contains("[[2026-05-04 Python list sorting]]", NoteOf("Python sorting stability"));
        Assert.DoesNotContain("**Related:**", NoteOf("Train times"));
        var agent = plan.Items.Single(i => i.DestinationPath!.Contains("Fix login"));
        Assert.Equal(Path.Combine(s.Vault, "Claude Code", "my-app", "2026-05-04 Fix login.md"), agent.DestinationPath);
        Assert.Contains("project: \"my-app\"", agent.Args!["content"]);
    }

    [Fact]
    public async Task Attachments_go_beside_the_note_text_as_files_folder_files_copied_zip_files_named()
    {
        using var s = new Setup();
        var picture = AiSandbox.Put(Path.Combine(s.Temp.Path, "export-folder", "file-1-leaf.png"), new byte[] { 1, 2, 3 });
        await s.Add(Chat("a1", "Plants", "Look at these", "Nice.", attachments: new[]
        {
            new ChatAttachment("notes.docx", 100, ExtractedText: "carrots, beans"),
            new ChatAttachment("leaf.png", 3, FilePath: picture),
            new ChatAttachment("in-zip.png", 5, ArchiveEntry: "file-2-in-zip.png"),
        }));

        var plan = await s.Plan("chatgpt-a1");
        var dir = Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Plants files");
        var text = plan.Items.Single(i => i.Kind == ActionKind.WriteTextFile && i.DestinationPath!.StartsWith(dir));
        Assert.Equal(Path.Combine(dir, "notes.docx.txt"), text.DestinationPath);
        Assert.Equal("carrots, beans", text.Args!["content"]);
        var copy = plan.Items.Single(i => i.Kind == ActionKind.CopyPath);
        Assert.Equal(picture, copy.SourcePath);
        Assert.Equal(Path.Combine(dir, "leaf.png"), copy.DestinationPath);
        Assert.Contains(plan.Items, i => i.Kind == ActionKind.CreateDirectory && i.DestinationPath == dir);
        var note = plan.Items.Single(i => i.DestinationPath == Path.Combine(s.Vault, "ChatGPT", "2026-05-04 Plants.md")).Args!["content"];
        Assert.Contains("![[2026-05-04 Plants files/leaf.png]]", note);
        Assert.Contains("[[2026-05-04 Plants files/notes.docx.txt]]", note);
        Assert.Contains("in-zip.png (in your export zip)", note);
        Assert.Equal(2, plan.AttachmentFiles);
    }

    [Fact]
    public void Odd_titles_make_safe_note_names()
    {
        Assert.Equal("a b c d", NoteNames.Safe("a/b|c#d"));
        Assert.Equal("_CON", NoteNames.Safe("CON"));
        Assert.Equal("Untitled chat", NoteNames.Safe(" ... "));
        Assert.Equal("trailing", NoteNames.Safe("trailing. . "));
        Assert.True(NoteNames.Safe(new string('x', 500)).Length <= 100);
    }

    [Fact]
    public void Marker_is_only_read_from_a_leading_frontmatter_block()
    {
        Assert.Equal("chatgpt-1", ExistingNotes.MarkerId("---\ntitle: x\npctidy-id: chatgpt-1\n---\nbody"));
        Assert.Null(ExistingNotes.MarkerId("# note\npctidy-id: chatgpt-1\n"));
        Assert.Null(ExistingNotes.MarkerId("---\ntitle: x\n---\npctidy-id: chatgpt-1\n"));
    }
}
