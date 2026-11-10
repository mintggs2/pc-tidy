using System.Text;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>The SQLite library: masked-only storage and search, user labels kept on re-import, the special views.</summary>
public class LibraryStoreTests
{
    private static ImportedChat Chat(string id, string title, params string[] texts) => new()
    {
        Source = ChatSource.ChatGpt,
        SourceId = id,
        Title = title,
        CreatedUtc = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(id.Length),
        Messages = texts.Select((t, i) => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, t)).ToList(),
        OriginKind = OriginKind.ExportZip,
        Origin = @"C:\nowhere\export.zip",
    };

    private static async Task<ChatLibraryStore> Store(TempFolder t, params ImportedChat[] chats)
    {
        var store = new ChatLibraryStore(t.Path);
        await store.UpsertAsync(chats.Select(c => ChatIngest.Prepare(c, SecretMasker.Default)).ToList(), CancellationToken.None);
        await store.RelabelTopicsAsync(CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task The_database_and_search_index_only_ever_hold_masked_secrets()
    {
        using var t = new TempFolder("store-mask");
        var key = FakeSecrets.Anthropic;
        var store = await Store(t, Chat("s1", "Deploy script", "Here is my key " + key + " for the deploy", "Thanks, rotate it later."));

        var bytes = File.ReadAllBytes(store.DatabasePath);
        Assert.DoesNotContain(key[10..40], Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(key[10..40], Encoding.Unicode.GetString(bytes));

        Assert.Single(await store.QueryAsync(new LibraryQuery { Text = "deploy" }, CancellationToken.None));
        Assert.Empty(await store.QueryAsync(new LibraryQuery { Text = key[7..30] }, CancellationToken.None));
        var detail = await store.GetAsync("chatgpt-s1", CancellationToken.None);
        Assert.Contains("sk-ant-…" + key[^4..], detail!.Messages[0].Text);

        var report = Assert.Single(await store.SecretsAsync(liveOnly: true, CancellationToken.None));
        Assert.Equal("Anthropic API key", report.RuleTitle);
        Assert.Equal("sk-ant-…" + key[^4..], report.Masked);
        Assert.Contains("Rotate", report.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, report.ChatsWithSameKey);
    }

    [Fact]
    public async Task Counts_and_groups_cover_the_whole_library_not_just_the_newest_rows()
    {
        using var t = new TempFolder("store-counts");
        // 25 newer chats in project "Alpha", 5 older near-empty ones in "Beta". A list limited to 10 rows holds no Beta chat.
        var chats = Enumerable.Range(0, 25).Select(i => Chat($"a{i:00}", "Alpha " + i, "A long question about the alpha project number " + i, "A long answer " + new string('x', 300)) with
                { Project = "Alpha", CreatedUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i) })
            .Concat(Enumerable.Range(0, 5).Select(i => Chat($"b{i}", "Beta " + i, "hi") with
                { Project = "Beta", CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i) }))
            .ToArray();
        var store = await Store(t, chats);

        var beta = await store.QueryAsync(new LibraryQuery { Group = "Beta", Limit = 10 }, CancellationToken.None);
        Assert.Equal(5, beta.Count);
        Assert.Equal(new[] { "Beta 4", "Beta 3", "Beta 2", "Beta 1", "Beta 0" }, beta.Select(c => c.Title)); // newest first, as the list shows
        Assert.Equal(5, await store.CountAsync(LibraryView.NearEmpty, CancellationToken.None));
        var groups = await store.GroupsAsync(CancellationToken.None);
        Assert.Equal(25, groups.Single(g => g.Name == "Alpha").Chats);
        Assert.Equal(5, groups.Single(g => g.Name == "Beta").Chats);
    }

    [Fact]
    public async Task A_damaged_library_is_recognised_and_can_be_set_aside_for_a_new_one()
    {
        using var t = new TempFolder("store-damaged");
        var store = await Store(t, Chat("d1", "Before the power cut", "hello", "hi"));
        File.WriteAllBytes(store.DatabasePath, Encoding.ASCII.GetBytes(new string('#', 8192))); // not a database any more

        var fresh = new ChatLibraryStore(t.Path);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fresh.CountsAsync(CancellationToken.None));
        Assert.True(ChatLibraryStore.IsDamaged(ex), ex.ToString());

        var aside = await fresh.SetAsideDamagedAsync(CancellationToken.None);
        Assert.NotNull(aside);
        Assert.True(File.Exists(Path.Combine(fresh.Folder, aside!))); // kept, never deleted
        Assert.Empty(await fresh.CountsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reimport_updates_text_but_keeps_tags_pins_folders_and_topics()
    {
        using var t = new TempFolder("store-reimport");
        var store = await Store(t, Chat("r1", "Tomato care", "How often to water tomatoes?", "Daily in summer."));
        await store.SetPinnedAsync("chatgpt-r1", true, CancellationToken.None);
        await store.AddTagAsync("chatgpt-r1", "Garden Plans", CancellationToken.None);
        await store.SetFolderAsync("chatgpt-r1", "Hobbies", CancellationToken.None);
        await store.SetTopicAsync("chatgpt-r1", "Vegetables", CancellationToken.None);

        var added = await store.UpsertAsync(new[] { ChatIngest.Prepare(Chat("r1", "Tomato care", "How often to water tomatoes?", "Daily in summer.", "And in winter?"), SecretMasker.Default) }, CancellationToken.None);
        Assert.Equal(0, added);
        var s = Assert.Single(await store.QueryAsync(new LibraryQuery(), CancellationToken.None));
        Assert.Equal(3, s.MessageCount);
        Assert.True(s.Pinned);
        Assert.Equal(new[] { "garden-plans" }, s.Tags);
        Assert.Equal("Hobbies", s.Folder);
        Assert.Equal("Vegetables", s.Topic);
        Assert.Single(await store.QueryAsync(new LibraryQuery { Tag = "garden-plans" }, CancellationToken.None));
        Assert.Single(await store.QueryAsync(new LibraryQuery { Folder = "Hobbies" }, CancellationToken.None));
        Assert.Single(await store.QueryAsync(new LibraryQuery { View = LibraryView.Pinned }, CancellationToken.None));
    }

    [Fact]
    public async Task Biggest_near_empty_and_duplicate_views()
    {
        using var t = new TempFolder("store-views");
        var big = string.Join(" ", Enumerable.Repeat("compost layers need greens and browns", 400));
        const string roses = "Cut about five millimetres above an outward-facing bud, at a slight angle so rain runs off. Remove dead, damaged or crossing stems first. Late winter, before new growth starts, is the best time for most roses.";
        var store = await Store(t,
            Chat("big", "Compost guide", "Tell me everything about compost", big),
            Chat("tiny", "hi", "hi"),
            Chat("dup1", "Roses", "How to prune roses?", roses),
            Chat("dup2", "Roses (copy)", "How to prune roses?", roses.Replace(". ", ".  ")));

        var biggest = await store.QueryAsync(new LibraryQuery { View = LibraryView.Biggest }, CancellationToken.None);
        Assert.Equal("chatgpt-big", biggest[0].Id);
        var near = await store.QueryAsync(new LibraryQuery { View = LibraryView.NearEmpty }, CancellationToken.None);
        Assert.Equal("chatgpt-tiny", Assert.Single(near).Id);
        var dupes = await store.QueryAsync(new LibraryQuery { View = LibraryView.Duplicates }, CancellationToken.None);
        Assert.Equal(new[] { "chatgpt-dup1", "chatgpt-dup2" }, dupes.Select(d => d.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Offline_topics_group_chats_that_share_words()
    {
        using var t = new TempFolder("store-topics");
        var store = await Store(t,
            Chat("p1", "Python list sorting", "How do I sort a list in python?", "Use sorted()."),
            Chat("p2", "Python dictionaries", "Python dict comprehension example?", "Use {k: v for ...}."),
            Chat("g1", "Tomato garden", "Tomato plants are wilting in my garden", "Water them."),
            Chat("g2", "Garden beds", "Best soil for a raised garden bed?", "Loam."),
            Chat("x1", "Train times", "When is the next train to the coast?", "Check the timetable."));
        var all = await store.QueryAsync(new LibraryQuery(), CancellationToken.None);
        string TopicOf(string id) => all.Single(c => c.Id == "chatgpt-" + id).Topic!;
        Assert.Equal("Python", TopicOf("p1"));
        Assert.Equal("Python", TopicOf("p2"));
        Assert.Equal("Garden", TopicOf("g1"));
        Assert.Equal("Garden", TopicOf("g2"));
        Assert.Equal(TopicLabeler.OtherTopic, TopicOf("x1"));
        var groups = await store.GroupsAsync(CancellationToken.None);
        Assert.Contains(groups, g => g.Name == "Python" && g.Chats == 2);
        Assert.Equal(2, (await store.QueryAsync(new LibraryQuery { Group = "Garden" }, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Search_handles_odd_input_and_removing_a_chat_only_touches_the_library()
    {
        using var t = new TempFolder("store-search");
        var store = await Store(t, Chat("q1", "Quotes \"and\" stars*", "NEAR AND OR ( ) * ^ - all at once", "ok"));
        Assert.Single(await store.QueryAsync(new LibraryQuery { Text = "\"stars* AND (" }, CancellationToken.None));
        Assert.Single(await store.QueryAsync(new LibraryQuery { Text = "  ***  " }, CancellationToken.None)); // nothing to search = everything
        await store.RemoveAsync(new[] { "chatgpt-q1" }, CancellationToken.None);
        Assert.Empty(await store.QueryAsync(new LibraryQuery(), CancellationToken.None));
    }
}
