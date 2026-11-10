using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using Xunit;

namespace PcTidy.Features.DriveMove.Tests;

/// <summary>Suggestion logic (pure): full-drive thresholds, room on the target, only real drives, one-drive PCs.</summary>
public class DriveBalanceTests
{
    private const long GB = 1L << 30;
    private static readonly DateTime Now = DriveMoveSandbox.Now;
    private static readonly DriveMoveThresholds T = DriveMoveThresholds.Default;

    private static DriveFacts C(long total, long free) => new(@"C:\", "System", DriveMedia.Ssd, total, free, true);
    private static DriveFacts D(long total, long free, DriveMedia media = DriveMedia.Hdd) => new(@"D:\", "Data", media, total, free, false);

    private static PlanItem Act(string path) => new() { Kind = ActionKind.KnownFolderRedirect, FeatureId = "drivemove", Description = "move", SourcePath = path };

    private static Movable Folder(string name, string path, long bytes, MovableKind kind = MovableKind.UserFolder)
        => new(kind, name, path, bytes, null, new[] { Act(path) });

    private static Movable Steam(string name, long bytes, DateTime? lastPlayed)
        => new(MovableKind.SteamGame, name, $@"C:\Program Files (x86)\Steam\steamapps\common\{name}", bytes, lastPlayed,
            new[] { new PlanItem { Kind = ActionKind.OpenExternal, FeatureId = "drivemove", Description = "open", Args = new() { ["target"] = "steam://open/settings" } } });

    [Theory]
    [InlineData(500, 50, true)]     // 10% free
    [InlineData(500, 150, false)]   // 30% free, 150 GB
    [InlineData(100, 18, true)]     // 18% free but under 20 GB
    [InlineData(1000, 160, false)]  // 16% free
    [InlineData(1000, 140, true)]   // 14% free
    public void A_drive_is_full_under_15_percent_or_20_GB_free(long totalGb, long freeGb, bool full)
        => Assert.Equal(full, C(totalGb * GB, freeGb * GB).IsFull(T));

    [Fact]
    public void One_drive_means_moving_is_not_available()
    {
        var drives = DriveList.Of(new[] { C(500 * GB, 10 * GB) });
        Assert.False(drives.MovingAvailable);
        Assert.Equal(NotAvailable.SingleDrive, drives.UnavailableReason);
        var advice = BalanceAdvisor.Advise(drives, new[] { Folder("Videos", @"C:\Users\x\Videos", 40 * GB) }, T, Now);
        Assert.False(advice.MovingAvailable);
        Assert.Empty(advice.Suggestions);
        Assert.Contains("one drive", advice.UnavailableReason);
    }

    [Fact]
    public void A_removable_or_network_second_drive_does_not_count()
    {
        Assert.False(DriveList.Of(new[] { C(500 * GB, 10 * GB), D(1000 * GB, 900 * GB, DriveMedia.Removable) }).MovingAvailable);
        Assert.False(DriveList.Of(new[] { C(500 * GB, 10 * GB), D(1000 * GB, 900 * GB, DriveMedia.Network) }).MovingAvailable);
        Assert.Equal(NotAvailable.NoOtherFixedDrive, DriveList.Of(new[] { C(500 * GB, 10 * GB), D(1000 * GB, 900 * GB, DriveMedia.Removable) }).UnavailableReason);
    }

    [Fact]
    public void Suggests_the_biggest_things_on_a_full_drive_to_the_drive_with_room()
    {
        var drives = DriveList.Of(new[] { C(500 * GB, 30 * GB), D(1000 * GB, 400 * GB) });
        var advice = BalanceAdvisor.Advise(drives, new[]
        {
            Folder("Downloads", @"C:\Users\x\Downloads", 5 * GB),
            Folder("Videos", @"C:\Users\x\Videos", 40 * GB),
            Folder("Tiny", @"C:\Users\x\Tiny", GB / 2, MovableKind.Folder), // under 1 GB: not suggested
            Folder("Elsewhere", @"D:\Stuff", 90 * GB, MovableKind.Folder),  // already on the roomy drive
        }, T, Now);

        Assert.True(advice.MovingAvailable);
        Assert.Equal(@"C:\", Assert.Single(advice.FullDrives).Root);
        Assert.Equal(new[] { "Move Videos (40 GB) to D:", "Move Downloads (5 GB) to D:" }, advice.Suggestions.Select(s => s.Title));
        Assert.All(advice.Suggestions, s => Assert.Single(s.Actions));
        Assert.DoesNotContain(advice.Biggest, m => m.Name == "Elsewhere");
    }

    [Fact]
    public void Nothing_is_suggested_when_no_drive_is_full()
    {
        var drives = DriveList.Of(new[] { C(500 * GB, 300 * GB), D(1000 * GB, 400 * GB) });
        var advice = BalanceAdvisor.Advise(drives, new[] { Folder("Videos", @"C:\Users\x\Videos", 40 * GB) }, T, Now);
        Assert.Empty(advice.FullDrives);
        Assert.Empty(advice.Suggestions);
        Assert.Equal("Videos", Assert.Single(advice.Biggest).Name); // system drive's big things still shown
    }

    [Fact]
    public void The_target_needs_the_size_plus_10_percent()
    {
        var movable = new[] { Folder("Videos", @"C:\Users\x\Videos", 40 * GB) };
        var tight = DriveList.Of(new[] { C(500 * GB, 30 * GB), D(100 * GB, 43 * GB) });  // needs 44 GB
        var enough = DriveList.Of(new[] { C(500 * GB, 30 * GB), D(100 * GB, 45 * GB) });
        Assert.Empty(BalanceAdvisor.Advise(tight, movable, T, Now).Suggestions);
        Assert.Single(BalanceAdvisor.Advise(enough, movable, T, Now).Suggestions);
    }

    [Fact]
    public void Suggestions_share_the_room_on_the_target()
    {
        var drives = DriveList.Of(new[] { C(500 * GB, 30 * GB), D(100 * GB, 50 * GB) });
        var advice = BalanceAdvisor.Advise(drives, new[]
        {
            Folder("Videos", @"C:\Users\x\Videos", 40 * GB),
            Folder("Music", @"C:\Users\x\Music", 30 * GB),
        }, T, Now);
        Assert.Equal("Move Videos (40 GB) to D:", Assert.Single(advice.Suggestions).Title);
    }

    [Fact]
    public void Nested_things_are_never_both_suggested()
    {
        var drives = DriveList.Of(new[] { C(500 * GB, 30 * GB), D(1000 * GB, 800 * GB) });
        var advice = BalanceAdvisor.Advise(drives, new[]
        {
            Folder("Videos", @"C:\Users\x\Videos", 40 * GB),
            Folder("Big", @"C:\Users\x\Videos\Big", 30 * GB, MovableKind.Folder),
        }, T, Now);
        Assert.Equal("Move Videos (40 GB) to D:", Assert.Single(advice.Suggestions).Title);
    }

    [Fact]
    public void Steam_games_not_played_in_6_months_become_one_launcher_suggestion()
    {
        var drives = DriveList.Of(new[] { C(1000 * GB, 60 * GB), D(2000 * GB, 1000 * GB) });
        var old = Now.AddDays(-250);
        var advice = BalanceAdvisor.Advise(drives, new[]
        {
            Steam("Alpha", 72 * GB, old), Steam("Beta", 56 * GB, old), Steam("Gamma", 45 * GB, Now.AddDays(-400)), Steam("Delta", 44 * GB, old),
            Steam("Fresh", 60 * GB, Now.AddDays(-3)),
            Steam("Unknown", 30 * GB, null),
        }, T, Now);

        var s = Assert.Single(advice.Suggestions);
        Assert.Equal("4 Steam games not played in 6 months (217 GB) → move with Steam to D:", s.Title);
        var open = Assert.Single(s.Actions);
        Assert.Equal(ActionKind.OpenExternal, open.Kind);
        Assert.Equal("steam://open/settings", open.Args!["target"]);
        Assert.Equal(4, s.Items.Count);
    }

    [Fact]
    public void Only_drives_that_exist_and_are_ready_are_listed()
    {
        var root = Path.Combine(DriveMoveSandbox.TestbedRoot, "drives-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var k = new SandboxKnownLocations(root, SandboxProfile.Default);
            Directory.CreateDirectory(k.FixedDrives[0]);
            Directory.CreateDirectory(k.FixedDrives[1]);
            var both = k.FixedDrives.Select((d, i) => new DriveDetails(d, "", DriveMedia.Ssd, 100 * GB, 50 * GB, i == 0)).ToList();

            Assert.Equal(2, DriveList.From(k, new FakeSystemInfo(both)).All.Count);
            // Windows reports only C: (D: not ready): D: is not offered and moving isn't available.
            var onlyC = DriveList.From(k, new FakeSystemInfo(both.Take(1).ToList()));
            Assert.Single(onlyC.All);
            Assert.False(onlyC.MovingAvailable);
            // The D folder vanished: not listed either.
            Directory.Delete(k.FixedDrives[1]);
            Assert.Single(DriveList.From(k, new FakeSystemInfo(both)).All);
        }
        finally { DriveMoveSandbox.ForceDelete(root); }
    }

    [Fact]
    public void Sandbox_drive_folders_read_as_drive_letters()
    {
        var d = new DriveFacts(@"X:\testbed\abc\D\", "", DriveMedia.Ssd, 1, 1, false);
        Assert.Equal("D:", d.Name);
        Assert.Equal("C:", C(1, 1).Name);
    }
}
