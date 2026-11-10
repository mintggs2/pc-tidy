using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;
using Xunit;
using static PcTidy.Engine.Tests.TreeTools;

namespace PcTidy.Engine.Tests;

public class QuarantineStoreTests
{
    private const string Run = "20260101-000000-test01";

    private static QuarantineStore Store(TestSandbox sb, bool forceCopy)
        => new(sb.Settings.DataRoot, sb.Locations, sb.Settings) { ForceCopyIntoDataRoot = forceCopy };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_round_trip_restores_exact_bytes_times_and_attributes(bool crossVolume)
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "report.pdf"), 1_300_000, 42, extra: FileAttributes.ReadOnly | FileAttributes.Hidden);
        var before = FileSnapshot(file);
        var store = Store(sb, crossVolume);

        var entry = await store.QuarantineAsync(file, Run, default);
        Assert.False(Exists(file));
        Assert.True(Exists(entry.StoredPath));
        Assert.StartsWith(store.DataStoreRoot, entry.StoredPath);
        Assert.Equal(1_300_000, entry.SizeBytes);
        // A copy is proven by XXH3; a rename doesn't touch the bytes, so it's identified by size + modified time.
        if (crossVolume) Assert.NotNull(entry.Hash);
        else Assert.Null(entry.Hash);

        var restored = await store.RestoreAsync(entry, default);
        Assert.Equal(file, restored);
        Assert.Equal(before, FileSnapshot(file));
        Assert.False(Exists(entry.StoredPath));
        var listed = Assert.Single(await store.ListAsync(Run, default));
        Assert.True(listed.Restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Folder_round_trip_is_byte_for_byte_including_odd_names(bool crossVolume)
    {
        using var sb = new TestSandbox();
        var folder = MakeTree(Path.Combine(sb.Work(), "Project Files"));
        var before = Snapshot(folder);
        var folderAttrs = File.GetAttributes(LongPath.Io(folder));
        var store = Store(sb, crossVolume);

        var entry = await store.QuarantineAsync(folder, Run, default);
        Assert.False(Exists(folder));
        Assert.True(entry.IsDirectory);
        Assert.Equal(before.Count(l => l.StartsWith("F|")), entry.FileCount);
        Assert.Equal(before, Snapshot(entry.StoredPath));

        await store.RestoreAsync(entry, default);
        Assert.Equal(before, Snapshot(folder));
        Assert.Equal(folderAttrs, File.GetAttributes(LongPath.Io(folder)));
    }

    [Fact]
    public async Task Restore_never_overwrites_it_numbers_restored_copies()
    {
        using var sb = new TestSandbox();
        var work = sb.Work();
        var store = Store(sb, false);
        var path = Path.Combine(work, "notes.txt");

        WriteText(path, "first");
        var e1 = await store.QuarantineAsync(path, Run, default);
        WriteText(path, "second");
        var e2 = await store.QuarantineAsync(path, Run, default);
        WriteText(path, "new file in the way");

        var r1 = await store.RestoreAsync(e1, default);
        var r2 = await store.RestoreAsync(e2, default);
        Assert.Equal(Path.Combine(work, "notes (restored).txt"), r1);
        Assert.Equal(Path.Combine(work, "notes (restored 2).txt"), r2);
        Assert.Equal("new file in the way", File.ReadAllText(path));
        Assert.Equal("first", File.ReadAllText(r1));
        Assert.Equal("second", File.ReadAllText(r2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_paths_over_260_work_for_files_and_folders(bool crossVolume)
    {
        using var sb = new TestSandbox();
        var deep = LongFolder(sb.Work());
        var file = WriteFile(Path.Combine(deep, "deep file.txt"), 5000, 3);
        Assert.True(file.Length > 260);
        var folder = MakeTree(Path.Combine(sb.Work(), "holder"), oddNames: false);
        WriteFile(Path.Combine(LongFolder(folder), "inner.bin"), 2048, 4);
        foreach (var d in Directory.EnumerateDirectories(LongPath.Io(folder), "level*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(d, OldTime); // pin folder times (NTFS updates them lazily)
        var fileBefore = FileSnapshot(file);
        var treeBefore = Snapshot(folder);
        var store = Store(sb, crossVolume);

        var fe = await store.QuarantineAsync(file, Run, default);
        var de = await store.QuarantineAsync(folder, Run, default);
        Assert.False(Exists(file));
        Assert.False(Exists(folder));

        await store.RestoreAsync(fe, default);
        await store.RestoreAsync(de, default);
        Assert.Equal(fileBefore, FileSnapshot(file));
        Assert.Equal(treeBefore, Snapshot(folder));
    }

    [Fact]
    public async Task Online_only_cloud_file_is_refused_and_left_alone()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "cloud.docx"), 100, 1);
        File.SetAttributes(LongPath.Io(file), FileAttributes.Offline); // what an online-only placeholder reports
        var store = Store(sb, false);

        var ex = await Assert.ThrowsAsync<SafetyRefusalException>(() => store.QuarantineAsync(file, Run, default));
        Assert.Contains("online-only", ex.Message);
        Assert.True(Exists(file));
        Assert.Empty(await store.ListAsync(null, default));
    }

    [Fact]
    public async Task Folder_containing_a_placeholder_is_refused()
    {
        using var sb = new TestSandbox();
        var folder = Path.Combine(sb.Work(), "OneDriveish");
        WriteFile(Path.Combine(folder, "local.txt"), 10, 1);
        var cloud = WriteFile(Path.Combine(folder, "remote.txt"), 10, 2);
        File.SetAttributes(LongPath.Io(cloud), FileAttributes.Offline);

        await Assert.ThrowsAsync<SafetyRefusalException>(() => Store(sb, false).QuarantineAsync(folder, Run, default));
        Assert.True(Exists(cloud));
    }

    [Fact]
    public async Task Cross_volume_copy_refuses_folders_with_links_inside_but_same_volume_moves_them_intact()
    {
        using var sb = new TestSandbox();
        var target = MakeTree(Path.Combine(sb.Work(), "target"), oddNames: false);
        var folder = Path.Combine(sb.Work(), "has link");
        WriteFile(Path.Combine(folder, "a.txt"), 10, 1);
        Junction.Create(Path.Combine(folder, "link"), target);
        var targetBefore = Snapshot(target);

        await Assert.ThrowsAsync<SafetyRefusalException>(() => Store(sb, true).QuarantineAsync(folder, Run, default));
        Assert.True(Exists(Path.Combine(folder, "a.txt")));

        var entry = await Store(sb, false).QuarantineAsync(folder, Run, default);
        Assert.True(Junction.PointsTo(Path.Combine(entry.StoredPath, "link"), target));
        await Store(sb, false).PurgeRunAsync(Run, default);   // purge removes the link, never what it points to
        Assert.Equal(targetBefore, Snapshot(target));
    }

    [Fact]
    public async Task Cross_volume_with_an_undeletable_original_keeps_the_verified_copy_and_reports_partial()
    {
        using var sb = new TestSandbox();
        var folder = Path.Combine(sb.Work(), "busy");
        WriteFile(Path.Combine(folder, "a.txt"), 100, 1);
        var held = WriteFile(Path.Combine(folder, "b.txt"), 100, 2, extra: FileAttributes.ReadOnly);
        WriteFile(Path.Combine(folder, "c.txt"), 100, 3);
        var store = Store(sb, true);

        PartialQuarantineException ex;
        // Another app has b.txt open (it can read, but nobody may delete it).
        using (new FileStream(LongPath.Io(held), FileMode.Open, FileAccess.Read, FileShare.Read))
            ex = await Assert.ThrowsAsync<PartialQuarantineException>(() => store.QuarantineAsync(folder, Run, default));

        Assert.Contains(held, ex.LeftBehind);
        Assert.True(Exists(held));                                       // the original that couldn't go is still there
        Assert.True((File.GetAttributes(LongPath.Io(held)) & FileAttributes.ReadOnly) != 0); // untouched, read-only kept
        Assert.Equal(3, Directory.GetFiles(LongPath.Io(ex.Entry.StoredPath)).Length); // and the copy is complete
        var listed = Assert.Single(await store.ListAsync(Run, default));
        Assert.Equal(ex.Entry.Id, listed.Id);
    }

    [Fact]
    public async Task Tampered_payload_is_not_restored_unless_forced()
    {
        using var sb = new TestSandbox();
        var file = WriteFile(Path.Combine(sb.Work(), "x.bin"), 1000, 1);
        var store = Store(sb, false);
        var entry = await store.QuarantineAsync(file, Run, default);
        File.WriteAllBytes(LongPath.Io(entry.StoredPath), new byte[1000]); // same size, different bytes + time

        await Assert.ThrowsAsync<QuarantineIntegrityException>(() => store.RestoreAsync(entry, default));
        Assert.False(Exists(file));
        Assert.Equal(file, await store.RestoreAsync(entry, force: true, default));
    }

    [Fact]
    public async Task Purge_deletes_payload_keeps_manifest_and_updates_totals()
    {
        using var sb = new TestSandbox();
        var store = Store(sb, false);
        var a = await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), "a.bin"), 1000, 1, extra: FileAttributes.ReadOnly), Run, default);
        await store.QuarantineAsync(MakeTree(Path.Combine(sb.Work(), "tree"), oddNames: true), "20260101-000001-other1", default);
        var total = await store.TotalBytesAsync(default);
        Assert.True(total > 1000);

        await store.PurgeRunAsync(Run, default);
        Assert.False(Exists(a.StoredPath));
        var purged = Assert.Single(await store.ListAsync(Run, default));
        Assert.True(purged.Purged);
        Assert.Equal(total - 1000, await store.TotalBytesAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(purged, default));
    }

    [Fact]
    public async Task Purge_helpers_list_by_age_and_by_size_cap_and_never_purge_on_their_own()
    {
        using var sb = new TestSandbox();
        var store = Store(sb, false);
        string[] runs = { "20260101-000000-aaaaaa", "20260102-000000-bbbbbb", "20260103-000000-cccccc" };
        for (int i = 0; i < runs.Length; i++)
        {
            await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), $"f{i}.bin"), 1000 * (i + 1), i), runs[i], default);
            await Task.Delay(20);
        }

        var forCap = await store.RunsToPurgeForSizeCapAsync(4000, default);
        Assert.Equal(new[] { runs[0], runs[1] }, forCap.Select(r => r.RunId));   // oldest first until 3000 left
        Assert.Empty(await store.RunsToPurgeForSizeCapAsync(6000, default));
        Assert.Empty(await store.RunsOlderThanAsync(TimeSpan.FromDays(7), default));
        Assert.Equal(6000, await store.TotalBytesAsync(default));               // listing changed nothing

        var purged = await store.PurgeOlderThanAsync(TimeSpan.Zero, default);
        Assert.Equal(3, purged.Count);
        Assert.Equal(0, await store.TotalBytesAsync(default));
    }

    [Fact]
    public async Task Item_already_in_quarantine_is_refused()
    {
        using var sb = new TestSandbox();
        var store = Store(sb, false);
        var e = await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1), Run, default);
        await Assert.ThrowsAsync<SafetyRefusalException>(() => store.QuarantineAsync(e.StoredPath, Run, default));
    }

    // -------------------------------------------------------------- per-drive store

    [Fact]
    public async Task Per_drive_store_is_used_for_files_on_a_non_system_drive_and_restores()
    {
        using var sb = new TestSandbox(SandboxProfile.Default);
        var file = WriteFile(Path.Combine(sb.SecondDrive(), "movie.mkv"), 50_000, 1);
        var before = FileSnapshot(file);
        var store = Store(sb, forceCopy: true); // even with the data folder "on another drive", the drive store is a rename

        var entry = await store.QuarantineAsync(file, Run, default);
        var volumeStore = Path.Combine(sb.Locations.FixedDrives[1], QuarantineStore.VolumeFolderName);
        Assert.StartsWith(volumeStore, entry.StoredPath);
        Assert.True((File.GetAttributes(LongPath.Io(volumeStore)) & FileAttributes.Hidden) != 0);
        var acl = new DirectoryInfo(LongPath.Io(volumeStore)).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); // private to this user (+ SYSTEM, Administrators)
        Assert.Equal(3, acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
        Assert.True(store.ReadManifestFor(entry)!.SameVolume);
        Assert.Single(await store.ListAsync(null, default));

        await store.RestoreAsync(entry, default);
        Assert.Equal(before, FileSnapshot(file));
    }

    [Fact]
    public async Task Per_drive_store_is_not_used_on_the_system_drive_by_default_or_when_turned_off()
    {
        using var sb = new TestSandbox(SandboxProfile.Default);
        var store = Store(sb, false);
        var onSystem = await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1), Run, default);
        Assert.StartsWith(store.DataStoreRoot, onSystem.StoredPath);

        new EngineSettings { PerVolumeQuarantine = PerVolumeQuarantine.Off }.SaveTo(sb.Settings);
        var off = await store.QuarantineAsync(WriteFile(Path.Combine(sb.SecondDrive(), "b.txt"), 10, 2), Run, default);
        Assert.StartsWith(store.DataStoreRoot, off.StoredPath);

        new EngineSettings { PerVolumeQuarantine = PerVolumeQuarantine.AllFixedDrives }.SaveTo(sb.Settings);
        var all = await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), "c.txt"), 10, 3), Run, default);
        Assert.StartsWith(Path.Combine(sb.Locations.SystemDrive, QuarantineStore.VolumeFolderName), all.StoredPath);
        Assert.Equal(3, (await store.ListAsync(Run, default)).Count);
    }

    [Fact]
    public async Task One_drive_pc_uses_the_data_folder()
    {
        using var sb = new TestSandbox(SandboxProfile.Minimal);
        var store = Store(sb, false);
        var e = await store.QuarantineAsync(WriteFile(Path.Combine(sb.Work(), "a.txt"), 10, 1), Run, default);
        Assert.StartsWith(store.DataStoreRoot, e.StoredPath);
        Assert.Single(sb.Locations.FixedDrives);
    }
}
