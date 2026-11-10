using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Archive;
using PcTidy.Features.AiChats.Local;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>"Archive a copy" plans (CopyPath, originals stay), the separate remove-originals step, and the scanner.</summary>
public class ArchiveAndScannerTests
{
    [Fact]
    public async Task Copy_plan_copies_transcripts_and_subagent_chats_and_skips_live_sessions()
    {
        using var s = new AiSandbox("arch-copy");
        var old = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-20));
        FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddMinutes(-3), session: "22222222-2222-4333-8444-555555555555");
        var codex = FakeAgents.CodexOldCompressed(s.K.CodexDir, AiSandbox.Now.AddDays(-400));
        FakeAgents.ClaudeConfigFiles(s.K.ClaudeDir);
        var archive = Path.Combine(s.Root, "D", "Chat archive");

        var sessions = await AgentChatInventory.ListAsync(s.K, CancellationToken.None);
        var plan = AgentArchivePlanner.PlanCopy(sessions, archive, s.Live(), new Dictionary<string, string> { [sessions.Single(x => x.SessionId == FakeAgents.ClaudeSession).LibraryId] = "# readable" }, CancellationToken.None);

        Assert.Equal(1, plan.SkippedLive);
        Assert.Equal(2, plan.Sessions);
        var copies = plan.Items.Where(i => i.Kind == ActionKind.CopyPath).ToList();
        Assert.Equal(3, copies.Count); // transcript, its subagent transcript, the codex rollout
        var transcriptCopy = copies.Single(c => c.SourcePath == old);
        Assert.Equal(Path.Combine(archive, "Claude Code", "my-app", Path.GetFileName(old)), transcriptCopy.DestinationPath);
        Assert.Equal(new FileInfo(old).Length, transcriptCopy.ExpectedSize);
        Assert.Contains(copies, c => c.DestinationPath == Path.Combine(archive, "Claude Code", "my-app", FakeAgents.ClaudeSession, "subagents", "agent-1.jsonl"));
        Assert.Contains(copies, c => c.SourcePath == codex && c.DestinationPath!.EndsWith(".jsonl.zst"));
        Assert.All(copies, c => Assert.True(c.SourcePath!.EndsWith(".jsonl") || c.SourcePath.EndsWith(".jsonl.zst")));
        Assert.DoesNotContain(plan.Items, i => i.Kind == ActionKind.QuarantinePath || i.Kind == ActionKind.MovePath);
        Assert.Contains(plan.Items, i => i.Kind == ActionKind.CreateDirectory && i.DestinationPath == Path.Combine(archive, "Claude Code", "my-app"));
        Assert.Contains(plan.Items, i => i.Kind == ActionKind.WriteTextFile && i.DestinationPath == Path.Combine(archive, "Claude Code", "my-app", FakeAgents.ClaudeSession + ".md"));
        Assert.Contains(plan.Notes, n => n.Contains("Big tool outputs"));

        // Normalised plan: folders first, every copy after its folder.
        var ordered = plan.ToActionPlan("Archive").Items;
        Assert.True(ordered.TakeWhile(i => i.Kind == ActionKind.CreateDirectory).Count() == ordered.Count(i => i.Kind == ActionKind.CreateDirectory));
    }

    [Fact]
    public async Task Originals_are_offered_for_quarantine_only_after_a_byte_identical_copy_and_never_when_live()
    {
        using var s = new AiSandbox("arch-remove");
        var transcript = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-20));
        var archive = Path.Combine(s.Root, "D", "Chat archive");
        var session = Assert.Single(await AgentChatInventory.ClaudeAsync(s.K, CancellationToken.None));

        var none = AgentArchivePlanner.PlanRemoveOriginals(new[] { session }, archive, s.Live(), CancellationToken.None);
        Assert.Empty(none.Items);
        Assert.Contains(none.Notes, n => n.Contains("Archive a copy first"));

        // Copy by hand the way the engine would (transcript + subagent transcript) and check again.
        AiSandbox.Put(AgentArchivePlanner.CopyPathFor(archive, session), File.ReadAllBytes(transcript));
        var sub = session.SidecarTranscripts[0];
        AiSandbox.Put(AgentArchivePlanner.SidecarCopyPath(archive, session, sub), File.ReadAllBytes(sub.Path));
        Assert.True(AgentArchivePlanner.VerifyCopy(archive, session, CancellationToken.None).Verified);

        var plan = AgentArchivePlanner.PlanRemoveOriginals(new[] { session }, archive, s.Live(), CancellationToken.None);
        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i => Assert.Equal(ActionKind.QuarantinePath, i.Kind));
        Assert.Contains(plan.Items, i => i.SourcePath == transcript && i.Description.Contains("--resume"));

        // A copy that differs by one byte doesn't count.
        AiSandbox.Put(AgentArchivePlanner.CopyPathFor(archive, session), File.ReadAllBytes(transcript).Append((byte)'\n').ToArray());
        Assert.False(AgentArchivePlanner.VerifyCopy(archive, session, CancellationToken.None).Verified);

        // Live: never offered, even with a good copy.
        AiSandbox.Put(AgentArchivePlanner.CopyPathFor(archive, session), File.ReadAllBytes(transcript));
        AiSandbox.Put(Path.Combine(s.K.ClaudeDir, "sessions", "900.json"), FakeAgents.Line(new { pid = 900, sessionId = FakeAgents.ClaudeSession }));
        s.Processes.Running[900] = new ProcessState(null);
        var live = AgentArchivePlanner.PlanRemoveOriginals(new[] { session }, archive, s.Live(), CancellationToken.None);
        Assert.Empty(live.Items);
        Assert.Equal(1, live.SkippedLive);
    }

    [Fact]
    public async Task The_transcript_item_expects_the_transcripts_own_time_even_when_a_subagent_file_is_newer()
    {
        // AIC-2: the session's newest time comes from its subagent file; the transcript's item must not expect that time,
        // or the engine would skip it as "changed since the scan" every time.
        using var s = new AiSandbox("arch-times");
        var transcript = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-20));
        var first = Assert.Single(await AgentChatInventory.ClaudeAsync(s.K, CancellationToken.None));
        File.SetLastWriteTimeUtc(first.SidecarTranscripts[0].Path, AiSandbox.Now.AddDays(-15));
        var session = Assert.Single(await AgentChatInventory.ClaudeAsync(s.K, CancellationToken.None));
        var own = File.GetLastWriteTimeUtc(transcript);
        Assert.True(session.LastWriteUtc > own);
        Assert.Equal(own, session.TranscriptWriteUtc);

        var archive = Path.Combine(s.Root, "D", "Chat archive");
        var copy = AgentArchivePlanner.PlanCopy(new[] { session }, archive, s.Live(), null, CancellationToken.None);
        Assert.Equal(own, copy.Items.Single(i => i.Kind == ActionKind.CopyPath && i.SourcePath == transcript).ExpectedLastWriteUtc);

        AiSandbox.Put(AgentArchivePlanner.CopyPathFor(archive, session), File.ReadAllBytes(transcript));
        var sub = session.SidecarTranscripts[0];
        AiSandbox.Put(AgentArchivePlanner.SidecarCopyPath(archive, session, sub), File.ReadAllBytes(sub.Path));
        var remove = AgentArchivePlanner.PlanRemoveOriginals(new[] { session }, archive, s.Live(), CancellationToken.None);
        Assert.Equal(own, remove.Items.Single(i => i.SourcePath == transcript).ExpectedLastWriteUtc);
        Assert.Equal(sub.LastWriteUtc, remove.Items.Single(i => i.SourcePath == sub.Path).ExpectedLastWriteUtc);
    }

    [Fact]
    public async Task Scanner_with_the_real_safety_guard_offers_archive_copies_and_never_touches_tool_settings()
    {
        using var s = new AiSandbox("scan", SandboxProfile.Default);
        var transcript = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-20));
        FakeAgents.CodexRollout(s.K.CodexDir, AiSandbox.Now.AddDays(-5));
        var config = FakeAgents.ClaudeConfigFiles(s.K.ClaudeDir);
        var archive = Path.Combine(s.Root, "D", "Chat archive");

        // The real guard: transcripts are reachable, the tools' settings/credentials/memory never are.
        Assert.NotEqual(PathProtection.Never, s.Host.Safety.ClassifyPath(transcript).Protection);
        foreach (var file in config) Assert.Equal(PathProtection.Never, s.Host.Safety.ClassifyPath(file).Protection);

        var scanner = new AiChatsScanner();
        Assert.False(scanner.IncludedInOptimizeAll);
        var withoutFolder = await scanner.ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Equal(2, withoutFolder.Groups.Count);
        Assert.All(withoutFolder.AllItems, i => Assert.NotNull(i.DisabledReason));
        Assert.Contains(withoutFolder.Groups, g => g.Description!.Contains("30 days"));

        var result = await scanner.ScanAsync(s.Context(new() { [AiChatsOptions.ArchiveFolder] = archive }), null, CancellationToken.None);
        var items = result.AllItems.ToList();
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.All(items, i => Assert.False(i.DefaultSelected));
        var claude = items.Single(i => i.OwnerApp == "Claude Code");
        Assert.Null(claude.DisabledReason);
        Assert.Equal("my-app", claude.Title);
        Assert.Equal(claude.Actions.Where(a => a.Kind == ActionKind.CopyPath).Sum(a => a.ExpectedSize), claude.SizeBytes);
        var actions = items.SelectMany(i => i.Actions).ToList();
        Assert.DoesNotContain(actions, a => config.Contains(a.SourcePath ?? "") || config.Contains(a.DestinationPath ?? ""));
        Assert.All(actions.Where(a => a.Kind == ActionKind.CopyPath), a => Assert.Equal(PathProtection.None, s.Host.Safety.ClassifyPath(a.DestinationPath!).Protection));
        foreach (var a in actions.Where(a => a.Kind is ActionKind.CreateDirectory))
            Assert.True(s.Host.Safety.Check(a).Allowed, a.Description);
    }

    [Fact]
    public async Task Scanner_on_a_pc_without_any_agent_tools_says_so_calmly()
    {
        using var s = new AiSandbox("scan-none");
        var result = await new AiChatsScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Empty(result.Groups);
        Assert.Contains(result.Notes, n => n.Contains("No Claude Code or Codex chats"));
    }
}
