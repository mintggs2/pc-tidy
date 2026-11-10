using PcTidy.Core;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Tests;

public class SpaceIndexTests
{
    private static readonly SpaceScanOptions NoDrive = new() { ClusterSize = 4096, IncludeUnscannedNode = false };

    private static SpaceIndex Scan(FakeTree tree, FakeSafety? safety = null, SpaceScanOptions? options = null, FakeTags? tags = null, FakeDrives? drives = null)
        => new SpaceIndexScanner(tree.Walker, safety ?? new FakeSafety(), tags ?? new FakeTags(), drives ?? new FakeDrives())
            .Scan(tree.Root, options ?? NoDrive, null, CancellationToken.None);

    [Fact]
    public void Folder_sizes_counts_and_disk_sizes_are_aggregated()
    {
        var t = new FakeTree(@"X:\Agg");
        t.File(@"a\one.bin", 1000);
        t.File(@"a\b\two.bin", 5000);
        t.File(@"a\b\c\three.bin", 4096);
        t.File(@"top.txt", 1);
        t.File(@"empty.txt", 0);
        t.Dir(@"emptydir");

        var ix = Scan(t);
        int a = ix.Find(@"X:\Agg\a"), b = ix.Find(@"X:\Agg\a\b");
        Assert.Equal(10096, ix.Size(a));
        Assert.Equal(9096, ix.Size(b));
        Assert.Equal(3, ix.FileCount(a));
        Assert.Equal(4096 + 8192 + 4096, ix.SizeOnDisk(a));    // cluster-rounded
        Assert.Equal(10097, ix.Size(SpaceIndex.RootNode));
        Assert.Equal(5, ix.Summary.Files);
        Assert.Equal(0, ix.SizeOnDisk(ix.Find(@"X:\Agg\empty.txt")));
        Assert.Equal(ix.Summary.ScannedBytes, ix.Size(SpaceIndex.RootNode));

        // Every folder equals the sum of its children, at every level.
        for (int n = 0; n < ix.Count; n++)
            if (ix.IsDirectory(n))
            {
                long sum = 0; long disk = 0; int files = 0;
                foreach (var c in ix.Children(n)) { sum += ix.Size(c); disk += ix.SizeOnDisk(c); files += ix.FileCount(c); }
                Assert.Equal(sum, ix.Size(n));
                Assert.Equal(disk, ix.SizeOnDisk(n));
                Assert.Equal(files, ix.FileCount(n));
            }
    }

    [Fact]
    public void Children_are_sorted_biggest_first_and_paths_round_trip()
    {
        var t = new FakeTree(@"X:\Sort");
        t.File("small.bin", 10);
        t.File("big.bin", 900);
        t.File(@"mid\x.bin", 500);
        var ix = Scan(t);
        var names = ix.Children(SpaceIndex.RootNode).ToArray().Select(ix.Name).ToArray();
        Assert.Equal(new[] { "big.bin", "mid", "small.bin" }, names);
        int x = ix.Find(@"X:\Sort\mid\x.bin");
        Assert.Equal(@"X:\Sort\mid\x.bin", ix.FullPath(x));
        Assert.Equal(2, ix.Depth(x));
        Assert.True(ix.IsAncestorOrSelf(ix.Find(@"X:\Sort\mid"), x));
    }

    [Fact]
    public void Last_touched_is_newer_of_modified_and_created_and_rolls_up()
    {
        var t = new FakeTree(@"X:\Time");
        var created = new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        t.File(@"d\copied.bin", 10, touched: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), created: created);
        var ix = Scan(t);
        Assert.Equal(created, ix.LastTouchedUtc(ix.Find(@"X:\Time\d\copied.bin")));
        Assert.Equal(created, ix.LastTouchedUtc(ix.Find(@"X:\Time\d")));
    }

    [Fact]
    public void Junctions_and_symlinks_are_zero_byte_links_and_never_walked()
    {
        var t = new FakeTree(@"X:\Links");
        var junction = t.Dir("junction", FileAttributes.Directory | FileAttributes.ReparsePoint);
        t.File(@"junction\inside.bin", 1_000_000); // would only be seen if followed
        var symlinkFile = t.File("link.txt", 0, attrs: FileAttributes.ReparsePoint);
        t.File("real.bin", 100);
        var tags = new FakeTags();
        tags.Tags[junction] = FakeTags.MountPoint;
        tags.Tags[symlinkFile] = FakeTags.Symlink;

        var ix = Scan(t, tags: tags);
        int j = ix.Find(junction);
        Assert.True(ix.Has(j, SpaceNodeFlags.Link));
        Assert.Equal(0, ix.Size(j));
        Assert.Equal(0, ix.ChildCount(j));
        Assert.True(ix.Has(ix.Find(symlinkFile), SpaceNodeFlags.Link));
        Assert.Equal(100, ix.Size(SpaceIndex.RootNode));
        Assert.Equal(2, ix.Summary.Links);
    }

    [Fact]
    public void Unknown_reparse_points_are_treated_as_links()
    {
        var t = new FakeTree(@"X:\Unknown");
        t.Dir("mystery", FileAttributes.Directory | FileAttributes.ReparsePoint);
        t.File(@"mystery\x.bin", 50);
        var ix = Scan(t); // no tag known → never followed
        Assert.True(ix.Has(ix.Find(@"X:\Unknown\mystery"), SpaceNodeFlags.Link));
        Assert.Equal(0, ix.Size(SpaceIndex.RootNode));
    }

    [Fact]
    public void Cloud_folders_are_walked_and_online_only_files_take_no_disk_space()
    {
        var t = new FakeTree(@"X:\Cloud");
        var od = t.Dir("OneDrive", FileAttributes.Directory | FileAttributes.ReparsePoint);
        t.File(@"OneDrive\local.docx", 10_000, attrs: FileAttributes.ReparsePoint);
        t.File(@"OneDrive\online.mp4", 50_000_000, attrs: FileAttributes.ReparsePoint | (FileAttributes)0x00400000);
        var tags = new FakeTags();
        tags.Tags[od] = FakeTags.CloudFiles;
        tags.Tags[Path.Combine(od, "local.docx")] = FakeTags.CloudFiles;

        var ix = Scan(t, tags: tags, options: NoDrive with { SplitDepth = 0 });
        int online = ix.Find(Path.Combine(od, "online.mp4"));
        Assert.True(ix.Has(online, SpaceNodeFlags.CloudPlaceholder));
        Assert.Equal(50_000_000, ix.Size(online));
        Assert.Equal(0, ix.SizeOnDisk(online));
        Assert.False(ix.Has(ix.Find(Path.Combine(od, "local.docx")), SpaceNodeFlags.Link));
        Assert.Equal(12288, ix.SizeOnDisk(ix.Find(od)));
    }

    [Fact]
    public void Protected_folders_are_flagged_with_their_contents_and_checked_once()
    {
        var t = new FakeTree(@"X:\Prot");
        t.File(@"Windows\System32\a.dll", 10);
        t.File(@"Windows\System32\deep\b.dll", 10);
        t.File(@"Users\tester\c.txt", 10);
        t.File(@"pagefile.sys", 4096, attrs: FileAttributes.System | FileAttributes.Hidden);
        t.File(@"OneDrive\d.txt", 10);
        var safety = new FakeSafety();
        safety.Never.Add(@"X:\Prot\Windows");
        safety.AskFirst.Add(@"X:\Prot\OneDrive");

        var ix = Scan(t, safety);
        Assert.True(ix.Has(ix.Find(@"X:\Prot\Windows"), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(ix.Find(@"X:\Prot\Windows\System32\deep\b.dll"), SpaceNodeFlags.Protected));
        Assert.False(ix.Has(ix.Find(@"X:\Prot\Users\tester\c.txt"), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(ix.Find(@"X:\Prot\pagefile.sys"), SpaceNodeFlags.Protected)); // System attribute
        Assert.True(ix.Has(ix.Find(@"X:\Prot\OneDrive\d.txt"), SpaceNodeFlags.AskFirst));
        // Folders are classified, files and deeper protected contents are not: root, Users, tester, OneDrive; Windows and
        // System32 (Windows' direct sub-folders are asked once, in case one is yours, like your profile inside Users),
        // each with one probe inside to learn that their contents are protected too.
        Assert.Equal(8, safety.ClassifyCalls);
        // Nothing here is ask-first or blocked-as-a-whole, so the cloud question is never asked.
        Assert.Equal(0, safety.CloudCalls);
    }

    [Fact]
    public void Folders_blocked_only_as_a_whole_keep_their_contents_actionable()
    {
        var t = new FakeTree(@"X:\Part");
        t.File(@"Users\tester\Downloads\movie.mkv", 10);
        t.File(@"Users\tester\AppData\Local\Programs\App\app.exe", 10);
        t.File(@"Users\tester\Downloads\desktop.ini", 10, attrs: FileAttributes.System | FileAttributes.Hidden);
        var safety = new FakeSafety();
        safety.ExactOnly.Add(@"X:\Part");
        safety.ExactOnly.Add(@"X:\Part\Users");
        safety.ExactOnly.Add(@"X:\Part\Users\tester\Downloads");
        safety.Never.Add(@"X:\Part\Users\tester\AppData\Local\Programs");

        var ix = Scan(t, safety);
        int users = ix.Find(@"X:\Part\Users"), downloads = ix.Find(@"X:\Part\Users\tester\Downloads");
        Assert.True(ix.Has(SpaceIndex.RootNode, SpaceNodeFlags.SelfLocked));
        Assert.True(ix.Has(users, SpaceNodeFlags.SelfLocked));
        Assert.False(ix.Has(users, SpaceNodeFlags.Protected));
        Assert.True(ix.Has(users, SpaceNodeFlags.ContainsProtected));     // Programs is somewhere inside
        Assert.True(ix.Has(downloads, SpaceNodeFlags.SelfLocked));
        // Its desktop.ini is a protected system file, but a stray file doesn't make Downloads "contain protected folders".
        Assert.True(ix.Has(ix.Find(@"X:\Part\Users\tester\Downloads\desktop.ini"), SpaceNodeFlags.Protected));
        Assert.False(ix.Has(downloads, SpaceNodeFlags.ContainsProtected));
        Assert.False(ix.Has(ix.Find(@"X:\Part\Users\tester\Downloads\movie.mkv"), SpaceNodeFlags.Protected | SpaceNodeFlags.SelfLocked));
        Assert.True(ix.Has(ix.Find(@"X:\Part\Users\tester\AppData\Local\Programs\App\app.exe"), SpaceNodeFlags.Protected));
        Assert.True(ix.Has(ix.Find(@"X:\Part\Users\tester"), SpaceNodeFlags.ContainsProtected));
    }

    [Fact]
    public void Quick_scanner_mode_skips_protected_contents()
    {
        var t = new FakeTree(@"X:\Skip");
        t.File(@"Windows\big.bin", 1000);
        t.File(@"Users\x.bin", 10);
        var safety = new FakeSafety();
        safety.Never.Add(@"X:\Skip\Windows");
        var ix = Scan(t, safety, NoDrive with { SkipProtectedContents = true });
        int w = ix.Find(@"X:\Skip\Windows");
        Assert.True(ix.Has(w, SpaceNodeFlags.NotWalked));
        Assert.Equal(0, ix.ChildCount(w));
        Assert.Equal(10, ix.Size(SpaceIndex.RootNode));
    }

    [Fact]
    public void Unscanned_block_is_drive_used_minus_scanned_on_disk()
    {
        var t = new FakeTree(@"X:\");
        t.File(@"a.bin", 4096 * 3);
        var drives = new FakeDrives { ScanRoot = new DriveSpace(1_000_000, 900_000) };
        var ix = Scan(t, drives: drives, options: NoDrive with { IncludeUnscannedNode = true });
        Assert.True(ix.UnscannedNode > 0);
        Assert.True(ix.Has(ix.UnscannedNode, SpaceNodeFlags.Pseudo | SpaceNodeFlags.Protected));
        Assert.Equal(100_000 - 12_288, ix.Size(ix.UnscannedNode));
        Assert.Equal(100_000, ix.SizeOnDisk(SpaceIndex.RootNode));
        Assert.Null(ix.FullPath(ix.UnscannedNode));
        Assert.Equal(100_000, ix.Summary.DriveUsedBytes);
    }

    [Fact]
    public void Unreadable_folders_are_counted()
    {
        var t = new FakeTree(@"X:\Denied");
        t.Dir("locked", listable: false);
        t.File("ok.bin", 5);
        var ix = Scan(t);
        Assert.Equal(1, ix.Summary.Inaccessible);
        Assert.Equal(5, ix.Size(SpaceIndex.RootNode));
    }

    [Fact]
    public void Parallel_and_single_threaded_scans_agree()
    {
        var walker = SyntheticTree.Walker();
        var one = new SpaceIndexScanner(walker, new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(SyntheticTree.Root, NoDrive with { MaxParallelism = 1, SplitDepth = 0 }, null, CancellationToken.None);
        var many = new SpaceIndexScanner(walker, new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(SyntheticTree.Root, NoDrive with { MaxParallelism = 8, SplitDepth = 2 }, null, CancellationToken.None);
        Assert.Equal(one.Count, many.Count);
        Assert.Equal(SyntheticTree.ExpectedFiles, many.Summary.Files);
        Assert.Equal(one.Size(SpaceIndex.RootNode), many.Size(SpaceIndex.RootNode));
        Assert.Equal(one.SizeOnDisk(SpaceIndex.RootNode), many.SizeOnDisk(SpaceIndex.RootNode));
        var path = one.FullPath(one.Children(one.Children(SpaceIndex.RootNode)[0])[0])!;
        Assert.Equal(one.Size(one.Find(path)), many.Size(many.Find(path)));
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var walker = new ListingWalker(_ => { cts.Cancel(); return Array.Empty<PcTidy.Core.FileEntry>(); });
        var scanner = new SpaceIndexScanner(walker, new FakeSafety(), new FakeTags(), new FakeDrives());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(@"X:\Cancel", NoDrive, null, cts.Token));
    }

    [Fact]
    public void Progress_is_reported()
    {
        var reports = new List<SpaceScanProgress>();
        var progress = new SyncProgress<SpaceScanProgress>(reports.Add);
        new SpaceIndexScanner(SyntheticTree.Walker(), new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(SyntheticTree.Root, NoDrive, progress, CancellationToken.None);
        Assert.NotEmpty(reports);
        Assert.Equal(SyntheticTree.ExpectedFiles, reports[^1].Files);
    }

    [Fact]
    public void Type_breakdown_and_top_files()
    {
        var t = new FakeTree(@"X:\Types");
        t.File("a.MP4", 3000);
        t.File(@"d\b.mp4", 2000);
        t.File(@"d\c.jpg", 500);
        t.File(@"d\e\noext", 7000);
        t.File(@"game\data.pak", 100);
        var ix = new SpaceIndexScanner(t.Walker, new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(t.Root, NoDrive with { GameRoots = new[] { @"X:\Types\game" } }, null, CancellationToken.None);

        var byExt = SpaceQueries.ByExtension(ix, SpaceIndex.RootNode);
        Assert.Equal("", byExt[0].Extension);
        var mp4 = byExt.Single(e => e.Extension == "mp4");
        Assert.Equal(5000, mp4.Bytes);
        Assert.Equal(2, mp4.Files);
        Assert.Equal(FileCategory.Videos, mp4.Category);

        var cats = SpaceQueries.ByCategory(ix, SpaceIndex.RootNode);
        Assert.Equal(100, cats.Single(c => c.Category == FileCategory.Games).Bytes);
        Assert.Equal(FileCategory.Videos, ix.Category(ix.Find(@"X:\Types\a.MP4")));

        var top = SpaceQueries.TopFiles(ix, SpaceIndex.RootNode, 3).Select(ix.Name).ToArray();
        Assert.Equal(new[] { "noext", "a.MP4", "b.mp4" }, top);
        Assert.Equal(FileCategory.Other, ix.Category(ix.Find(@"X:\Types\d\e")));
    }

    [Fact]
    public void Folder_colour_is_its_dominant_type()
    {
        var t = new FakeTree(@"X:\Dom");
        t.File(@"v\a.mp4", 9000);
        t.File(@"v\b.jpg", 10);
        t.File(@"v\c.txt", 10);
        var ix = Scan(t);
        Assert.Equal(FileCategory.Videos, ix.Category(ix.Find(@"X:\Dom\v")));
    }

    [Fact]
    public void Top_files_over_a_large_tree_are_the_largest()
    {
        var ix = new SpaceIndexScanner(SyntheticTree.Walker(), new FakeSafety(), new FakeTags(), new FakeDrives())
            .Scan(SyntheticTree.Root, NoDrive, null, CancellationToken.None);
        var top = SpaceQueries.TopFiles(ix, SpaceIndex.RootNode, 100);
        Assert.Equal(100, top.Count);
        long smallestTop = ix.Size(top[^1]);
        long biggerOutside = 0;
        for (int n = 0; n < ix.Count; n++)
            if (!ix.IsDirectory(n) && !top.Contains(n)) biggerOutside = Math.Max(biggerOutside, ix.Size(n));
        Assert.True(biggerOutside <= smallestTop);
        Assert.True(top.Zip(top.Skip(1)).All(p => ix.Size(p.First) >= ix.Size(p.Second)));
    }

    [Fact]
    public void Odd_names_and_long_paths_are_kept_exactly()
    {
        var t = new FakeTree(@"X:\Odd");
        var deep = string.Join('\\', Enumerable.Repeat("a very long folder name for testing", 9));
        var weird = t.File(Path.Combine(deep, "Résumé 日本語 file. .txt"), 42);
        t.File("CON.txt", 1);
        var ix = Scan(t);
        int n = ix.Find(weird);
        Assert.True(n > 0);
        Assert.Equal(weird, ix.FullPath(n));
        Assert.True(ix.Has(n, SpaceNodeFlags.LongPath));
        Assert.True(ix.Summary.LongPaths > 0);
        Assert.False(ix.Has(ix.Find(@"X:\Odd\CON.txt"), SpaceNodeFlags.LongPath));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) { lock (this) _a(value); }
    }
}
