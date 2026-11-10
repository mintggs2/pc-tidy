using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.UserFolders;
using Xunit;
using Xunit.Abstractions;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>v2 review: DMV-1 (the old copy of a moved user folder really goes to quarantine, only after the move) and DMV-2 (external disks).</summary>
[Collection("drivemove-testbed")]
public class ReviewFixTests(ITestOutputHelper log)
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    [Fact]
    public async Task The_old_copy_is_left_alone_when_the_move_itself_does_not_run()
    {
        using var s = new DriveMoveSandbox("dmv1-gated");
        var file = s.Put(Path.Combine(s.K.Downloads, "setup.exe"), DriveMoveSandbox.Bytes(3000, 1));
        s.Put(Path.Combine(s.K.Downloads, "Installers", "tool.msi"), DriveMoveSandbox.Bytes(4000, 2));
        var scan = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var item = scan.AllItems.Single(i => i.Title == "Move Downloads to D:");
        Assert.Equal(3, item.Actions.Count);
        Assert.Equal(7000, item.SizeBytes);

        // In the sandbox the known-folder move is never done for real, so every quarantine step must be skipped with it.
        var plan = PlanBuilder.FromSelection("drivemove", "User folders", new[] { item });
        var run = await s.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        foreach (var step in run.Steps) log.WriteLine($"{step.Status}: {step.Item.Description} {step.Message}");
        var redirect = run.Steps.Single(x => x.Item.Kind == ActionKind.KnownFolderRedirect);
        Assert.NotEqual(StepStatus.Done, redirect.Status);
        Assert.All(run.Steps.Where(x => x.Item.Kind == ActionKind.QuarantinePath), q => Assert.Equal(StepStatus.Skipped, q.Status));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(s.K.Downloads, "Installers", "tool.msi")));
    }

    [Fact]
    public async Task Ticking_the_old_copy_steps_without_the_move_does_nothing()
    {
        using var s = new DriveMoveSandbox("dmv1-alone");
        var file = s.Put(Path.Combine(s.K.Videos, "clip.mp4"), DriveMoveSandbox.Bytes(5000, 3));
        var scan = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var item = scan.AllItems.Single(i => i.Title == "Move Videos to D:");
        var onlyQuarantine = new ActionPlan { FeatureId = "drivemove", Title = "Old copy only", Items = item.Actions.Skip(1).ToList() };

        var run = await s.Host.Executor.ExecuteAsync(onlyQuarantine, Real, null, CancellationToken.None);
        Assert.All(run.Steps, q => Assert.Equal(StepStatus.Skipped, q.Status));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void An_external_usb_disk_is_never_a_receiver_or_a_user_folder_target()
    {
        using var s = new DriveMoveSandbox("dmv2-usb");
        var details = s.K.FixedDrives.Select((d, i) => new DriveDetails(d, i == 0 ? "System" : "Backup", i == 0 ? DriveMedia.Ssd : DriveMedia.Hdd,
            2L << 40, i == 0 ? 10L << 30 : 1L << 40, i == 0) { IsExternal = i > 0 }).ToList();
        s.System = new FakeSystemInfo(details);
        var drives = DriveList.From(s.K, s.System);

        var usb = drives.All.Single(d => d.IsExternal);
        Assert.False(usb.CanReceive);
        Assert.DoesNotContain(usb, drives.Receivers);
        Assert.False(drives.MovingAvailable);
        Assert.Equal(NotAvailable.NoOtherFixedDrive, drives.UnavailableReason);
        Assert.Contains("external", usb.NotReceivingReason);
        Assert.Equal("External (USB)", usb.MediaText);
        Assert.Empty(drives.TargetsFor(drives.All[0], 1, DriveMoveSandbox.Tiny));

        var all = KnownFolderTargets.All(s.K);
        var problem = KnownFolderTargets.Problem(Path.Combine(s.D, "Users", "tester", "Downloads"), all[0], all, drives, s.Host.Safety, s.K.CloudRoots);
        Assert.Equal(usb.NotReceivingReason, problem);
    }

    [Fact]
    public async Task With_an_external_disk_as_the_only_other_drive_user_folders_are_not_offered()
    {
        using var s = new DriveMoveSandbox("dmv2-usb-scan");
        s.Put(Path.Combine(s.K.Downloads, "setup.exe"), DriveMoveSandbox.Bytes(3000, 1));
        s.System = new FakeSystemInfo(s.K.FixedDrives.Select((d, i) => new DriveDetails(d, "", DriveMedia.Ssd, 1L << 40, 1L << 39, i == 0) { IsExternal = i > 0 }).ToList());
        var scan = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(scan.AllItems, i => i.Actions.Count > 0);
    }
}
