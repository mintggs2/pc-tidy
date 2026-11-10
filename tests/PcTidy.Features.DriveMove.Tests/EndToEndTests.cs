using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.TestbedGen;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>
/// Real composition (sandbox): scan → plan → real SafetyGuard → Executor → Undo, proved with hash manifests.
/// C\Users\tester\Videos\Big moves to D\Moved by PC Tidy\Big with a junction left behind, then undo puts it all back.
/// </summary>
[Collection("drivemove-testbed")]
public class EndToEndTests(ITestOutputHelper log)
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    [Fact]
    public async Task Folder_moves_to_the_other_drive_with_a_junction_and_undo_leaves_the_testbed_identical()
    {
        using var s = new DriveMoveSandbox("e2e-junction", SandboxProfile.Default, generate: true);
        var big = Path.Combine(s.K.Videos, "Big");
        s.Put(Path.Combine(big, "holiday.mp4"), DriveMoveSandbox.Bytes(200_000, 11), DriveMoveSandbox.Now.AddDays(-40));
        s.Put(Path.Combine(big, "Raw", "take 1.mov"), DriveMoveSandbox.Bytes(150_000, 12), DriveMoveSandbox.Now.AddDays(-41));
        s.Put(Path.Combine(big, "Raw", "notes.txt"), "keep me", DriveMoveSandbox.Now.AddDays(-42));
        // The user allowed PC Tidy to work inside Videos (Settings → folder permissions).
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var before = TestbedBuilder.Manifest(s.Root);

        var scan = await new FolderMoveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var item = scan.AllItems.Single(i => string.Equals(i.Detail, big, StringComparison.OrdinalIgnoreCase));
        log.WriteLine($"{item.Title}: {item.Reason}");
        Assert.Null(item.DisabledReason);
        Assert.False(item.DefaultSelected);
        var action = Assert.Single(item.Actions);
        var destination = Path.Combine(s.D, DriveMoveIds.MovedFolderName, "Big");
        Assert.Equal(ActionKind.MoveFolderWithJunction, action.Kind);
        Assert.Equal(big, action.SourcePath);
        Assert.Equal(destination, action.DestinationPath);
        Assert.True(action.IsDirectory);
        Assert.Equal(200_000 + 150_000 + 7, action.ExpectedSize);

        var plan = PlanBuilder.FromSelection(DriveMoveIds.FeatureId, "Folders", new[] { item });
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        Assert.Equal(1, run.DoneCount);

        // The files live on D now; the old place is a junction that reads the same content.
        Assert.True(File.Exists(Path.Combine(destination, "Raw", "take 1.mov")));
        var link = new DirectoryInfo(big);
        Assert.True(link.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(big, "Raw", "notes.txt")));
        // A second scan never offers the link itself again.
        var again = await new FolderMoveScanner().ScanAsync(s.Context(roots: new[] { big }), null, CancellationToken.None);
        Assert.Equal(JunctionRules.AlreadyLink, again.AllItems.Single(i => string.Equals(i.Detail, big, StringComparison.OrdinalIgnoreCase)).DisabledReason);

        var undo = await s.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        foreach (var m in undo.Messages) log.WriteLine(m);
        Assert.Equal(0, undo.Failed);
        Assert.False(new DirectoryInfo(big).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.False(Directory.Exists(Path.Combine(s.D, DriveMoveIds.MovedFolderName)));

        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(s.Root));
        // PC Tidy's own data (journal, quarantine) is the only thing allowed to differ.
        var outside = diff.Differences.Where(d => !d.Path.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase) && d.Path != "Data").ToList();
        Assert.True(outside.Count == 0, "Undo did not restore the tree exactly:\n" + string.Join("\n", outside.Take(20).Select(d => d.ToString())));
    }
}
