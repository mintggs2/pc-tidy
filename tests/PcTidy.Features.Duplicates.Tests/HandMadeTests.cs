using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Junk;
using PcTidy.Features.Duplicates.Planning;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>Focused cases on small hand-made sandboxes (Minimal profile: one drive, no OneDrive), real SafetyGuard.</summary>
public sealed class HandMadeTests
{
    private static DuplicatesEngine Engine() => new(new HashCache());

    [Fact]
    public async Task Hard_links_are_not_duplicates_and_free_nothing()
    {
        using var pc = new HandMadePc("hardlink");
        var dl = pc.K.Downloads;
        var data = HandMadePc.Bytes(1, 200_000);
        var a = pc.Put(Path.Combine(dl, "video.bin"), data);
        var link = Path.Combine(dl, "video link.bin");
        HandMadePc.HardLink(link, a);
        // Two names of one file and nothing else: not a duplicate at all.
        var only = await Engine().ScanAsync(pc.Context());
        Assert.Empty(only.ExactGroups);
        Assert.True(only.Stats.HardLinks >= 1);

        // Add a real copy: one group of three paths, but only one real extra copy.
        var copy = pc.Put(Path.Combine(dl, "Old stuff", "video.bin"), data);
        var r = await Engine().ScanAsync(pc.Context());
        var g = Assert.Single(r.ExactGroups);
        Assert.Equal(3, g.Copies.Count);
        var hard = Assert.Single(g.Copies, c => c.IsHardLink);
        Assert.Equal(0, hard.ReclaimableBytes);
        Assert.False(hard.CanBeRemoved);
        Assert.Equal(data.Length, g.ReclaimableBytes);
        var items = DuplicatesScanner.ToScanResult(r, TimeSpan.Zero).AllItems.ToList();
        var hardItem = items.Single(i => i.CurrentState == "Hard link");
        Assert.Equal(0, hardItem.SizeBytes);
        Assert.NotNull(hardItem.DisabledReason);
        Assert.Contains("hard link", hardItem.DisabledReason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(hardItem.DefaultSelected);
    }

    [Fact]
    public void File_identity_reads_ids_without_opening_content()
    {
        using var pc = new HandMadePc("fileid");
        var a = pc.Put(Path.Combine(pc.K.Downloads, "a.txt"), HandMadePc.Bytes(2, 100));
        var b = Path.Combine(pc.K.Downloads, "b.txt");
        HandMadePc.HardLink(b, a);
        var c = pc.Put(Path.Combine(pc.K.Downloads, "c.txt"), HandMadePc.Bytes(2, 100));
        var ia = FileIdentity.TryGet(a)!.Value;
        var ib = FileIdentity.TryGet(b)!.Value;
        var ic = FileIdentity.TryGet(c)!.Value;
        Assert.Equal((ia.Volume, ia.IdLow, ia.IdHigh), (ib.Volume, ib.IdLow, ib.IdHigh));
        Assert.NotEqual((ia.IdLow, ia.IdHigh), (ic.IdLow, ic.IdHigh));
        Assert.Equal(2u, ia.LinkCount);
        Assert.Equal(1u, ic.LinkCount);
        // Works while another app holds the file exclusively (attributes only).
        using (new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.NotNull(FileIdentity.TryGet(c));
        Assert.Null(FileIdentity.TryGet(Path.Combine(pc.K.Downloads, "missing.txt")));
    }

    [Fact]
    public async Task Duplicate_folders_are_found_once_and_cover_their_files()
    {
        using var pc = new HandMadePc("folders");
        var dl = pc.K.Downloads;
        void Album(string root)
        {
            pc.Put(Path.Combine(root, "1.jpg"), HandMadePc.Bytes(10, 5_000));
            pc.Put(Path.Combine(root, "notes.txt"), HandMadePc.Bytes(11, 300));
            pc.Put(Path.Combine(root, "raw", "1.cr2"), HandMadePc.Bytes(12, 90_000));
        }
        Album(Path.Combine(dl, "Album"));
        Album(Path.Combine(dl, "Album - Copy"));
        // Same names but one byte different: not a duplicate folder.
        Album(Path.Combine(dl, "Album edited"));
        var edited = HandMadePc.Bytes(11, 300); edited[0] ^= 1;
        pc.Put(Path.Combine(dl, "Album edited", "notes.txt"), edited);

        var r = await Engine().ScanAsync(pc.Context());
        var folders = Assert.Single(r.FolderGroups, g => g.Copies.Any(c => c.Path.EndsWith(@"\Album", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, folders.Copies.Count);
        Assert.True(DefaultPcFixture.Same(Path.Combine(dl, "Album"), folders.KeptPath), folders.KeptPath);
        Assert.Equal(3, folders.FileCount);
        Assert.Empty(folders.AutoTicked);
        // "raw" matches in all three albums: Album's group covers its copy's raw, so one of those stands in, plus the edited album's.
        var raw = Assert.Single(r.FolderGroups, g => g.Copies.All(c => c.Path.EndsWith(@"\raw", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, raw.Copies.Count);
        Assert.Contains(raw.Copies, c => c.Path.Contains(@"\Album edited\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, r.FolderGroups.Count);
        // 1.jpg and 1.cr2 also exist in "Album edited", so their file groups stay; notes.txt exists only in the two copies.
        Assert.DoesNotContain(r.ExactGroups, g => g.Copies.All(c => c.Path.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(r.ExactGroups, g => g.Copies.Count == 3 && g.Copies.All(c => c.Path.EndsWith("1.jpg", StringComparison.OrdinalIgnoreCase)));
        var item = DuplicatesScanner.ToScanResult(r, TimeSpan.Zero).Groups.Single(x => x.Id == "duplicates.folders").Items
            .Single(i => i.Detail!.EndsWith("Album - Copy", StringComparison.OrdinalIgnoreCase));
        var action = item.Actions.Single();
        Assert.True(action.IsDirectory);
        Assert.Equal(DuplicatePlanner.VerifyModeFolder, action.Args![DuplicatePlanner.ArgVerifyMode]);
        Assert.Equal(95_300, action.ExpectedSize);
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task Compare_to_master_lists_only_files_already_in_the_master()
    {
        using var pc = new HandMadePc("master");
        var master = Path.Combine(pc.K.Downloads, "Master photos");
        var other = Path.Combine(pc.K.Downloads, "From phone");
        pc.Put(Path.Combine(master, "2024", "a.jpg"), HandMadePc.Bytes(20, 70_000));
        pc.Put(Path.Combine(master, "b.jpg"), HandMadePc.Bytes(21, 4_000));
        var dupA = pc.Put(Path.Combine(other, "IMG_0001.jpg"), HandMadePc.Bytes(20, 70_000));
        pc.Put(Path.Combine(other, "IMG_0002.jpg"), HandMadePc.Bytes(22, 4_000));        // same size as b.jpg, different
        pc.Put(Path.Combine(other, "new.jpg"), HandMadePc.Bytes(23, 1_234));

        var cmp = await Engine().CompareToMasterAsync(pc.Context(), master, other);
        var m = Assert.Single(cmp.Matches);
        Assert.True(DefaultPcFixture.Same(dupA, m.Copy.Path));
        Assert.True(DefaultPcFixture.Same(Path.Combine(master, "2024", "a.jpg"), m.MasterPath));
        Assert.Equal(2, cmp.OnlyInOther);
        Assert.True(m.Copy.CanBeRemoved);
        var plan = DuplicatePlanner.ForMasterMatch(m, s => s);
        Assert.Equal(FileHash.Full(dupA), plan.Args![DuplicatePlanner.ArgVerifyHash]);
        Assert.True(pc.Host.Safety.Check(plan).Allowed);

        var nested = await Engine().CompareToMasterAsync(pc.Context(), master, Path.Combine(master, "2024"));
        Assert.Empty(nested.Matches);
        Assert.Contains("separate", nested.Notes.Single());
    }

    [Fact]
    public async Task Reference_folders_are_compared_but_never_offered()
    {
        using var pc = new HandMadePc("reference");
        var archive = Path.Combine(pc.Root, "C", "Archive");
        var bytes = HandMadePc.Bytes(30, 9_000);
        pc.Put(Path.Combine(archive, "song.mp3"), bytes);
        var dl = pc.Put(Path.Combine(pc.K.Downloads, "song.mp3"), bytes);
        var settings = new DuplicatesSettings { ReferenceFolders = { archive } };
        settings.Store(pc.Host.Settings);
        var r = await Engine().ScanAsync(pc.Context(roots: new[] { archive, pc.K.Downloads }));
        var g = Assert.Single(r.ExactGroups);
        Assert.True(DefaultPcFixture.Same(Path.Combine(archive, "song.mp3"), g.KeptPath));
        Assert.Contains("reference folder", g.KeepReason);
        Assert.False(g.Kept.CanBeRemoved);
        Assert.True(g.Copies.Single(c => DefaultPcFixture.Same(c.Path, dl)).CanBeRemoved);

        // Both copies in reference folders: nothing to do, the group isn't shown.
        settings.ReferenceFolders.Add(pc.K.Downloads);
        settings.Store(pc.Host.Settings);
        var none = await Engine().ScanAsync(pc.Context(roots: new[] { archive, pc.K.Downloads }));
        Assert.Empty(none.ExactGroups);
    }

    [Fact]
    public async Task Sync_version_folders_and_odd_names_are_handled()
    {
        using var pc = new HandMadePc("oddnames");
        var dl = pc.K.Downloads;
        var bytes = HandMadePc.Bytes(40, 3_000);
        pc.Put(Path.Combine(dl, "Synced", ".stversions", "doc~20260101.txt"), bytes);
        pc.Put(Path.Combine(dl, "Synced", "doc.txt"), bytes);
        // Names Windows' path rules would change: the scan must still read the exact file.
        var dotted = Path.Combine(dl, "notes.");
        File.WriteAllBytes(@"\\?\" + dotted, bytes);
        var r = await Engine().ScanAsync(pc.Context());
        var g = Assert.Single(r.ExactGroups);
        Assert.Equal(2, g.Copies.Count);
        Assert.DoesNotContain(g.Copies, c => c.Path.Contains(".stversions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(g.Copies, c => c.Path.EndsWith("notes.", StringComparison.Ordinal));
        File.Delete(@"\\?\" + dotted);
    }

    [Fact]
    public async Task Cancelling_stops_the_scan()
    {
        using var pc = new HandMadePc("cancel");
        for (int i = 0; i < 50; i++) pc.Put(Path.Combine(pc.K.Downloads, $"f{i}.bin"), HandMadePc.Bytes(i % 5, 10_000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().ScanAsync(pc.Context(), null, null, cts.Token));
    }

    [Fact]
    public void Hash_cache_round_trips_and_ignores_changed_files()
    {
        var cache = new HashCache();
        var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        cache.PutPartial(@"C:\a.bin", 10, t, 0xABCDEF);
        cache.PutFull(@"C:\a.bin", 10, t, 0x123456);
        cache.PutImage(@"C:\p.jpg", 99, t, new ImageHashes(1, 2, 640, 480));
        cache.PutNotAnImage(@"C:\broken.jpg", 5, t);
        using var ms = new MemoryStream();
        cache.Save(ms);
        Assert.False(cache.IsDirty);
        ms.Position = 0;
        var back = HashCache.Load(ms);
        Assert.Equal(3, back.Count);
        Assert.True(back.TryGetFull(@"C:\A.BIN", 10, t, out var full));
        Assert.Equal(0x123456UL, full);
        Assert.False(back.TryGetFull(@"C:\a.bin", 11, t, out _));            // size changed
        Assert.False(back.TryGetPartial(@"C:\a.bin", 10, t.AddSeconds(1), out _)); // modified since
        Assert.True(back.TryGetImage(@"C:\p.jpg", 99, t, out var img, out var notImage));
        Assert.Equal((640, 480), (img.Width, img.Height));
        Assert.False(notImage);
        Assert.True(back.TryGetImage(@"C:\broken.jpg", 5, t, out _, out notImage));
        Assert.True(notImage);
        // Garbage gives an empty cache, never an error.
        var junk = HashCache.Load(new MemoryStream(new byte[] { 1, 2, 3 }));
        Assert.Equal(0, junk.Count);
        // PC Tidy's own data for this feature lives in <DataRoot>\duplicates\.
        Assert.Equal(Path.Combine(@"C:\Data", "duplicates", HashCache.FileName), HashCache.PathIn(@"C:\Data"));
    }

    [Fact]
    public void Module_shares_one_cache_and_only_saves_when_changed()
    {
        var m = new DuplicatesModule();
        Assert.Equal("duplicates", m.Id);
        Assert.Equal("Duplicates", m.Title);
        Assert.Equal(NavGroup.CleanUp, m.Group);
        Assert.Equal("DocumentCopy24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        var scanner = Assert.Single(m.Scanners);
        Assert.True(scanner.IncludedInOptimizeAll);
        Assert.Equal("duplicates", scanner.FeatureId);
        var cache = m.CacheFor(null);
        Assert.Same(cache, m.CacheFor(@"C:\elsewhere"));
        cache.PutFull(@"C:\x", 1, DateTime.UtcNow, 1);
        Assert.False(m.SaveCache()); // no data folder known: nothing is written anywhere
    }

    [Fact]
    public void Shortcut_reader_follows_the_link_format_read_only()
    {
        using var pc = new HandMadePc("lnk");
        // A shortcut Windows itself made would be ideal; the testbed generator writes the documented binary format.
        var target = pc.Put(Path.Combine(pc.K.ProgramFiles, "App", "app.exe"), HandMadePc.Bytes(50, 100));
        var lnkBytes = BuildLnk(target);
        var lnk = pc.Put(Path.Combine(pc.K.Downloads, "App.lnk"), lnkBytes);
        var t = ShortcutReader.Read(lnk);
        Assert.Equal(ShortcutTargetKind.Local, t.Kind);
        Assert.Equal(target, t.Path, ignoreCase: true);
        var unc = ShortcutReader.Read(pc.Put(Path.Combine(pc.K.Downloads, "Share.lnk"), BuildLnk(@"\\server\share\file.txt")));
        Assert.Equal(ShortcutTargetKind.Network, unc.Kind);
        Assert.Equal(ShortcutTargetKind.Unknown, ShortcutReader.Read(pc.Put(Path.Combine(pc.K.Downloads, "bad.lnk"), HandMadePc.Bytes(3, 200))).Kind);
    }

    /// <summary>Minimal [MS-SHLLINK] file: header + LinkInfo with a Unicode local base path.</summary>
    private static byte[] BuildLnk(string targetPath)
    {
        var ansi = System.Text.Encoding.Latin1.GetBytes(targetPath + "\0");
        var unicode = System.Text.Encoding.Unicode.GetBytes(targetPath + "\0");
        const int header = 0x24, volumeSize = 0x11;
        int baseOffset = header + volumeSize, suffixOffset = baseOffset + ansi.Length, baseU = suffixOffset + 1, suffixU = baseU + unicode.Length;
        int infoSize = suffixU + 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x4C); w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(0x02 | 0x80); w.Write(0x20); w.Write(0L); w.Write(0L); w.Write(0L); w.Write(0); w.Write(0); w.Write(1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);
        w.Write(infoSize); w.Write(header); w.Write(1); w.Write(header); w.Write(baseOffset); w.Write(0); w.Write(suffixOffset); w.Write(baseU); w.Write(suffixU);
        w.Write(volumeSize); w.Write(3); w.Write(0x1234); w.Write(0x10); w.Write((byte)0);
        w.Write(ansi); w.Write((byte)0); w.Write(unicode); w.Write((ushort)0);
        w.Write(0);
        return ms.ToArray();
    }
}
