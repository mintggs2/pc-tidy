using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Images;
using PcTidy.Features.Duplicates.Junk;
using PcTidy.Features.Duplicates.Scanning;
using PcTidy.Features.Duplicates.UI;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>v2 review fixes DUP-1..DUP-5, and DUP-V3-1 (a folder reached through a junction is one folder).</summary>
public sealed class ReviewFixTests
{
    // ------------------------------------------------------------------ DUP-1: the hash cache survives the session

    [Fact]
    public async Task A_scan_saves_the_hash_cache_and_the_next_session_reuses_it()
    {
        using var pc = new HandMadePc("dup1-cache");
        var a = pc.Put(Path.Combine(pc.K.Documents, "report.pdf"), HandMadePc.Bytes(1, 20_000));
        pc.Put(Path.Combine(pc.K.Downloads, "report.pdf"), File.ReadAllBytes(a));
        var ctx = pc.Context();

        var first = new DuplicatesModule();
        await first.Scanners[0].ScanAsync(ctx, null, CancellationToken.None);
        var file = HashCache.PathIn(ctx.Settings.DataRoot);
        Assert.True(File.Exists(file), "the cache is written to <DataRoot>\\duplicates\\");
        Assert.StartsWith(Path.Combine(ctx.Settings.DataRoot, DuplicatesModule.FeatureId), file, StringComparison.OrdinalIgnoreCase);
        Assert.False(first.SaveCache()); // nothing new since the save

        var second = new DuplicatesModule(); // a new app session
        var cache = second.CacheFor(ctx.Settings.DataRoot);
        Assert.True(cache.Count > 0);
        var report = await new DuplicatesEngine(cache).ScanAsync(ctx);
        Assert.True(report.Stats.CacheHits > 0);
        Assert.Equal(0, report.Stats.FullHashed);
        Assert.Single(report.ExactGroups);
    }

    // ------------------------------------------------------------------ DUP-2: "inside a duplicate folder" by parent chain

    [Fact]
    public void Folder_membership_is_found_through_the_parent_chain()
    {
        var set = new DuplicatesEngine.FolderSet(new[] { @"C:\Photos\2019", @"D:\Backup\Photos\2019\" });
        Assert.True(set.ContainsAncestorOf(@"C:\Photos\2019\a.jpg"));
        Assert.True(set.ContainsAncestorOf(@"c:\photos\2019\Trip\b.jpg"));
        Assert.True(set.ContainsAncestorOf(@"D:\Backup\Photos\2019"));
        Assert.False(set.ContainsAncestorOf(@"C:\Photos\2019-old\a.jpg"));
        Assert.False(set.ContainsAncestorOf(@"C:\Photos\a.jpg"));
        Assert.False(set.ContainsAncestorOf(@"E:\x.jpg"));
    }

    // ------------------------------------------------------------------ DUP-3: code-page shortcut paths

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [Fact]
    public void A_working_shortcut_stored_in_the_ansi_code_page_is_not_called_broken()
    {
        using var pc = new HandMadePc("dup3-ansi");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var acp = Encoding.GetEncoding((int)GetACP());
        // Letters outside ASCII that this PC's code page can hold (é is in nearly every Latin code page; else skip).
        var folder = Path.Combine(pc.K.Documents, "Caf\u00e9 r\u00e9sum\u00e9");
        if (acp.GetString(acp.GetBytes(folder)) != folder) return;
        var target = pc.Put(Path.Combine(folder, "notes.txt"), HandMadePc.Bytes(2, 100));

        var t = ShortcutReader.Read(pc.Put(Path.Combine(pc.K.Desktop, "Notes.lnk"), AnsiOnlyLnk(acp.GetBytes(target))));
        Assert.Equal(ShortcutTargetKind.Local, t.Kind);
        Assert.Equal(target, t.Path);
        Assert.Equal(JunkFinder.TargetState.Exists, JunkFinder.Check(t));
    }

    [Fact]
    public void A_code_page_path_from_another_language_is_never_called_broken()
    {
        using var pc = new HandMadePc("dup3-foreign");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cyrillic = Encoding.GetEncoding(1251);
        var target = pc.Put(Path.Combine(pc.K.Documents, "\u041e\u0442\u0447\u0451\u0442.docx"), HandMadePc.Bytes(3, 100)); // Отчёт.docx
        if (GetACP() == 1251) return; // on a Cyrillic PC this decodes correctly (covered by the test above)

        var t = ShortcutReader.Read(pc.Put(Path.Combine(pc.K.Desktop, "Report.lnk"), AnsiOnlyLnk(cyrillic.GetBytes(target))));
        Assert.True(t.MaybeMisread);
        Assert.Equal(JunkFinder.TargetState.Unknown, JunkFinder.Check(t)); // can't be sure: not offered
    }

    [Fact]
    public void A_plain_ascii_shortcut_to_a_missing_file_is_still_broken()
    {
        using var pc = new HandMadePc("dup3-ascii");
        var gone = Path.Combine(pc.K.Documents, "Gone", "old.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(gone)!);
        var t = ShortcutReader.Read(pc.Put(Path.Combine(pc.K.Desktop, "Old.lnk"), AnsiOnlyLnk(Encoding.ASCII.GetBytes(gone))));
        Assert.False(t.MaybeMisread);
        Assert.Equal(JunkFinder.TargetState.Missing, JunkFinder.Check(t));
    }

    /// <summary>[MS-SHLLINK] with the short (0x1C) LinkInfo header: only the ANSI local base path, no Unicode fields.</summary>
    private static byte[] AnsiOnlyLnk(byte[] path)
    {
        const int header = 0x1C, volumeSize = 0x11;
        int baseOffset = header + volumeSize, suffixOffset = baseOffset + path.Length + 1, infoSize = suffixOffset + 1;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x4C); w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(0x02); w.Write(0x20); w.Write(0L); w.Write(0L); w.Write(0L); w.Write(0); w.Write(0); w.Write(1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);
        w.Write(infoSize); w.Write(header); w.Write(1); w.Write(header); w.Write(baseOffset); w.Write(0); w.Write(suffixOffset);
        w.Write(volumeSize); w.Write(3); w.Write(0x1234); w.Write(0x10); w.Write((byte)0);
        w.Write(path); w.Write((byte)0); w.Write((byte)0);
        w.Write(0);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ DUP-4: the page never sticks on "Scanning"

    private sealed class BrokenServices(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(ISafetyGuard) ? throw new NotSupportedException("simulated") : inner.GetService(t);
    }

    [Fact]
    public async Task An_unexpected_error_ends_the_scan_instead_of_leaving_it_running()
    {
        using var pc = new HandMadePc("dup4-error");
        var vm = new DuplicatesViewModel(new BrokenServices(pc.Host), new DuplicatesModule());
        await vm.ScanAsync(); // must not throw
        Assert.Equal(PageState.Ready, vm.State);
    }

    // ------------------------------------------------------------------ DUP-5: indexed pairing, star groups

    [Fact]
    public void The_index_finds_exactly_the_pairs_all_pairs_comparison_finds()
    {
        var rnd = new Random(7);
        var hashes = new List<ImageHashes>();
        for (int i = 0; i < 400; i++)
        {
            // Clusters of near hashes plus noise, so there are real matches to find.
            ulong baseHash = (ulong)rnd.NextInt64() ^ ((ulong)rnd.NextInt64() << 32);
            hashes.Add(new ImageHashes(baseHash, baseHash, 100, 100));
            for (int k = 0; k < 3; k++)
            {
                ulong flip = 0;
                for (int b = 0; b < rnd.Next(0, 16); b++) flip |= 1UL << rnd.Next(64);
                hashes.Add(new ImageHashes(baseHash ^ flip, baseHash ^ flip, 100, 100));
            }
        }
        foreach (var t in new[] { SimilarityThresholds.Quick, SimilarityThresholds.Deep })
        {
            var indexed = LookAlikeIndex.Neighbours(hashes, t, 4, null, CancellationToken.None);
            for (int i = 0; i < hashes.Count; i++)
            {
                var brute = Enumerable.Range(0, hashes.Count).Where(j => j != i
                    && ImageFingerprint.Distance(hashes[i].PHash, hashes[j].PHash) <= t.MaxPHashDistance
                    && ImageFingerprint.Distance(hashes[i].DHash, hashes[j].DHash) <= t.MaxDHashDistance).ToHashSet();
                Assert.True(brute.SetEquals(indexed[i]), $"picture {i}");
            }
        }
        Assert.Equal(697, LookAlikeIndex.MasksUpTo(3).Count);
    }

    [Fact]
    public void Look_alike_groups_are_stars_around_the_best_picture_never_chains()
    {
        using var pc = new HandMadePc("dup5-chain");
        // A ≈ B ≈ C ≈ D, each 8 bits from the next, so A and D are 24 bits apart (far beyond Deep's 12).
        ulong a = 0;
        ulong Step(ulong h, int from) { for (int b = from; b < from + 8; b++) h ^= 1UL << b; return h; }
        var hashes = new[] { a, Step(a, 0), Step(Step(a, 0), 8), Step(Step(Step(a, 0), 8), 16) };
        var cache = new HashCache();
        var files = new List<FileRec>();
        for (int i = 0; i < hashes.Length; i++)
        {
            var path = pc.Put(Path.Combine(pc.K.Pictures, $"p{i}.jpg"), HandMadePc.Bytes(10 + i, 1000 + i));
            var info = new FileInfo(path);
            // B has the most pixels, so it's the first centre.
            cache.PutImage(path, info.Length, info.LastWriteTimeUtc, new ImageHashes(hashes[i], hashes[i], i == 1 ? 4000 : 1000, 1000));
            files.Add(new FileRec(path, info.Name, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes, 0));
        }
        var groups = new SimilarImageFinder(cache).Find(files, SimilarityThresholds.Deep, f => "path:" + f.Path, null, CancellationToken.None);

        foreach (var g in groups)
        {
            var centre = g.Members[0].Hashes.PHash;
            Assert.All(g.Members, m => Assert.True(ImageFingerprint.Distance(m.Hashes.PHash, centre) <= SimilarityThresholds.Deep.MaxPHashDistance));
        }
        Assert.DoesNotContain(groups, g => g.Members.Any(m => m.File.Path.EndsWith("p0.jpg")) && g.Members.Any(m => m.File.Path.EndsWith("p3.jpg")));
        Assert.Equal(new[] { "p0.jpg", "p1.jpg", "p2.jpg" }, groups[0].Members.Select(m => Path.GetFileName(m.File.Path)).OrderBy(n => n));
    }

    // ------------------------------------------------------------------ DUP-V3-1: a junction to another root is the same folder

    [Fact]
    public async Task A_root_reached_through_a_junction_is_scanned_once_and_never_its_own_duplicate()
    {
        using var pc = new HandMadePc("dupv3-junction");
        var photos = Path.Combine(pc.K.Pictures, "Trip");
        pc.Put(Path.Combine(photos, "a.jpg"), HandMadePc.Bytes(20, 5000));
        pc.Put(Path.Combine(photos, "b.jpg"), HandMadePc.Bytes(21, 6000));
        var link = Path.Combine(pc.K.Documents, "Trip link");
        Assert.True(TryJunction(link, photos), "mklink /J in the testbed");

        Assert.Equal(new[] { PathUtil.Normalize(photos) }, ScanScope.Dedupe(new[] { link, photos }));
        Assert.Equal(new[] { PathUtil.Normalize(photos) }, ScanScope.Dedupe(new[] { photos, link }));

        var report = await new DuplicatesEngine(new HashCache()).ScanAsync(pc.Context(ScanDepth.Quick, new[] { link, photos }));
        Assert.Empty(report.ExactGroups);
        Assert.Empty(report.FolderGroups);
        Assert.Single(report.Roots);
    }

    private static bool TryJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit(10_000);
        return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
    }
}
