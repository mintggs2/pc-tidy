using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.UserFolders;
using PcTidy.Safety;
using PcTidy.Safety.Detection;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>Windows user folders: target validation (drive root, shared, OneDrive, non-empty) and the KnownFolderRedirect plans.</summary>
[Collection("drivemove-testbed")]
public class UserFolderTests
{
    private static DriveList Drives(DriveMoveSandbox s) => DriveList.From(s.K, s.System ?? s.Host.System);

    [Fact]
    public async Task Downloads_Videos_Music_move_to_a_new_folder_and_OneDrive_backed_folders_stay()
    {
        using var s = new DriveMoveSandbox("kf-default"); // Desktop, Documents and Pictures are inside OneDrive
        s.Put(Path.Combine(s.K.Downloads, "setup.exe"), DriveMoveSandbox.Bytes(3000, 1));
        s.Put(Path.Combine(s.K.Videos, "clip.mp4"), DriveMoveSandbox.Bytes(5000, 2));
        var result = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var group = Assert.Single(result.Groups);
        Assert.Equal(DriveMoveIds.GroupUserFolders, group.Id);

        foreach (var (name, id) in new[] { ("Downloads", KnownFolders.Downloads), ("Videos", KnownFolders.Videos), ("Music", KnownFolders.Music) })
        {
            var item = group.Items.Single(i => i.Title == $"Move {name} to D:");
            Assert.Null(item.DisabledReason);
            Assert.False(item.DefaultSelected);
            Assert.Equal(Badge.Check, item.Badge);
            var a = item.Actions[0];
            Assert.Equal(ActionKind.KnownFolderRedirect, a.Kind);
            Assert.Equal(id, Guid.Parse(a.Args![DriveMoveArgs.FolderId]));
            Assert.Equal(Path.Combine(s.D, "Users", "tester", name), a.DestinationPath);
            Assert.Equal("move", a.Args[DriveMoveArgs.Purpose]);
            Assert.True(a.Reversible);
            Assert.True(s.Host.Safety.Check(a).Allowed);
            AssertOldCopyStepsFollow(item, a, s.Host.Safety);
        }
        var videos = group.Items.Single(i => i.Title == "Move Videos to D:");
        Assert.Equal(5000, videos.SizeBytes);
        Assert.Equal(Path.Combine(s.K.Videos, "clip.mp4"), Assert.Single(videos.Actions.Skip(1)).SourcePath);
        Assert.Contains("quarantine", videos.Reason);
        Assert.Empty(group.Items.Single(i => i.Title == "Move Music to D:").Actions.Skip(1)); // nothing in it: nothing to clear

        foreach (var name in new[] { "Pictures", "Documents", "Desktop" })
        {
            var item = group.Items.Single(i => i.Title == name);
            Assert.Contains("OneDrive is backing up", item.DisabledReason);
            Assert.Empty(item.Actions);
        }
    }

    [Fact]
    public async Task Without_OneDrive_all_six_folders_can_move()
    {
        using var s = new DriveMoveSandbox("kf-nocloud", SandboxProfile.Default with { OneDrive = false, OneDriveRedirectsFolders = false });
        var result = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Equal(6, result.AllItems.Count(i => i.DisabledReason is null && i.Actions.Count == 1));
    }

    [Fact]
    public async Task OneDrive_backup_read_from_its_registry_folders_keeps_the_folder_in_place()
    {
        using var s = new DriveMoveSandbox("kf-registry", SandboxProfile.Default with { OneDrive = false, OneDriveRedirectsFolders = false });
        // OneDrive's own account record (HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder) covers Music here.
        s.Pc.OneDrive.Add(s.K.Music);
        var result = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.Contains("OneDrive is backing up", result.AllItems.Single(i => i.Title == "Music").DisabledReason);
    }

    [Fact]
    public void Target_validation_refuses_drive_roots_shared_folders_OneDrive_and_non_empty_folders()
    {
        using var s = new DriveMoveSandbox("kf-targets");
        var all = KnownFolderTargets.All(s.K);
        var downloads = all.Single(f => f.Name == "Downloads");
        var drives = Drives(s);
        string? Problem(string target) => KnownFolderTargets.Problem(target, downloads, all, drives, s.Host.Safety, s.K.CloudRoots);

        Assert.Null(Problem(Path.Combine(s.D, "Users", "tester", "Downloads")));
        Assert.Equal(KnownFolderTargets.DriveRoot, Problem(s.D));
        Assert.Equal(KnownFolderTargets.Shared("Videos"), Problem(s.K.Videos));
        Assert.Equal(KnownFolderTargets.Shared("Videos"), Problem(Path.Combine(s.K.Videos, "Downloads")));
        Assert.NotNull(Problem(s.K.UserProfile)); // the profile holds Downloads itself (and OneDrive)
        Assert.Equal(KnownFolderTargets.Shared("Music"), Problem(Path.Combine(s.K.Music, "Downloads")));
        Assert.Equal(KnownFolderTargets.InCloud, Problem(Path.Combine(s.K.CloudRoots[0], "Downloads")));
        Assert.Equal(KnownFolderTargets.IntoItself, Problem(Path.Combine(s.K.Downloads, "Sub")));
        Assert.Equal(KnownFolderTargets.SamePlace, Problem(s.K.Downloads));

        var used = Path.Combine(s.D, "Stuff");
        s.Put(Path.Combine(used, "old.txt"), "x");
        Assert.Equal(KnownFolderTargets.NotEmpty, Problem(used));
        Assert.Null(Problem(Directory.CreateDirectory(Path.Combine(s.D, "Empty")).FullName)); // an existing EMPTY folder is fine

        // The guard refuses the same plans.
        foreach (var bad in new[] { s.D, s.K.Videos, Path.Combine(s.K.CloudRoots[0], "Downloads"), used })
            Assert.False(s.Host.Safety.Check(Plans.Redirect(downloads.Id, "Downloads", downloads.CurrentPath, bad, 0, restore: false)).Allowed, bad);
    }

    [Fact]
    public void A_removable_or_unknown_drive_is_never_a_target()
    {
        using var s = new DriveMoveSandbox("kf-removable");
        s.Drives((512L << 30, 100L << 30, DriveMedia.Ssd), (64L << 30, 60L << 30, DriveMedia.Removable));
        var all = KnownFolderTargets.All(s.K);
        var problem = KnownFolderTargets.Problem(Path.Combine(s.D, "Users", "tester", "Downloads"), all[0], all, Drives(s), s.Host.Safety, s.K.CloudRoots);
        Assert.Equal(KnownFolderTargets.NotFixed, problem);
        Assert.Equal(KnownFolderTargets.NotFixed,
            KnownFolderTargets.Problem(@"Q:\Users\tester\Downloads", all[0], all, Drives(s), s.Host.Safety, s.K.CloudRoots));
    }

    [Fact]
    public async Task A_non_empty_suggested_folder_is_refused_with_the_reason()
    {
        using var s = new DriveMoveSandbox("kf-nonempty");
        s.Put(Path.Combine(s.D, "Users", "tester", "Videos", "someone else's.mp4"), "x");
        var result = await new UserFolderScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var videos = result.AllItems.Single(i => i.Title == "Videos");
        Assert.Contains(KnownFolderTargets.NotEmpty, videos.DisabledReason);
        Assert.Empty(videos.Actions);
    }

    [Fact]
    public async Task A_moved_folder_offers_restore_default_location()
    {
        using var s = new DriveMoveSandbox("kf-restore");
        var moved = Path.Combine(s.D, "Users", "tester", "Downloads");
        s.Put(Path.Combine(moved, "file.zip"), DriveMoveSandbox.Bytes(2000, 3));
        var k = new MovedDownloads(s.K, moved);
        var guard = new SafetyGuard(k, s.Host.Settings);
        var result = await new UserFolderScanner().ScanAsync(s.Context(guard: guard, locations: k), null, CancellationToken.None);

        var item = result.AllItems.Single(i => i.Title == "Restore default location for Downloads");
        Assert.Null(item.DisabledReason);
        var a = item.Actions[0];
        Assert.Equal(ActionKind.KnownFolderRedirect, a.Kind);
        Assert.Equal(Path.Combine(s.K.UserProfile, "Downloads"), a.DestinationPath);
        Assert.Equal("restore", a.Args![DriveMoveArgs.Purpose]);
        Assert.Equal(moved, a.Args[DriveMoveArgs.CurrentPath]);
        Assert.True(guard.Check(a).Allowed);
        AssertOldCopyStepsFollow(item, a, guard);
        Assert.Equal(Path.Combine(moved, "file.zip"), Assert.Single(item.Actions.Skip(1)).SourcePath);
        Assert.Equal(2000, item.SizeBytes);
    }

    /// <summary>
    /// DMV-1: after the redirect, every other step quarantines one entry of the OLD copy (never the user folder itself),
    /// only if the redirect finished, and the row's size is exactly what those steps free.
    /// </summary>
    private static void AssertOldCopyStepsFollow(ScanItem item, PlanItem redirect, ISafetyGuard guard)
    {
        var oldFolder = redirect.Args![DriveMoveArgs.CurrentPath];
        var rest = item.Actions.Skip(1).ToList();
        Assert.All(rest, q =>
        {
            Assert.Equal(ActionKind.QuarantinePath, q.Kind);
            Assert.Equal(new[] { redirect.Id }, q.DependsOn);
            Assert.Equal(oldFolder, Path.GetDirectoryName(q.SourcePath), ignoreCase: true);
            Assert.True(q.Reversible);
            Assert.True(guard.Check(q).Allowed);
        });
        Assert.Equal(rest.Sum(q => q.ExpectedSize), item.SizeBytes);
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
    }

    /// <summary>The sandbox PC with Downloads already redirected to another drive.</summary>
    private sealed class MovedDownloads(SandboxKnownLocations k, string downloads) : IKnownLocations
    {
        public string UserProfile => k.UserProfile;
        public string LocalAppData => k.LocalAppData;
        public string RoamingAppData => k.RoamingAppData;
        public string ProgramData => k.ProgramData;
        public string WindowsDir => k.WindowsDir;
        public string SystemDrive => k.SystemDrive;
        public string ProgramFiles => k.ProgramFiles;
        public string ProgramFilesX86 => k.ProgramFilesX86;
        public string UserTemp => k.UserTemp;
        public string WindowsTemp => k.WindowsTemp;
        public string Desktop => k.Desktop;
        public string Documents => k.Documents;
        public string Downloads => downloads;
        public string Pictures => k.Pictures;
        public string Videos => k.Videos;
        public string Music => k.Music;
        public IReadOnlyList<string> CloudRoots => k.CloudRoots;
        public string? SteamRoot => k.SteamRoot;
        public IReadOnlyList<string> SteamLibraries => k.SteamLibraries;
        public string? EpicManifestsDir => k.EpicManifestsDir;
        public IReadOnlyList<string> GameInstallDirs => k.GameInstallDirs;
        public IReadOnlyList<string> AppInstallDirs => k.AppInstallDirs;
        public string ClaudeDir => k.ClaudeDir;
        public string CodexDir => k.CodexDir;
        public string DataRoot => k.DataRoot;
        public IReadOnlyList<string> FixedDrives => k.FixedDrives;
        public bool IsSandbox => true;
    }
}
