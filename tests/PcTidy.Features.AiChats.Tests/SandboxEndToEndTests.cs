using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Archive;
using PcTidy.Features.AiChats.Export;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.AiChats.Tests;

[CollectionDefinition("aichats-sandbox", DisableParallelization = true)]
public class AiChatsSandboxCollection { }

/// <summary>
/// Real composition (PcTidyHost.CreateSandbox: real SafetyGuard, Executor, Undo) over a made-up PC: import exports into
/// the library, export notes into an Obsidian folder, run the plan, then undo and prove the tree is identical.
/// </summary>
[Collection("aichats-sandbox")]
public class SandboxEndToEndTests(ITestOutputHelper log)
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    [Fact]
    public async Task Export_to_Obsidian_runs_through_the_engine_and_undo_leaves_the_tree_identical()
    {
        using var s = new AiSandbox("e2e-export", SandboxProfile.Default);
        var key = FakeSecrets.GitHubClassic;
        var chatgpt = FakeExports.Zip(Path.Combine(s.Inputs, "chatgpt-export.zip"),
            ("conversations.json", FakeExports.Json(
                FakeExports.BranchyChat(),
                FakeExports.SimpleChat("12340000-0000-4000-8000-000000000001", "Deploy token", "Is this token ok to share? " + key, "No: rotate it now."))));
        var claude = FakeExports.Zip(Path.Combine(s.Inputs, "claude-export.zip"),
            ("conversations.json", FakeExports.Json(FakeExports.ClaudeChat("5678abcd-0000-4000-8000-000000000002", "Bread hydration", null,
                ("human", "What hydration for a bread dough?", 1), ("assistant", "Around 70 percent for bread.", 2)))));
        var vault = Path.Combine(s.Root, "D", "Obsidian", "AI chats");
        var foreign = AiSandbox.Put(Path.Combine(vault, "ChatGPT", "2026-03-01 Baking bread at home.md"), "# My own notes, not PC Tidy's\n");

        var store = new ChatLibraryStore(s.Host.Settings.DataRoot);
        var importer = new LibraryImporter(store);
        Assert.Equal(2, (await importer.ImportExportAsync(chatgpt, null, CancellationToken.None)).Chats);
        Assert.Equal(1, (await importer.ImportExportAsync(claude, null, CancellationToken.None)).Chats);
        Assert.True(PathUtil.IsUnder(store.DatabasePath, Path.Combine(s.Host.Settings.DataRoot, "aichats")));

        var before = TestbedBuilder.Manifest(s.Root);
        var ids = (await store.QueryAsync(new LibraryQuery(), CancellationToken.None)).Select(c => c.Id).ToList();
        var plan = await new ObsidianExporter(store).PlanAsync(new ExportRequest { Folder = vault, ChatIds = ids }, CancellationToken.None);
        Assert.Equal(3, plan.NewNotes);
        foreach (var item in plan.Items)
        {
            var verdict = s.Host.Safety.Check(item);
            log.WriteLine($"{item.Kind} {item.DestinationPath} -> {(verdict.Allowed ? "ok" : verdict.Reason)}");
            Assert.True(verdict.Allowed, item.Description + ": " + verdict.Reason);
        }

        var run = await s.Host.Executor.ExecuteAsync(plan.ToActionPlan(), Real, null, CancellationToken.None);
        foreach (var step in run.Steps.Where(x => x.Status != StepStatus.Done)) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(plan.Items.Count, run.DoneCount);
        Assert.Equal("# My own notes, not PC Tidy's\n", File.ReadAllText(foreign)); // never overwritten
        var notes = Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories).Where(f => f != foreign).ToList();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Contains("pctidy-id: ", File.ReadAllText(n)));
        var all = string.Concat(notes.Select(File.ReadAllText));
        Assert.DoesNotContain(key, all);
        Assert.Contains("ghp_…" + key[^4..], all);

        // Exporting again changes nothing: every note is recognised by its pctidy-id and is up to date.
        var again = await new ObsidianExporter(store).PlanAsync(new ExportRequest { Folder = vault, ChatIds = ids }, CancellationToken.None);
        Assert.Empty(again.Items);
        Assert.Equal(3, again.UnchangedNotes);

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.Failed);
        AssertSameOutsideData(before, TestbedBuilder.Manifest(s.Root));
    }

    [Fact]
    public async Task Archive_copy_runs_through_the_engine_when_the_platform_supports_CopyPath()
    {
        using var s = new AiSandbox("e2e-archive", SandboxProfile.Default);
        var transcript = FakeAgents.ClaudeCodeSession(s.K.ClaudeDir, AiSandbox.Now.AddDays(-20));
        FakeAgents.ClaudeConfigFiles(s.K.ClaudeDir);
        var archive = Path.Combine(s.Root, "D", "Chat archive");
        Directory.CreateDirectory(archive); // the folder the user picked
        var sessions = await AgentChatInventory.ListAsync(s.K, CancellationToken.None);
        var plan = AgentArchivePlanner.PlanCopy(sessions, archive, s.Live(), null, CancellationToken.None).ToActionPlan("Archive");
        var copy = plan.Items.First(i => i.Kind == ActionKind.CopyPath);

        var supported = s.Host.Safety.Check(copy).Allowed && s.Host.Handlers.Any(h => h.CanHandle(copy));
        if (!supported)
        {
            // CopyPath is new in v3: its handler/guard rule come from the Engine/Safety agents. Until then only the plan is checked.
            log.WriteLine("CopyPath isn't supported by this build's engine/guard yet: " + s.Host.Safety.Check(copy).Reason);
            Assert.All(plan.Items.Where(i => i.Kind == ActionKind.CreateDirectory), i => Assert.True(s.Host.Safety.Check(i).Allowed, i.Description));
            return;
        }

        var before = TestbedBuilder.Manifest(s.Root);
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps.Where(x => x.Status != StepStatus.Done)) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(plan.Items.Count, run.DoneCount);
        Assert.True(File.Exists(transcript), "the original stays");
        var session = sessions.Single(x => x.TranscriptPath == transcript);
        Assert.True(AgentArchivePlanner.VerifyCopy(archive, session, CancellationToken.None).Verified);

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.Failed);
        AssertSameOutsideData(before, TestbedBuilder.Manifest(s.Root));
    }

    private static bool IsPcTidyData(string relative)
        => relative.StartsWith("Data", StringComparison.OrdinalIgnoreCase) || relative.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase);

    private static void AssertSameOutsideData(TestbedManifest before, TestbedManifest after)
    {
        var outside = TestbedBuilder.Compare(before, after).Differences.Where(d => !IsPcTidyData(d.Path)).ToList();
        Assert.True(outside.Count == 0, "Undo did not restore the tree exactly:\n" + string.Join("\n", outside.Take(20).Select(d => d.ToString())));
    }
}
