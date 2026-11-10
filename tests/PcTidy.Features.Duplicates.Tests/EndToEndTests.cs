using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.TestbedGen;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>
/// Scan → plan → real SafetyGuard → real Executor → Undo on a generated PC (PcTidyHost.CreateSandbox),
/// and the tree is identical afterwards (PC Tidy's own Data folder aside).
/// </summary>
public sealed class EndToEndTests
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    [Theory]
    [InlineData("e2e-minimal", false)]
    [InlineData("e2e-default", true)]
    public async Task Quarantines_duplicates_and_junk_then_undo_restores_everything(string name, bool defaultProfile)
    {
        using var pc = new GeneratedPc(name, defaultProfile ? SandboxProfile.Default : SandboxProfile.Minimal);
        var before = TestbedBuilder.Manifest(pc.Root);
        var scanner = new DuplicatesModule().Scanners.Single();
        var result = await scanner.ScanAsync(pc.Context(ScanDepth.Quick), null, CancellationToken.None);

        // Default ticks (clear cases), plus one hand-picked item of each other kind the user could tick.
        var picked = result.AllItems.Where(i => i.DefaultSelected).ToList();
        Assert.NotEmpty(picked);
        foreach (var groupId in new[] { "duplicates.empty-folders", "duplicates.zero-byte", "duplicates.broken-shortcuts", "duplicates.similar" })
            if (result.Groups.FirstOrDefault(g => g.Id == groupId)?.Items.FirstOrDefault(i => i.DisabledReason is null) is { } extra)
                picked.Add(extra);
        var exactUnticked = result.Groups.Single(g => g.Id == "duplicates.exact").Items.FirstOrDefault(i => !i.DefaultSelected && i.DisabledReason is null);
        if (exactUnticked is not null) picked.Add(exactUnticked);
        var plan = PlanBuilder.FromSelection(DuplicatesModule.FeatureId, "Duplicates and junk", picked);
        Assert.All(plan.Items, i => Assert.True(pc.Host.Safety.Check(i).Allowed, $"{i.SourcePath}: {pc.Host.Safety.Check(i).Reason}"));

        var run = await pc.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        Assert.True(run.DoneCount == plan.Items.Count, string.Join("\n", run.Steps.Where(s => s.Status != StepStatus.Done).Select(s => $"{s.Status} {s.Item.SourcePath}: {s.Message}")));
        foreach (var item in plan.Items)
            Assert.False(File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath), item.SourcePath);
        // Every kept copy is still there.
        foreach (var item in plan.Items.Where(i => i.Args?.ContainsKey("keepPath") == true))
            Assert.True(File.Exists(item.Args!["keepPath"]) || Directory.Exists(item.Args["keepPath"]), item.Args["keepPath"]);
        var after = TestbedBuilder.Manifest(pc.Root);
        Assert.Empty(TestbedBuilder.CanaryViolations(before, after, pc.Info.Canaries.Where(c => c.Tier == CanaryTier.Never)));
        Assert.True(run.BytesFreed > 0);

        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }

    [Fact]
    public async Task A_copy_changed_after_the_scan_is_skipped_not_removed()
    {
        using var pc = new HandMadePc("changed");
        var bytes = HandMadePc.Bytes(7, 5_000);
        pc.Put(Path.Combine(pc.K.Documents, "keep.pdf"), bytes);
        var dl = pc.Put(Path.Combine(pc.K.Downloads, "keep.pdf"), bytes);
        var result = await new DuplicatesModule().Scanners.Single().ScanAsync(pc.Context(), null, CancellationToken.None);
        var item = Assert.Single(result.AllItems, i => i.DefaultSelected);
        Assert.True(DefaultPcFixture.Same(dl, item.Detail!));
        // The user edits the Downloads copy before confirming: it's no longer the file that was checked.
        File.WriteAllBytes(dl, HandMadePc.Bytes(8, 5_000));
        var plan = PlanBuilder.FromSelection(DuplicatesModule.FeatureId, "Duplicates", new[] { item });
        var run = await pc.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        Assert.Equal(StepStatus.Skipped, run.Steps.Single().Status);
        Assert.True(File.Exists(dl));
    }
}
