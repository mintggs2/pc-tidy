using PcTidy.Core;
using PcTidy.Features.AiChats.Import;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>Claude Code and Codex transcripts in a made-up .claude / .codex tree, and live-session detection.</summary>
public class AgentChatTests
{
    [Fact]
    public async Task Claude_Code_transcript_is_parsed_leniently_with_project_from_cwd()
    {
        using var s = new AiSandbox("cc-parse");
        var file = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-3));
        FakeAgents.ClaudeConfigFiles(s.K.ClaudeDir);

        var sessions = await AgentChatInventory.ClaudeAsync(s.K, CancellationToken.None);
        var session = Assert.Single(sessions);
        Assert.Equal(file, session.TranscriptPath);
        Assert.Equal(FakeAgents.ClaudeSession, session.SessionId);
        Assert.Equal(FakeAgents.ProjectCwd, session.Cwd);
        Assert.Equal("my-app", session.ProjectName);
        Assert.Single(session.SidecarTranscripts);
        Assert.True(session.TotalBytes > session.TranscriptBytes + session.SidecarTranscripts[0].Size); // the big tool output counts too

        var report = new ImportReport();
        var chat = await AgentTranscriptImporter.ReadAsync(session, null, report, CancellationToken.None);
        Assert.NotNull(chat);
        Assert.Equal("Fix the login button", chat!.Title);
        Assert.Equal(FakeAgents.ProjectCwd, chat.Project);
        Assert.Equal("claude-test-model", chat.Model);
        Assert.Equal(1, report.SkippedRecords); // the broken line
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant }, chat.Messages.Select(m => m.Role));
        Assert.Contains("**Tool:** `Bash`", chat.Messages[1].Text);
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("Subagent chatter") || m.Text.Contains("Caveat"));
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), chat.CreatedUtc);
    }

    [Fact]
    public async Task Inventory_lists_transcripts_only_never_settings_credentials_or_memory()
    {
        using var s = new AiSandbox("cc-only");
        FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-3));
        var config = FakeAgents.ClaudeConfigFiles(s.K.ClaudeDir);
        AiSandbox.Put(Path.Combine(s.K.CodexDir, "config.toml"), "model = \"made-up\"\n");
        AiSandbox.Put(Path.Combine(s.K.CodexDir, "auth.json"), "{}");
        FakeAgents.CodexRollout(s.K.CodexDir, AiSandbox.Now.AddDays(-2));

        var all = await AgentChatInventory.ListAsync(s.K, CancellationToken.None);
        var touched = all.SelectMany(x => x.SidecarTranscripts.Select(f => f.Path).Append(x.TranscriptPath)).ToList();
        Assert.All(touched, p => Assert.True(p.EndsWith(".jsonl") || p.EndsWith(".jsonl.zst"), p));
        Assert.DoesNotContain(touched, p => config.Contains(p) || p.EndsWith("config.toml") || p.EndsWith("auth.json"));
        Assert.All(touched, p => Assert.True(
            PathUtil.IsUnder(p, Path.Combine(s.K.ClaudeDir, "projects")) || PathUtil.IsUnder(p, Path.Combine(s.K.CodexDir, "sessions")), p));
    }

    [Fact]
    public async Task Codex_rollouts_plain_and_zstd_compressed_with_titles_from_the_session_index()
    {
        using var s = new AiSandbox("codex");
        FakeAgents.CodexRollout(s.K.CodexDir, AiSandbox.Now.AddDays(-2));
        FakeAgents.CodexOldCompressed(s.K.CodexDir, AiSandbox.Now.AddDays(-500));
        FakeAgents.CodexIndex(s.K.CodexDir, (FakeAgents.CodexSession, "Refactor the parser"));

        var sessions = await AgentChatInventory.CodexAsync(s.K, CancellationToken.None);
        Assert.Equal(2, sessions.Count);
        var titles = await AgentChatInventory.CodexTitlesAsync(s.K, CancellationToken.None);

        var modern = sessions.Single(x => x.SessionId == FakeAgents.CodexSession);
        var chat = (await AgentTranscriptImporter.ReadAsync(modern, titles, new ImportReport(), CancellationToken.None))!;
        Assert.Equal("Refactor the parser", chat.Title);
        Assert.Equal("gpt-test-codex", chat.Model);
        Assert.Equal("parser", AgentProjects.ShortName(chat.Project));
        Assert.Equal("Split the parser into two files", chat.Messages[0].Text); // environment context skipped, event_msg not doubled
        Assert.Single(chat.Messages, m => m.Text == "Split the parser into two files");
        Assert.Contains(chat.Messages, m => m.Role == ChatRole.Tool && m.Text.Contains("parser.rs"));
        Assert.Equal("Done: lexer.rs and parser.rs.", chat.Messages[^1].Text);

        var old = sessions.Single(x => x.TranscriptPath.EndsWith(".jsonl.zst"));
        Assert.Equal(FakeAgents.CodexOldSession, old.SessionId);
        var oldChat = (await AgentTranscriptImporter.ReadAsync(old, titles, new ImportReport(), CancellationToken.None))!;
        Assert.Equal("Write a haiku about disks", oldChat.Title);
        Assert.Equal(new[] { "Write a haiku about disks", "Spinning platters hum" }, oldChat.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task A_damaged_zstd_file_does_not_crash_the_import()
    {
        using var s = new AiSandbox("zst-bad");
        var dir = Path.Combine(s.K.CodexDir, "sessions", "2025", "01", "01");
        AiSandbox.Put(Path.Combine(dir, "rollout-2025-01-01T00-00-00-12345678-1234-4234-8234-123456789012.jsonl.zst"), new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3 });
        var session = Assert.Single(await AgentChatInventory.CodexAsync(s.K, CancellationToken.None));
        var report = new ImportReport();
        var chat = await AgentTranscriptImporter.ReadAsync(session, null, report, CancellationToken.None);
        Assert.NotNull(chat);
        Assert.Empty(chat!.Messages);
        Assert.True(report.SkippedRecords >= 1);
    }

    [Fact]
    public void Thread_is_rebuilt_from_parent_links_and_falls_back_to_file_order()
    {
        AgentTranscriptImporter.Record R(string u, string? p, string text) => new(u, p, new ChatMessage(ChatRole.User, text), null);
        // A rewind: "b-old" was answered, then the user went back and continued from "a".
        var chain = AgentTranscriptImporter.Thread(new[] { R("a", null, "1"), R("b-old", "a", "old"), R("b", "a", "2"), R("c", "b", "3") });
        Assert.Equal(new[] { "1", "2", "3" }, chain.Select(m => m.Text));
        // Parents that point at non-message records: file order.
        var broken = AgentTranscriptImporter.Thread(new[] { R("a", "x", "1"), R("b", "y", "2"), R("c", "z", "3") });
        Assert.Equal(new[] { "1", "2", "3" }, broken.Select(m => m.Text));
    }

    // ------------------------------------------------------------------ live sessions

    private static AgentSession Session(ChatSource source, string id, DateTime lastWrite, string? cwd = null) => new()
    {
        Source = source, TranscriptPath = @"C:\nowhere\" + id + ".jsonl", SessionId = id, Cwd = cwd, LastWriteUtc = lastWrite, CreatedUtc = lastWrite,
    };

    [Fact]
    public void Claude_session_file_with_a_running_process_and_matching_start_time_is_live()
    {
        using var s = new AiSandbox("live-pid");
        var started = AiSandbox.Now.AddHours(-2);
        AiSandbox.Put(Path.Combine(s.K.ClaudeDir, "sessions", "4242.json"),
            FakeAgents.Line(new { pid = 4242, sessionId = "live-one", cwd = FakeAgents.ProjectCwd, startedAt = (long)(started - DateTime.UnixEpoch).TotalMilliseconds }));
        AiSandbox.Put(Path.Combine(s.K.ClaudeDir, "sessions", "5151.json"),
            FakeAgents.Line(new { pid = 5151, sessionId = "reused-pid", startedAt = (long)(started - DateTime.UnixEpoch).TotalMilliseconds }));
        AiSandbox.Put(Path.Combine(s.K.ClaudeDir, "sessions", "6161.json"), FakeAgents.Line(new { pid = 6161, sessionId = "not-running" }));
        s.Processes.Running[4242] = new ProcessState(started.AddSeconds(1));
        s.Processes.Running[5151] = new ProcessState(started.AddHours(1)); // same PID number, different process

        var live = s.Live();
        var old = AiSandbox.Now.AddDays(-3);
        Assert.True(live.IsLive(Session(ChatSource.ClaudeCode, "live-one", old)));
        Assert.False(live.IsLive(Session(ChatSource.ClaudeCode, "reused-pid", old)));
        Assert.False(live.IsLive(Session(ChatSource.ClaudeCode, "not-running", old)));
        Assert.False(live.IsLive(Session(ChatSource.ClaudeCode, "something-else", old)));
    }

    [Fact]
    public void Running_process_with_unknown_start_time_or_unknown_session_errs_on_the_side_of_live()
    {
        using var s = new AiSandbox("live-unknown");
        AiSandbox.Put(Path.Combine(s.K.ClaudeDir, "sessions", "777.json"), FakeAgents.Line(new { pid = 777, cwd = FakeAgents.ProjectCwd }));
        s.Processes.Running[777] = new ProcessState(null);
        var live = s.Live();
        Assert.True(live.IsLive(Session(ChatSource.ClaudeCode, "x", AiSandbox.Now.AddHours(-3), FakeAgents.ProjectCwd)));
        Assert.False(live.IsLive(Session(ChatSource.ClaudeCode, "y", AiSandbox.Now.AddDays(-5), FakeAgents.ProjectCwd)));
    }

    [Fact]
    public void Recently_changed_files_and_codex_writer_locks_are_live()
    {
        using var s = new AiSandbox("live-misc");
        AiSandbox.Put(Path.Combine(s.K.CodexDir, "thread-writer-locks", FakeAgents.CodexSession + ".lock"), "");
        var live = s.Live();
        Assert.True(live.IsLive(Session(ChatSource.ClaudeCode, "fresh", AiSandbox.Now.AddMinutes(-9))));
        Assert.False(live.IsLive(Session(ChatSource.ClaudeCode, "settled", AiSandbox.Now.AddMinutes(-11))));
        Assert.True(live.IsLive(Session(ChatSource.Codex, FakeAgents.CodexSession, AiSandbox.Now.AddDays(-1))));
        Assert.False(live.IsLive(Session(ChatSource.Codex, FakeAgents.CodexOldSession, AiSandbox.Now.AddDays(-1))));
    }
}
