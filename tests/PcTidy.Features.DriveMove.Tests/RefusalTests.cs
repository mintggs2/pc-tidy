using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>
/// "Any folder → other drive" refusals, each listed case, explained by the section's own rules AND enforced by the real
/// SafetyGuard on the plan item (a sandbox PC built by the real composition root).
/// </summary>
[Collection("drivemove-testbed")]
public class RefusalTests
{
    private static async Task<ScanItem> PickAsync(DriveMoveSandbox s, string folder, ISafetyGuard? guard = null)
    {
        var result = await new FolderMoveScanner().ScanAsync(s.Context(roots: new[] { folder }, guard: guard), null, CancellationToken.None);
        return result.AllItems.Single(i => string.Equals(i.Detail, PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A junction move of this folder to D:\Moved by PC Tidy, as if the section had offered it.</summary>
    private static PlanItem Junction(DriveMoveSandbox s, string folder) => new()
    {
        Kind = ActionKind.MoveFolderWithJunction, FeatureId = DriveMoveIds.FeatureId, Description = "test",
        SourcePath = folder, DestinationPath = Path.Combine(s.D, DriveMoveIds.MovedFolderName, Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : "x"),
        IsDirectory = true,
    };

    private static void AssertRefused(ScanItem item, string reason)
    {
        Assert.Equal(reason, item.DisabledReason);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task Each_protected_place_is_refused_with_its_reason_and_the_guard_agrees()
    {
        using var s = new DriveMoveSandbox("refuse");
        var k = s.K;
        var cases = new (string Folder, string Reason)[]
        {
            (Path.Combine(k.WindowsDir, "Fonts"), JunctionRules.Windows),
            (Path.Combine(k.WindowsDir, "System32", "drivers"), JunctionRules.Drivers),
            (Path.Combine(k.ProgramFiles, "SomeApp"), JunctionRules.Apps),
            (Path.Combine(k.ProgramFilesX86, "OldApp"), JunctionRules.Apps),
            (Path.Combine(k.ProgramData, "Vendor"), JunctionRules.Apps),
            (Path.Combine(k.SteamLibraries[0], "steamapps", "common", "SomeGame"), JunctionRules.Games),
            (Path.Combine(k.SteamLibraries[1]), JunctionRules.Games),
            (Path.Combine(k.ProgramFiles, "Epic Games", "FakeGame"), JunctionRules.Games), // Epic game in Program Files: the game reason wins
            (Path.Combine(s.D, "XboxGames", "Halo"), JunctionRules.Games),
            (Path.Combine(k.LocalAppData, "Packages", "Microsoft.Photos_8wekyb3d8bbwe"), JunctionRules.StoreApps),
            (Path.Combine(k.CloudRoots[0], "Holiday"), JunctionRules.Cloud),
            (k.Downloads, JunctionRules.UserFolderRoot),
            (k.Videos, JunctionRules.UserFolderRoot),
            (k.UserProfile, JunctionRules.ProfileOrAppData),
            (k.LocalAppData, JunctionRules.ProfileOrAppData),
            (Path.GetDirectoryName(k.UserProfile)!, JunctionRules.HoldsUserFolders),
            (Path.Combine(s.D, "Games"), JunctionRules.Games), // holds a Steam library
            (k.DataRoot, JunctionRules.PcTidyData),
        };
        foreach (var (folder, _) in cases) Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(s.D, "Games", "steamapps"));

        // Places today's guard doesn't know by path (Store app data, a Steam library Steam doesn't list): the section's own
        // rules are the only stop there, which the report asks the lead to add to the guard.
        var sectionOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Path.Combine(k.LocalAppData, "Packages", "Microsoft.Photos_8wekyb3d8bbwe"), Path.Combine(s.D, "Games") };

        foreach (var (folder, reason) in cases)
        {
            var item = await PickAsync(s, folder);
            AssertRefused(item, reason);
            Assert.Empty(item.Actions); // nothing to tick
            // The guard enforces the same limit on the plan item itself.
            if (!sectionOnly.Contains(folder))
                Assert.False(s.Host.Safety.Check(Junction(s, folder)).Allowed, $"guard allowed a junction move of {folder}");
        }
    }

    [Fact]
    public async Task A_whole_drive_and_an_existing_link_are_refused()
    {
        using var s = new DriveMoveSandbox("refuse-root", generate: true);
        AssertRefused(await PickAsync(s, s.C), JunctionRules.DriveRoot);
        Assert.False(s.Host.Safety.Check(Junction(s, s.C)).Allowed);

        // The generated testbed has a junction in Downloads ("Linked Projects" → source\repos): never followed, never moved.
        var link = Path.Combine(s.K.Downloads, "Linked Projects");
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        AssertRefused(await PickAsync(s, link), JunctionRules.AlreadyLink);
        // And the folder scan doesn't offer it either.
        var scan = await new FolderMoveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(scan.AllItems, i => string.Equals(i.Detail, link, StringComparison.OrdinalIgnoreCase) && i.DisabledReason is null);
    }

    [Fact]
    public async Task A_folder_with_files_in_use_is_refused_until_they_are_closed()
    {
        using var s = new DriveMoveSandbox("refuse-inuse");
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var file = s.Put(Path.Combine(s.K.Videos, "Editing", "project.mp4"), DriveMoveSandbox.Bytes(4096, 1));
        using (TestbedBuilder.LockFile(file))
        {
            var item = await PickAsync(s, Path.GetDirectoryName(file)!);
            Assert.NotNull(item.DisabledReason);
            Assert.Contains("in use", item.DisabledReason!.ToLowerInvariant().Replace("using files", "in use"));
        }
        var free = await PickAsync(s, Path.GetDirectoryName(file)!);
        Assert.Null(free.DisabledReason);
    }

    [Fact]
    public async Task Without_room_on_the_other_drive_the_folder_stays()
    {
        using var s = new DriveMoveSandbox("refuse-room");
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        s.Put(Path.Combine(s.K.Videos, "Big", "a.bin"), DriveMoveSandbox.Bytes(10_000, 2));
        s.Drives((100_000, 1_000, DriveMedia.Ssd), (100_000, 10_500, DriveMedia.Hdd)); // needs 11,000 bytes on D:
        var item = await PickAsync(s, Path.Combine(s.K.Videos, "Big"));
        Assert.NotNull(item.DisabledReason);
        Assert.Contains("plus 10%", item.DisabledReason);
        Assert.Empty(item.Actions);
    }

    [Fact]
    public async Task Inside_a_user_library_the_guard_asks_first_until_the_folder_is_allowed()
    {
        using var s = new DriveMoveSandbox("refuse-library");
        s.Put(Path.Combine(s.K.Videos, "Big", "a.bin"), DriveMoveSandbox.Bytes(1000, 3));
        var blocked = await PickAsync(s, Path.Combine(s.K.Videos, "Big"));
        Assert.Contains("Videos", blocked.DisabledReason);
        Assert.Equal(ActionKind.MoveFolderWithJunction, Assert.Single(blocked.Actions).Kind); // shown, not tickable

        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var allowed = await PickAsync(s, Path.Combine(s.K.Videos, "Big"));
        Assert.Null(allowed.DisabledReason);
    }

    [Fact]
    public void Apps_whose_updater_replaces_their_folder_get_a_warning()
    {
        using var s = new DriveMoveSandbox("warn-squirrel");
        var rules = new JunctionRules(s.K, s.Host.Safety);
        var discord = Directory.CreateDirectory(Path.Combine(s.K.LocalAppData, "Discord")).FullName;
        var w = rules.Warning(discord);
        Assert.NotNull(w);
        Assert.Contains("Discord", w!.Text);
        Assert.Contains("Discord", w.CloseFirst);
        Assert.Null(rules.Refusal(discord)); // allowed, with the warning

        // An unlisted app with Squirrel's own layout is spotted too.
        var other = Path.Combine(s.K.LocalAppData, "SomeChat");
        s.Put(Path.Combine(other, "Update.exe"), "x");
        Directory.CreateDirectory(Path.Combine(other, "app-1.2.3"));
        Assert.NotNull(rules.Warning(other));
        Assert.Null(rules.Warning(Directory.CreateDirectory(Path.Combine(s.K.LocalAppData, "PlainTool")).FullName));
    }

    [Fact]
    public async Task OneDrive_is_never_junctioned_into_either()
    {
        using var s = new DriveMoveSandbox("refuse-into-cloud");
        s.Host.Settings.AllowedAskFirstFolders.Add(s.K.Videos);
        var folder = Directory.CreateDirectory(Path.Combine(s.K.Videos, "Clips")).FullName;
        var into = Junction(s, folder) with { DestinationPath = Path.Combine(s.K.CloudRoots[0], "Clips") };
        Assert.False(s.Host.Safety.Check(into).Allowed);
        Assert.True(s.Host.Safety.Check(Junction(s, folder)).Allowed);
        await Task.CompletedTask;
    }
}
