using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.TestbedGen;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>
/// One Quick scan of a generated Default-profile PC (two drives, OneDrive-redirected Desktop/Documents/Pictures,
/// Steam, Epic, junctions, a .git project, online-only placeholders) through the REAL SafetyGuard.
/// </summary>
[Collection("default-pc")]
public sealed class DetectionTests
{
    private readonly DefaultPcFixture _f;

    public DetectionTests(DefaultPcFixture f) => _f = f;

    private DuplicatesReport R => _f.Report;
    private IEnumerable<string> AllPaths => R.ExactGroups.Concat(R.FolderGroups).SelectMany(g => g.Copies.Select(c => c.Path))
        .Concat(R.SimilarImages.SelectMany(g => g.Images.Select(i => i.Path)))
        .Concat(R.EmptyFolders.Concat(R.ZeroByteFiles).Concat(R.BrokenShortcuts).Select(j => j.Path));

    [Fact]
    public void Finds_every_copy_of_the_report_and_keeps_the_library_copy_with_the_clean_name()
    {
        var copies = _f.Pc.Tagged("dup:report");
        Assert.Equal(3, copies.Count);
        var g = _f.GroupWith(copies[0]);
        Assert.Equal(copies.Count, g.Copies.Count);
        Assert.All(copies, p => Assert.Contains(g.Copies, c => DefaultPcFixture.Same(c.Path, p)));
        Assert.True(DefaultPcFixture.Same(Path.Combine(_f.Pc.K.Documents, "Work", "Report final.docx"), g.KeptPath), g.KeptPath);
        // Downloads copy of a file kept in a library folder: the one clear case, ticked.
        var downloads = Path.Combine(_f.Pc.K.Downloads, "Report final.docx");
        Assert.Contains(g.AutoTicked, p => DefaultPcFixture.Same(p, downloads));
        Assert.Single(g.AutoTicked);
        Assert.Contains("original name", g.KeepReason);
    }

    [Fact]
    public void Holiday_photo_across_folders_and_drives_ticks_only_the_downloads_copy()
    {
        var copies = _f.Pc.Tagged("dup:holiday");
        Assert.Equal(4, copies.Count); // Pictures ×2, Downloads, D:\Photos
        var g = _f.GroupWith(copies[0]);
        Assert.Equal(4, g.Copies.Count);
        Assert.True(DefaultPcFixture.Same(Path.Combine(_f.Pc.K.Pictures, "Holiday 2025", "IMG_20250614_101500.jpg"), g.KeptPath), g.KeptPath);
        Assert.Equal(new[] { Path.Combine(_f.Pc.K.Downloads, "IMG_20250614_101500.jpg") }.Select(PathUtil.Normalize), g.AutoTicked.Select(PathUtil.Normalize));
        // The D: copy is "elsewhere", not Downloads: shown, unticked, still removable by hand.
        var onD = g.Copies.Single(c => c.Path.Contains(@"\D\Photos\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LocationClass.Other, onD.Location);
        Assert.True(onD.CanBeRemoved);
        Assert.DoesNotContain(onD.Path, g.AutoTicked);
    }

    [Fact]
    public void Same_size_decoys_are_never_matched()
    {
        foreach (var decoy in _f.Pc.Tagged("decoy"))
            Assert.DoesNotContain(R.ExactGroups.SelectMany(g => g.Copies), c => DefaultPcFixture.Same(c.Path, decoy));
        // The prefix decoys share their first 64 KB: they reach the full hash and are told apart there.
        Assert.True(R.Stats.FullHashed + R.Stats.CacheHits >= 2);
    }

    [Fact]
    public void Online_only_files_junctions_git_and_protected_areas_are_never_offered()
    {
        var placeholders = _f.Pc.Info.Placeholders.Select(_f.Pc.Abs).ToList();
        Assert.NotEmpty(placeholders);
        Assert.True(R.Stats.PlaceholdersSkipped >= placeholders.Count);
        // Never opened: no hash of any kind was ever taken of an online-only file.
        foreach (var ph in placeholders)
        {
            var fi = new FileInfo(ph);
            Assert.False(_f.Cache.TryGetPartial(ph, fi.Length, fi.LastWriteTimeUtc, out _), ph);
            Assert.False(_f.Cache.TryGetImage(ph, fi.Length, fi.LastWriteTimeUtc, out _, out _), ph);
        }
        var all = AllPaths.ToList();
        Assert.DoesNotContain(all, p => placeholders.Any(ph => DefaultPcFixture.Same(ph, p)));
        Assert.DoesNotContain(all, p => p.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) || p.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, p => p.Contains(@"\node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, p => p.Contains(DownloadsAreaWebProject(), StringComparison.OrdinalIgnoreCase));
        foreach (var j in _f.Pc.Info.Junctions)
            Assert.DoesNotContain(all, p => PathUtil.IsUnder(p, _f.Pc.Abs(j.RelativeLink)));
        var k = _f.Pc.K;
        foreach (var never in new[] { k.WindowsDir, k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.LocalAppData, k.RoamingAppData, k.DataRoot }
                     .Concat(k.SteamLibraries).Concat(k.GameInstallDirs))
            Assert.DoesNotContain(all, p => PathUtil.IsUnder(p, never));
        Assert.True(R.Stats.LinksSkipped >= _f.Pc.Info.Junctions.Count);
        Assert.True(R.Stats.ProjectFoldersSkipped >= 1);
    }

    private static string DownloadsAreaWebProject() => @"\my-web-app\";

    [Fact]
    public void Empty_folders_are_found_outermost_first()
    {
        var dl = _f.Pc.K.Downloads;
        var paths = R.EmptyFolders.Select(j => j.Path).ToList();
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "New folder")));
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "New folder (2)")));
        var nested = R.EmptyFolders.Single(j => DefaultPcFixture.Same(j.Path, Path.Combine(dl, "Nested empty")));
        Assert.Equal(2, nested.NestedEmptyFolders);
        Assert.DoesNotContain(paths, p => p.Contains(@"\Nested empty\", StringComparison.OrdinalIgnoreCase));
        // Folders the walk never entered (and the user folders themselves) are not "empty".
        Assert.DoesNotContain(paths, p => DefaultPcFixture.Same(p, dl));
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(_f.Pc.Root, "D", "Temp stuff")));
    }

    [Fact]
    public void Zero_byte_files_are_found_and_marker_files_are_left_alone()
    {
        var dl = _f.Pc.K.Downloads;
        var paths = R.ZeroByteFiles.Select(j => j.Path).ToList();
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "New Text Document.txt")));
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "empty.log")));
        Assert.DoesNotContain(paths, p => Junk.JunkFinder.IsMarker(Path.GetFileName(p)));
    }

    [Fact]
    public void Broken_shortcuts_are_found_and_working_ones_are_not()
    {
        var dl = _f.Pc.K.Downloads;
        var paths = R.BrokenShortcuts.Select(j => j.Path).ToList();
        Assert.Contains(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "Old Tool.lnk")));
        Assert.DoesNotContain(paths, p => DefaultPcFixture.Same(p, Path.Combine(dl, "FakeApp.lnk")));
        Assert.DoesNotContain(paths, p => p.EndsWith("Steam.lnk", StringComparison.OrdinalIgnoreCase));
        // On the OneDrive-backed Desktop the guard asks first: listed, but greyed out with its reason.
        var desktop = R.BrokenShortcuts.Single(j => DefaultPcFixture.Same(j.Path, Path.Combine(_f.Pc.K.Desktop, "Broken Game.lnk")));
        Assert.NotNull(desktop.RemovalBlockedReason);
        var old = R.BrokenShortcuts.Single(j => DefaultPcFixture.Same(j.Path, Path.Combine(dl, "Old Tool.lnk")));
        Assert.Null(old.RemovalBlockedReason);
        Assert.Contains("GoneTool.exe", old.Target);
    }

    [Fact]
    public void Look_alike_pictures_are_grouped_and_never_ticked()
    {
        var beach = _f.Pc.Tagged("near:beach");
        Assert.Equal(4, beach.Count);
        var g = R.SimilarImages.Single(s => s.Images.Any(i => DefaultPcFixture.Same(i.Path, beach[0])));
        Assert.All(beach, b => Assert.Contains(g.Images, i => DefaultPcFixture.Same(i.Path, b)));
        // Other photos (different scenes) stay out; the exact-duplicate holiday photo isn't "similar".
        Assert.Equal(4, g.Images.Count);
        Assert.DoesNotContain(R.SimilarImages.SelectMany(s => s.Images), i => i.Path.Contains("IMG_20250614_101500", StringComparison.OrdinalIgnoreCase));
        var result = DuplicatesScanner.ToScanResult(R, TimeSpan.Zero);
        var similar = result.Groups.Single(x => x.Id == "duplicates.similar");
        Assert.All(similar.Items, i => { Assert.False(i.DefaultSelected); Assert.Equal(Badge.Check, i.Badge); Assert.NotNull(i.PreviewPath); });
    }

    [Fact]
    public void Scan_items_follow_the_badge_rules_and_carry_verification_data()
    {
        var result = DuplicatesScanner.ToScanResult(R, TimeSpan.Zero);
        var items = result.AllItems.ToList();
        Assert.NotEmpty(items);
        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
        foreach (var i in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Reason));
            if (i.Badge != Badge.Safe) Assert.False(i.DefaultSelected, i.Title);
            if (i.DisabledReason is not null) Assert.False(i.DefaultSelected, i.Title);
            var a = Assert.Single(i.Actions);
            Assert.Equal(ActionKind.QuarantinePath, a.Kind);
            Assert.True(a.Reversible);
            Assert.NotNull(a.ExpectedLastWriteUtc);
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
        }
        // Ticked = only Downloads/temp/backup copies of files kept in a library folder.
        var ticked = items.Where(i => i.DefaultSelected).ToList();
        Assert.NotEmpty(ticked);
        foreach (var t in ticked)
        {
            Assert.True(PathUtil.IsUnder(t.Detail!, _f.Pc.K.Downloads), t.Detail);
            var a = t.Actions[0];
            var keep = a.Args![DuplicatePlanner.ArgKeepPath];
            Assert.True(new[] { _f.Pc.K.Documents, _f.Pc.K.Pictures, _f.Pc.K.Desktop, _f.Pc.K.Music, _f.Pc.K.Videos }.Any(l => PathUtil.IsUnder(keep, l)), keep);
            // The engine re-hashes right before moving: the stored hash is today's hash of both copies.
            Assert.Equal(FileHash.Full(a.SourcePath!), a.Args[DuplicatePlanner.ArgVerifyHash]);
            Assert.Equal(FileHash.Full(keep), a.Args[DuplicatePlanner.ArgVerifyHash]);
            Assert.Equal(a.ExpectedSize, new FileInfo(a.SourcePath!).Length);
        }
        // Every ticked action passes the real safety guard as built.
        Assert.All(ticked, t => Assert.True(_f.Pc.Host.Safety.Check(t.Actions[0]).Allowed, t.Detail));
    }

    [Fact]
    public void Scanning_changed_nothing()
    {
        var diff = _f.Pc.DiffFrom(_f.Before);
        Assert.True(diff.Identical, diff.Format());
        var report = new DuplicatesEngine(_f.Cache).ScanAsync(_f.Pc.Context(ScanDepth.Deep)).GetAwaiter().GetResult();
        Assert.True(report.Stats.FilesSeen >= R.Stats.FilesSeen);
        // Deep is more sensitive, yet different scenes still don't count as look-alikes.
        var beach = Assert.Single(report.SimilarImages);
        Assert.Equal(4, beach.Images.Count);
        Assert.All(report.ExactGroups.SelectMany(g => g.Copies), c => Assert.DoesNotContain(@"\AppData\", c.Path));
        var after = _f.Pc.DiffFrom(_f.Before);
        Assert.True(after.Identical, after.Format());
    }

    [Fact]
    public void Rescan_uses_the_hash_cache()
    {
        var again = new DuplicatesEngine(_f.Cache).ScanAsync(_f.Pc.Context(ScanDepth.Quick)).GetAwaiter().GetResult();
        Assert.Equal(0, again.Stats.PartialHashed);
        Assert.Equal(0, again.Stats.FullHashed);
        Assert.Equal(0, again.Stats.ImagesHashed);
        Assert.True(again.Stats.CacheHits > 0);
        Assert.Equal(R.ExactGroups.Count, again.ExactGroups.Count);
    }
}
