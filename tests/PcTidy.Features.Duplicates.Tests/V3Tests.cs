using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Images;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.Features.Duplicates.UI;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>v3: the probable-duplicate tier for huge files ("Check fully"), and the photo facts for side-by-side compare.</summary>
public sealed class ProbableTierTests
{
    private const int Mb = 1024 * 1024;
    /// <summary>Small stand-ins for "over 2 GB": anything over 4 MB counts as huge here.</summary>
    private static readonly DuplicateScanOptions HugeAt4Mb = new() { HugeFileBytes = 4 * Mb, FindSimilarImages = false, FindJunk = false, FindWrongExtensions = false };

    /// <summary>Two 5 MB "movies" equal in their start, middle and end blocks but not in between, and two truly identical "backups".</summary>
    private static (string MovieA, string MovieB, string IsoA, string IsoB) Build(HandMadePc pc)
    {
        var movie = HandMadePc.Bytes(10, 5 * Mb);
        var other = (byte[])movie.Clone();
        other[(int)(1.5 * Mb)] ^= 0xFF; // between the start block and the middle block: samples can't see it
        var iso = HandMadePc.Bytes(11, 5 * Mb + 123);
        return (pc.Put(Path.Combine(pc.K.Documents, "movie.mkv"), movie), pc.Put(Path.Combine(pc.K.Downloads, "movie.mkv"), other),
                pc.Put(Path.Combine(pc.K.Documents, "backup.iso"), iso), pc.Put(Path.Combine(pc.K.Downloads, "backup.iso"), iso));
    }

    [Fact]
    public async Task Huge_files_matching_by_samples_are_never_ticked_or_acted_on_until_a_full_hash_proves_it()
    {
        using var pc = new HandMadePc("probable");
        var (movieA, movieB, isoA, isoB) = Build(pc);
        var cache = new HashCache();
        var report = await new DuplicatesEngine(cache).ScanAsync(pc.Context(), HugeAt4Mb);

        Assert.Empty(report.ExactGroups);
        Assert.Equal(0, report.Stats.FullHashed);
        Assert.Equal(2, report.ProbableGroups.Count);
        Assert.All(report.ProbableGroups, g =>
        {
            Assert.True(g.IsProbable);
            Assert.Empty(g.AutoTicked);
            Assert.Equal(SampledHash.Compute(g.KeptPath, g.Size), g.ContentHash);
            // A sampled match can never become a removal.
            Assert.Throws<InvalidOperationException>(() => DuplicatePlanner.ForCopy(g, g.Others.First(), s => s));
        });
        Assert.Equal(0, report.ReclaimableBytes);

        // Standard flow / Optimize-all: shown so you know, Check, never ticked, no action at all.
        var items = DuplicatesScanner.ToScanResult(report, TimeSpan.Zero).Groups.Single(g => g.Id == "duplicates.probable").Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal(Badge.Check, i.Badge);
            Assert.False(i.DefaultSelected);
            Assert.Empty(i.Actions);
            Assert.NotNull(i.DisabledReason);
            Assert.Contains("Probably the same", i.Reason);
        });

        // "Check fully": the movies differ after all, the backups are identical.
        var movies = report.ProbableGroups.Single(g => g.KeptPath.EndsWith("movie.mkv"));
        var differ = await ProbableVerifier.CheckFullyAsync(movies, cache);
        Assert.Empty(differ.Confirmed);
        Assert.Equal(2, differ.Different.Count);

        var isos = report.ProbableGroups.Single(g => g.KeptPath.EndsWith("backup.iso"));
        var same = await ProbableVerifier.CheckFullyAsync(isos, cache);
        var verified = Assert.Single(same.Confirmed);
        Assert.True(verified.CheckedFully);
        Assert.False(verified.IsProbable);
        Assert.Empty(verified.AutoTicked);
        Assert.Equal(FileHash.Full(isoA), verified.ContentHash);
        Assert.NotEqual(isos.ContentHash, verified.ContentHash);
        var plan = DuplicatePlanner.ForCopy(verified, verified.Others.Single(), s => s);
        Assert.Equal(FileHash.Full(isoB), plan.Args![DuplicatePlanner.ArgVerifyHash]);
        Assert.Equal(verified.KeptPath, plan.Args[DuplicatePlanner.ArgKeepPath]);

        // The full hashes are remembered: a rescan lists the backups as real duplicates and the movies not at all.
        var again = await new DuplicatesEngine(cache).ScanAsync(pc.Context(), HugeAt4Mb);
        var exact = Assert.Single(again.ExactGroups);
        Assert.Contains(exact.Copies, c => DefaultPcFixture.Same(c.Path, isoB));
        Assert.Empty(again.ProbableGroups);
        Assert.True(File.Exists(movieA) && File.Exists(movieB));
    }

    [Fact]
    public async Task A_copy_changed_after_the_scan_is_left_out_of_the_full_check()
    {
        using var pc = new HandMadePc("probable-changed");
        var (_, _, isoA, isoB) = Build(pc);
        var report = await new DuplicatesEngine().ScanAsync(pc.Context(), HugeAt4Mb);
        File.SetLastWriteTimeUtc(isoB, DateTime.UtcNow);
        var check = await ProbableVerifier.CheckFullyAsync(report.ProbableGroups.Single(g => g.KeptPath.EndsWith("backup.iso")), new HashCache());
        Assert.Empty(check.Confirmed);
        Assert.Contains(check.Skipped, p => DefaultPcFixture.Same(p, isoB));
        Assert.True(File.Exists(isoA));
    }

    [Fact]
    public async Task On_the_page_ticking_a_probable_copy_runs_the_full_check_and_only_a_full_match_reaches_the_plan()
    {
        using var pc = new HandMadePc("probable-page");
        var (_, _, _, isoB) = Build(pc);
        var report = await new DuplicatesEngine().ScanAsync(pc.Context(), HugeAt4Mb);
        var vm = new DuplicatesViewModel(pc.Host, new DuplicatesModule());
        vm.Load(report);
        Assert.Equal("Huge files (2)", vm.ProbableTabTitle);
        Assert.Empty(vm.SelectedItems());

        var isoRow = vm.Probable.Single(g => g.Group.KeptPath.EndsWith("backup.iso"));
        Assert.StartsWith("Probably the same (not fully checked)", isoRow.Header);
        var other = isoRow.Copies.Single(c => !c.IsKept);
        Assert.True(other.CanTick);
        Assert.False(other.CanRemove);
        other.IsChecked = true;
        // Ticked, but nothing is in the plan while the full hash runs.
        Assert.True(isoRow.IsChecking);
        Assert.Empty(vm.SelectedItems());
        await WaitUntil(() => !vm.Probable.Contains(isoRow));

        var confirmed = vm.Probable.Single(g => g.Group.KeptPath.EndsWith("backup.iso"));
        Assert.StartsWith("Checked fully", confirmed.Header);
        var item = Assert.Single(vm.SelectedItems());
        Assert.True(DefaultPcFixture.Same(isoB, item.SourcePath!));
        Assert.Equal(FileHash.Full(isoB), item.Args![DuplicatePlanner.ArgVerifyHash]);

        // The movies aren't the same after all: the group goes, with a note, and nothing new is selected.
        var movieRow = vm.Probable.Single(g => g.Group.KeptPath.EndsWith("movie.mkv"));
        await vm.CheckFullyAsync(movieRow);
        Assert.DoesNotContain(vm.Probable, g => g.Group.KeptPath.EndsWith("movie.mkv"));
        Assert.Contains(vm.Notes, n => n.Contains("not the same after all"));
        Assert.Single(vm.SelectedItems());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The full check didn't finish in time.");
            await Task.Delay(20);
        }
    }
}

/// <summary>Side-by-side compare: "keep this one" changes the kept copy; photo facts come from EXIF.</summary>
[Collection("default-pc")]
public sealed class CompareTests(DefaultPcFixture f)
{
    [Fact]
    public void Keep_this_one_updates_the_kept_copy_for_duplicates_and_look_alike_pictures()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new DuplicatesViewModel(f.Pc.Host, new DuplicatesModule());
                vm.Load(f.Report);

                var group = vm.Groups.First(g => g.Copies.Count >= 2 && g.Copies.All(c => !c.Copy.IsHardLink));
                var compare = CompareViewModel.For(group);
                var window = new CompareWindow(compare);
                window.Measure(new Size(1000, 700));
                window.Arrange(new Rect(0, 0, 1000, 700));
                var kept = group.Group.KeptPath;
                Assert.Equal(kept, compare.Left!.Path);
                Assert.True(compare.Left.IsKept);
                var right = compare.Right!;
                Assert.False(right.IsKept);
                right.KeepToggle = true;
                Assert.Equal(right.Path, group.Group.KeptPath);
                Assert.Equal("You chose to keep this copy.", group.Group.KeepReason);
                Assert.Equal(right.Path, compare.Left!.Path);
                Assert.Equal(kept, compare.Right!.Path);
                // Turning "keep" off is ignored: one copy is always kept.
                compare.Left.KeepToggle = false;
                Assert.Equal(right.Path, group.Group.KeptPath);
                window.Close();

                var similar = vm.Similar.First();
                var pictures = CompareViewModel.For(similar);
                var suggested = similar.Group.SuggestedKeep;
                var pick = pictures.Right!.Path;
                pictures.Right.KeepToggle = true;
                Assert.Equal(pick, similar.Group.SuggestedKeep);
                Assert.Contains("You chose", similar.KeepLine);
                Assert.False(similar.Images.Single(i => i.Path == pick).CanRemove);
                var old = similar.Images.Single(i => i.Path == suggested);
                Assert.Equal(old.Image.CanBeRemoved, old.CanRemove);
                Assert.Equal("Kept (your choice)", similar.Images.Single(i => i.Path == pick).Likeness);
                if (old.CanRemove)
                {
                    old.IsChecked = true;
                    Assert.Contains(vm.SelectedItems(), i => i.SourcePath == suggested && i.Description.Contains(Path.GetFileName(pick)));
                }

                // The facts the panel shows: pixel size for a real picture.
                var facts = PhotoFacts.Read(pick);
                Assert.True(facts.Width > 0 && facts.Height > 0);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void Compare_notes_always_say_why_a_copy_is_tagged()
    {
        var vm = new DuplicatesViewModel(f.Pc.Host, new DuplicatesModule());
        vm.Load(f.Report);
        foreach (var row in vm.Groups.Concat(vm.FolderGroups).SelectMany(g => g.Copies).Where(r => r.Tag is not null and not "Kept"))
        {
            var note = CompareViewModel.NoteFor(row);
            Assert.NotNull(note);
            Assert.NotEqual(row.Tag, note); // never a bare "Protected" / "Reference" / "Hard link"
            Assert.Contains(":", note);
        }
    }
    /// <summary>A JPEG with an EXIF block: Make, Model and DateTimeOriginal (big-endian TIFF inside).</summary>
    public static byte[] JpegWithCamera(string make, string model, string taken)
    {
        var tiff = new byte[512];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        int data = 200;
        void Entry(int at, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at + 4), count);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at + 8), value);
        }
        uint Text(string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); b.CopyTo(tiff, data); var at = (uint)data; data += b.Length; return at; }
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 3);
        Entry(10, 0x010F, 2, (uint)make.Length + 1, Text(make));
        Entry(22, 0x0110, 2, (uint)model.Length + 1, Text(model));
        Entry(34, 0x8769, 4, 1, 100);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(100), 1);
        Entry(102, 0x9003, 2, 20, Text(taken));
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var len = (ushort)(app1.Length + 2);
        return new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(app1).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
    }

    [Fact]
    public void Exif_camera_and_date_taken_are_read_and_garbage_gives_nothing()
    {
        var canon = PhotoFacts.ParseExif(JpegWithCamera("Canon", "Canon EOS R6", "2021:06:05 14:30:00"));
        Assert.Equal("Canon EOS R6", canon.Camera);
        Assert.Equal(new DateTime(2021, 6, 5, 14, 30, 0), canon.Taken);
        var nikon = PhotoFacts.ParseExif(JpegWithCamera("NIKON CORPORATION", "D750", "2019:01:02 03:04:05"));
        Assert.Equal("NIKON CORPORATION D750", nikon.Camera);
        foreach (var bad in new[] { Encoding.ASCII.GetBytes("not a picture"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 1, 2 } })
        {
            var none = PhotoFacts.ParseExif(bad);
            Assert.Null(none.Camera);
            Assert.Null(none.Taken);
        }
        var rng = new Random(3);
        var junk = new byte[2048];
        for (int i = 0; i < 200; i++) { rng.NextBytes(junk); junk[0] = 0xFF; junk[1] = 0xD8; _ = PhotoFacts.ParseExif(junk); }
    }

    /// <summary>Every icon name used in the page's XAML exists in WPF-UI (a wrong one only fails at runtime).</summary>
    [Fact]
    public void Every_symbol_name_exists_in_wpf_ui()
    {
        var src = Path.Combine(Area.FindCodeRoot(), "src", "PcTidy.Features.Duplicates");
        var symbol = new Regex(@"(?:Symbol=""|\{ui:SymbolIcon |ActionIcon=""|Icon="")(\w+)");
        var names = Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(p => !p.Contains(@"\obj\"))
            .SelectMany(p => symbol.Matches(File.ReadAllText(p)).Select(m => m.Groups[1].Value))
            .Where(n => !n.StartsWith("Binding") && n != "ui").ToHashSet();
        Assert.Contains("SplitVertical24", names);
        foreach (var n in names) Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(n, out _), $"Unknown WPF-UI symbol: {n}");
    }
}
