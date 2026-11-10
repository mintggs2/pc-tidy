using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Photos.Desktop;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.Shared;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Photos.Tests;

/// <summary>
/// Scan → plan → real SafetyGuard → real Executor → Undo on a generated PC (PcTidyHost.CreateSandbox), and the tree is
/// identical afterwards (PC Tidy's own Data folder aside).
/// </summary>
public class EndToEndTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Photos_sort_and_quarantine_then_undo_restores_everything(bool defaultProfile)
    {
        using var pc = TestPc.Generated(defaultProfile ? "e2e-photos-default" : "e2e-photos-minimal", defaultProfile ? SandboxProfile.Default : SandboxProfile.Minimal);
        if (defaultProfile) CloudFolders.Allow(pc.K.Pictures, pc.Host.Settings);
        var before = TestbedBuilder.Manifest(pc.Root);
        // The scanner reads the saved settings (the page stores them the same way).
        new PhotoSettings { Folders = new() { pc.K.Pictures, pc.K.Downloads }, IncludeSubfolders = true }.Store(pc.Host.Settings);

        var result = await new PhotosModule().Scanners.Single().ScanAsync(pc.Context(ScanDepth.Quick), null, CancellationToken.None);

        // Every default-ticked move, plus the one flagged copy the user chooses to quarantine (its move is dropped).
        var picked = result.AllItems.Where(i => i.DefaultSelected).ToList();
        var flagged = result.Groups.Single(g => g.Id == PhotoResultBuilder.BurstGroupId).Items.First();
        picked.Add(flagged);
        var plan = new ActionPlan { FeatureId = PhotosModule.FeatureId, Title = "Sort photos", Items = PlanFolders.Order(picked.SelectMany(i => i.Actions)) };
        Assert.DoesNotContain(plan.Items, i => i.Kind == ActionKind.MovePath && TestPc.Same(i.SourcePath!, flagged.Actions[0].SourcePath!));
        Assert.True(plan.Items.Count(i => i.Kind == ActionKind.MovePath) >= 5);
        Assert.All(plan.Items, i => Assert.True(pc.Host.Safety.Check(i).Allowed, $"{i.Description}: {pc.Host.Safety.Check(i).Reason}"));

        var run = await pc.Host.Executor.ExecuteAsync(plan, TestPc.Real, null, CancellationToken.None);
        Assert.True(run.DoneCount == plan.Items.Count, string.Join("\n", run.Steps.Where(s => s.Status != StepStatus.Done).Select(s => $"{s.Status} {s.Item.Description}: {s.Message}")));
        foreach (var move in plan.Items.Where(i => i.Kind == ActionKind.MovePath))
        {
            Assert.False(File.Exists(move.SourcePath));
            Assert.True(File.Exists(move.DestinationPath), move.DestinationPath);
        }
        Assert.False(File.Exists(flagged.Actions[0].SourcePath));
        var after = TestbedBuilder.Manifest(pc.Root);
        Assert.Empty(TestbedBuilder.CanaryViolations(before, after, pc.Info!.Canaries.Where(c => c.Tier == CanaryTier.Never)));

        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }

    [Theory]
    [InlineData(DesktopTidyMode.MoveOldClutter, true)]
    [InlineData(DesktopTidyMode.GroupByType, false)]
    public async Task Desktop_tidy_then_undo_restores_everything(DesktopTidyMode mode, bool defaultProfile)
    {
        using var pc = TestPc.Generated($"e2e-desktop-{mode}", defaultProfile ? SandboxProfile.Default : SandboxProfile.Minimal);
        if (defaultProfile) CloudFolders.Allow(pc.K.Desktop, pc.Host.Settings);
        new DesktopTidySettings { Mode = mode, IncludeShortcuts = true }.Store(pc.Host.Settings);
        var before = TestbedBuilder.Manifest(pc.Root);

        var result = await new DesktopTidyModule().Scanners.Single().ScanAsync(pc.Context(ScanDepth.Deep), null, CancellationToken.None);
        var picked = result.AllItems.Where(i => i.DefaultSelected).ToList();
        Assert.NotEmpty(picked);
        var plan = PlanBuilder.FromSelection(DesktopTidyModule.FeatureId, "Desktop tidy", picked);
        Assert.All(plan.Items, i => Assert.True(PathUtil.IsUnder(i.DestinationPath!, pc.K.Desktop)));

        var run = await pc.Host.Executor.ExecuteAsync(plan, TestPc.Real, null, CancellationToken.None);
        Assert.True(run.DoneCount == plan.Items.Count, string.Join("\n", run.Steps.Where(s => s.Status != StepStatus.Done).Select(s => $"{s.Status} {s.Item.Description}: {s.Message}")));
        Assert.True(File.Exists(Path.Combine(pc.K.Desktop, "desktop.ini")));
        foreach (var move in plan.Items.Where(i => i.Kind == ActionKind.MovePath)) Assert.True(File.Exists(move.DestinationPath), move.DestinationPath);

        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }
}
