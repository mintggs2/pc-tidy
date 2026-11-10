using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Actions;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Tests;

public class ScannerAndActionsTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddYears(-2);
    private const long MB = 1024 * 1024, GB = 1024 * MB;
    private const string SandboxRoot = @"X:\Sbx";

    private sealed record Pc(SandboxKnownLocations Loc, FakeTree Tree, FakeSafety Safety, FakeDrives Drives);

    private static Pc MakePc(SandboxProfile profile, long dFree = 500 * GB)
    {
        var loc = new SandboxKnownLocations(SandboxRoot, profile);
        var tree = new FakeTree(loc.UserProfile);
        tree.Facts.Dirs.Add(tree.Root);
        tree.File(@"Videos\2023\wedding.mov", 4 * GB, Old);
        tree.File(@"Downloads\installer.iso", 700 * MB, Old);
        tree.File(@"Downloads\new.zip", 900 * MB, Now.AddDays(-2));
        tree.File(@"Documents\small.docx", 20_000, Old);
        var drives = new FakeDrives();
        drives.Drives[Path.Combine(SandboxRoot, "C")] = new DriveSpace(512 * GB, 40 * GB);
        if (profile.SecondDrive) drives.Drives[Path.Combine(SandboxRoot, "D")] = new DriveSpace(2048 * GB, dFree);
        return new Pc(loc, tree, new FakeSafety(), drives);
    }

    private static Task<ScanResult> RunScanner(Pc pc, ScanDepth depth = ScanDepth.Quick)
    {
        var scanner = new SpaceMapScanner(() => Now, new FakeTags(), pc.Drives, pc.Tree.Facts);
        var ctx = new ScanContext
        {
            Locations = pc.Loc,
            Safety = pc.Safety,
            InUse = new NullInUse(),
            Settings = new AppSettings(),
            Walker = pc.Tree.Walker,
            Depth = depth,
        };
        return scanner.ScanAsync(ctx, null, CancellationToken.None);
    }

    [Fact]
    public void Scanner_identity()
    {
        var s = new SpaceMapScanner();
        Assert.Equal("spacemap", s.FeatureId);
        Assert.True(s.IncludedInOptimizeAll);
        Assert.Equal(Release.V1, s.Release);
    }

    [Fact]
    public async Task Two_drive_pc_suggests_moving_big_old_files_to_the_other_drive()
    {
        var pc = MakePc(SandboxProfile.Default);
        var result = await RunScanner(pc);

        var group = Assert.Single(result.Groups);
        Assert.Equal(SpaceMapScanner.BigAndOldGroupId, group.Id);
        Assert.Equal(Badge.Check, group.Badge);
        Assert.Equal(new[] { "wedding.mov", "installer.iso" }, group.Items.Select(i => i.Title).ToArray());
        Assert.All(group.Items, i =>
        {
            Assert.Equal(Badge.Check, i.Badge);
            Assert.False(i.DefaultSelected);
            var action = Assert.Single(i.Actions);
            Assert.Equal(ActionKind.MovePath, action.Kind);
            Assert.Equal("spacemap", action.FeatureId);
            Assert.True(action.Reversible);
            Assert.Equal(i.SizeBytes, action.ExpectedSize);
            Assert.NotNull(action.ExpectedLastWriteUtc);
            Assert.False(string.IsNullOrWhiteSpace(i.Reason));
        });
        var move = group.Items[0].Actions[0];
        Assert.Equal(Path.Combine(pc.Loc.UserProfile, @"Videos\2023\wedding.mov"), move.SourcePath);
        Assert.Equal(Path.Combine(SandboxRoot, @"D\Moved by PC Tidy\Users\tester\Videos\2023\wedding.mov"), move.DestinationPath);
    }

    [Fact]
    public async Task One_drive_pc_never_suggests_a_move()
    {
        var pc = MakePc(SandboxProfile.Minimal);
        Assert.Single(pc.Loc.FixedDrives);
        var result = await RunScanner(pc);
        var items = Assert.Single(result.Groups).Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(ActionKind.QuarantinePath, Assert.Single(i.Actions).Kind));
        Assert.All(items, i => Assert.DoesNotContain("Move", i.Reason));
    }

    [Fact]
    public async Task Second_drive_without_room_falls_back_to_quarantine()
    {
        var pc = MakePc(SandboxProfile.Default, dFree: 3 * GB); // 4 GB file + 2 GB headroom won't fit
        var items = Assert.Single((await RunScanner(pc)).Groups).Items;
        Assert.Equal(ActionKind.QuarantinePath, items.Single(i => i.Title == "wedding.mov").Actions[0].Kind);
        Assert.Equal(ActionKind.MovePath, items.Single(i => i.Title == "installer.iso").Actions[0].Kind);
    }

    [Fact]
    public async Task Nothing_big_and_old_gives_no_groups()
    {
        var loc = new SandboxKnownLocations(SandboxRoot, SandboxProfile.Minimal);
        var tree = new FakeTree(loc.UserProfile);
        tree.Facts.Dirs.Add(tree.Root);
        tree.File("tiny.txt", 10, Old);
        var result = await RunScanner(new Pc(loc, tree, new FakeSafety(), new FakeDrives()));
        Assert.Empty(result.Groups);
    }

    // ---------------- SpaceActions ----------------

    private static (SpaceIndex Index, SpaceActions Actions, Pc Pc) Indexed(SandboxProfile profile)
    {
        var pc = MakePc(profile);
        pc.Tree.Dir("junction", FileAttributes.Directory | FileAttributes.ReparsePoint);
        pc.Tree.File(@"OneDrive\online.mp4", 2 * GB, Old, attrs: FileAttributes.ReparsePoint | (FileAttributes)0x00400000);
        pc.Tree.File(@"AppData\Local\Programs\App\app.exe", 50 * MB, Old);
        pc.Safety.Never.Add(Path.Combine(pc.Loc.UserProfile, @"AppData\Local\Programs"));
        var ix = new SpaceIndexScanner(pc.Tree.Walker, pc.Safety, new FakeTags(), pc.Drives)
            .Scan(pc.Tree.Root, new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);
        return (ix, new SpaceActions(pc.Loc, pc.Safety, pc.Drives, 7, pc.Tree.Facts), pc);
    }

    [Fact]
    public void Locked_items_explain_why()
    {
        var (ix, actions, pc) = Indexed(SandboxProfile.Default);
        string P(string rel) => Path.Combine(pc.Loc.UserProfile, rel);
        Assert.NotNull(actions.WhyLocked(ix, SpaceIndex.RootNode));
        Assert.Contains("link", actions.WhyLocked(ix, ix.Find(P("junction")))!);
        Assert.Equal("Windows needs this folder.", actions.WhyLocked(ix, ix.Find(P(@"AppData\Local\Programs\App\app.exe"))));
        Assert.Contains("Online-only", actions.WhyLocked(ix, ix.Find(P(@"OneDrive\online.mp4")))!);
        Assert.Contains("main Windows folder", actions.WhyLocked(ix, ix.Find(P("Downloads")))!);
        Assert.Null(actions.WhyLocked(ix, ix.Find(P(@"Downloads\installer.iso"))));
        Assert.Null(actions.WhyLocked(ix, ix.Find(P(@"Videos\2023"))));

        var blocked = actions.Quarantine(ix, ix.Find(P(@"AppData\Local\Programs\App\app.exe")));
        Assert.False(blocked.Ok);
        Assert.NotNull(blocked.WhyNot);
    }

    [Fact]
    public void Quarantine_item_is_reversible_and_reverified()
    {
        var (ix, actions, pc) = Indexed(SandboxProfile.Minimal);
        var r = actions.Quarantine(ix, ix.Find(Path.Combine(pc.Loc.UserProfile, @"Videos\2023")));
        Assert.True(r.Ok, r.WhyNot);
        Assert.Equal(ActionKind.QuarantinePath, r.Item!.Kind);
        Assert.True(r.Item.IsDirectory);
        Assert.True(r.Item.Reversible);
        Assert.Equal(4 * GB, r.Item.ExpectedSize);
        Assert.Contains("7 days", r.Item.Description);
    }

    [Fact]
    public void Vanished_file_is_refused()
    {
        var (ix, actions, pc) = Indexed(SandboxProfile.Default);
        var path = Path.Combine(pc.Loc.UserProfile, @"Downloads\installer.iso");
        pc.Tree.Facts.Files.Remove(path);
        Assert.Contains("not there", actions.Quarantine(ix, ix.Find(path)).WhyNot);
    }

    [Fact]
    public void Move_targets_come_only_from_other_fixed_drives_with_room()
    {
        var (ix, actions, pc) = Indexed(SandboxProfile.Default);
        var file = Path.Combine(pc.Loc.UserProfile, @"Downloads\installer.iso");
        var target = Assert.Single(actions.MoveTargets(file, 700 * MB));
        Assert.Equal(pc.Loc.FixedDrives[1], target.DriveRoot);
        Assert.Empty(actions.MoveTargets(file, 499 * GB));                     // too big for D:
        Assert.False(actions.MoveInto(ix, ix.Find(file), Path.Combine(SandboxRoot, @"C\Elsewhere")).Ok); // same drive
        var ok = actions.MoveInto(ix, ix.Find(file), Path.Combine(SandboxRoot, @"D\Archive"));
        Assert.True(ok.Ok, ok.WhyNot);
        Assert.Equal(Path.Combine(SandboxRoot, @"D\Archive\installer.iso"), ok.Item!.DestinationPath);

        pc.Safety.Never.Add(Path.Combine(SandboxRoot, @"D\SteamLibrary"));
        Assert.False(actions.MoveInto(ix, ix.Find(file), Path.Combine(SandboxRoot, @"D\SteamLibrary\x")).Ok);
    }

    [Fact]
    public void One_drive_pc_has_no_move_targets()
    {
        var (ix, actions, pc) = Indexed(SandboxProfile.Minimal);
        var file = Path.Combine(pc.Loc.UserProfile, @"Downloads\installer.iso");
        Assert.Empty(actions.MoveTargets(file, 1));
        var r = actions.MoveInto(ix, ix.Find(file), Path.Combine(SandboxRoot, @"D\Archive"));
        Assert.False(r.Ok);
        Assert.Contains("No other drive", r.WhyNot);
    }

    [Fact]
    public void Missing_space_report_names_the_parts()
    {
        var t = new FakeTree(@"X:\");
        t.File("pagefile.sys", 8 * GB, attrs: FileAttributes.System | FileAttributes.Hidden);
        t.File("hiberfil.sys", 13 * GB, attrs: FileAttributes.System | FileAttributes.Hidden);
        t.File(@"Windows\WinSxS\x.dll", 7 * GB);
        t.Dir("System Volume Information", listable: false);
        var drives = new FakeDrives { ScanRoot = new DriveSpace(1024 * GB, 900 * GB) };
        var ix = new SpaceIndexScanner(t.Walker, new FakeSafety(), new FakeTags(), drives)
            .Scan(t.Root, new SpaceScanOptions { ClusterSize = 4096 }, null, CancellationToken.None);
        var report = MissingSpace.Explain(ix, t.Facts);

        Assert.Equal(124 * GB - 28 * GB, report.UnscannedBytes);
        Assert.Equal(13 * GB, report.Parts.Single(p => p.Name == "Hibernation file").Bytes);
        Assert.Equal(8 * GB, report.Parts.Single(p => p.Name == "Page file").Bytes);
        Assert.Equal(MeasuredBy.NeedsAdmin, report.Parts.Single(p => p.Name.StartsWith("Restore points")).How);
        Assert.Equal(MeasuredBy.Estimated, report.Parts.Single(p => p.Name.Contains("WinSxS")).How);
        Assert.Contains(report.Parts, p => p.Name.Contains("can't open"));
        var text = MissingSpace.Format(report);
        Assert.Contains("Big hidden Windows files", text);
        Assert.Contains("measured with admin", text);
    }
}
