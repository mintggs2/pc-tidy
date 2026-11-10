using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.Shared;
using Xunit;

namespace PcTidy.Features.Photos.Tests;

/// <summary>Blurry shots and burst near-duplicates on synthetic photos (and no false alarms on the testbed's photos).</summary>
public class PhotoQualityTests
{
    [Fact]
    public void Sharpness_separates_crisp_from_soft_at_every_small_size()
    {
        var crisp = new byte[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) crisp[y * 64 + x] = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 40 : 210);
        var soft = new byte[crisp.Length];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int s = 0, n = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++) { s += crisp[Math.Clamp(y + dy, 0, 63) * 64 + Math.Clamp(x + dx, 0, 63)]; n++; }
                soft[y * 64 + x] = (byte)(s / n);
            }
        Assert.InRange(ImageMath.Sharpness(crisp, 64, 64), 90, 200);
        Assert.True(ImageMath.Sharpness(soft, 64, 64) < BlurCheck.Threshold);
        Assert.Equal(0, ImageMath.Sharpness(new byte[64 * 64], 64, 64));
        Assert.Equal(0, ImageMath.Distance(ImageMath.DifferenceHash(crisp, 64, 64), ImageMath.DifferenceHash(crisp, 64, 64)));
    }

    [Fact]
    public async Task Blurry_shots_are_flagged_unticked_with_a_preview()
    {
        using var pc = TestPc.HandMade("blur");
        var pics = pc.K.Pictures;
        var sharp = TestPc.Put(Path.Combine(pics, "sharp.jpg"), SyntheticPhotos.Jpeg(11, new DateTime(2024, 5, 1, 9, 0, 0)));
        var blurry = TestPc.Put(Path.Combine(pics, "shaky.jpg"), SyntheticPhotos.Jpeg(12, new DateTime(2024, 5, 1, 12, 0, 0), blur: 2));

        var preview = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pics } }, null, CancellationToken.None);

        var flag = Assert.Single(preview.Flags);
        Assert.Equal(PhotoFlagKind.Blurry, flag.Kind);
        Assert.True(TestPc.Same(blurry, flag.SourcePath));
        Assert.Contains("best guess", flag.Reason);
        Assert.Equal(ActionKind.QuarantinePath, flag.Item.Kind);
        Assert.True(pc.Host.Safety.Check(flag.Item).Allowed);

        var result = PhotoResultBuilder.Build(preview);
        var item = result.Groups.Single(g => g.Id == PhotoResultBuilder.BlurryGroupId).Items.Single();
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.True(TestPc.Same(blurry, item.PreviewPath!));
        // Both photos are still sorted by date; the sharp one has no flag.
        Assert.Equal(2, preview.Moves.Count);
        Assert.DoesNotContain(preview.Flags, f => TestPc.Same(f.SourcePath, sharp));

        // Switched off: no flags at all.
        var off = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pics }, FindBlurry = false }, null, CancellationToken.None);
        Assert.Empty(off.Flags);
    }

    [Fact]
    public async Task Burst_keeps_the_sharpest_and_flags_the_others()
    {
        using var pc = TestPc.HandMade("burst");
        var pics = pc.K.Pictures;
        var t = new DateTime(2025, 8, 9, 17, 0, 0);
        var first = TestPc.Put(Path.Combine(pics, "IMG_0101.jpg"), SyntheticPhotos.Jpeg(21, t, blur: 1));
        var best = TestPc.Put(Path.Combine(pics, "IMG_0102.jpg"), SyntheticPhotos.Jpeg(21, t.AddSeconds(1), shiftX: 2));
        var third = TestPc.Put(Path.Combine(pics, "IMG_0103.jpg"), SyntheticPhotos.Jpeg(21, t.AddSeconds(2), shiftX: 4, blur: 2));
        // Same moment, different scene: not part of the burst. Same scene ten seconds later: a new shot, not a burst.
        var other = TestPc.Put(Path.Combine(pics, "IMG_0104.jpg"), SyntheticPhotos.Jpeg(99, t.AddSeconds(1)));
        var later = TestPc.Put(Path.Combine(pics, "IMG_0105.jpg"), SyntheticPhotos.Jpeg(21, t.AddSeconds(12), shiftX: 1));

        var preview = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pics } }, null, CancellationToken.None);

        var burst = preview.Flags.Where(f => f.Kind == PhotoFlagKind.BurstExtra).ToList();
        Assert.Equal(2, burst.Count);
        Assert.Contains(burst, f => TestPc.Same(f.SourcePath, first));
        Assert.Contains(burst, f => TestPc.Same(f.SourcePath, third));
        Assert.All(burst, f =>
        {
            Assert.True(TestPc.Same(best, f.KeptPath!));
            Assert.Equal(3, f.BurstSize);
            Assert.Contains("IMG_0102.jpg", f.Reason);
        });
        // The third shot is also soft: said once, on its burst row (no second "blurry" row for the same file).
        Assert.Contains("also looks blurry", burst.Single(f => TestPc.Same(f.SourcePath, third)).Reason);
        Assert.Equal(burst.Count + preview.Flags.Count(f => f.Kind == PhotoFlagKind.Blurry), preview.Flags.Select(f => f.SourcePath).Distinct().Count());
        foreach (var keep in new[] { best, other, later }) Assert.DoesNotContain(preview.Flags, f => TestPc.Same(f.SourcePath, keep));

        var items = PhotoResultBuilder.Build(preview).Groups.Single(g => g.Id == PhotoResultBuilder.BurstGroupId).Items;
        Assert.All(items, i =>
        {
            Assert.Equal(Badge.Check, i.Badge);
            Assert.False(i.DefaultSelected);
            Assert.NotNull(i.PreviewPath);
            Assert.Equal(ActionKind.QuarantinePath, Assert.Single(i.Actions).Kind);
        });
    }

    /// <summary>PHO-1: when every shot of a burst is soft, the one the burst keeps is not also offered as blurry.</summary>
    [Fact]
    public async Task The_kept_shot_of_an_all_blurry_burst_is_never_offered_as_blurry()
    {
        using var pc = TestPc.HandMade("burst-blurry");
        var pics = pc.K.Pictures;
        var t = new DateTime(2025, 8, 9, 17, 0, 0);
        var shots = new[]
        {
            TestPc.Put(Path.Combine(pics, "IMG_0201.jpg"), SyntheticPhotos.Jpeg(31, t, blur: 2)),
            TestPc.Put(Path.Combine(pics, "IMG_0202.jpg"), SyntheticPhotos.Jpeg(31, t.AddSeconds(1), shiftX: 2, blur: 2)),
            TestPc.Put(Path.Combine(pics, "IMG_0203.jpg"), SyntheticPhotos.Jpeg(31, t.AddSeconds(2), shiftX: 4, blur: 2)),
        };

        var preview = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pics } }, null, CancellationToken.None);

        var burst = preview.Flags.Where(f => f.Kind == PhotoFlagKind.BurstExtra).ToList();
        Assert.Equal(2, burst.Count);
        var kept = burst[0].KeptPath!;
        Assert.Contains(shots, s => TestPc.Same(s, kept));
        // Every flag together (burst extras and blurry) still leaves the kept shot.
        Assert.DoesNotContain(preview.Flags, f => TestPc.Same(f.SourcePath, kept));
        Assert.Equal(shots.Length - 1, preview.Flags.Select(f => f.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Testbed_photos_raise_no_false_blur_alarms_and_identical_copies_are_one_burst()
    {
        using var pc = TestPc.Generated("quality", SandboxProfile.Minimal);
        var preview = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pc.K.Pictures, pc.K.Downloads }, IncludeSubfolders = true },
            null, CancellationToken.None);

        Assert.True(preview.Checked >= 10, $"only {preview.Checked} photos checked");
        Assert.DoesNotContain(preview.Flags, f => f.Kind == PhotoFlagKind.Blurry);
        // "IMG_20250614_101500.jpg" and "… (1).jpg" in Pictures\Holiday 2025 are the same photo: the shorter name is kept.
        var copy = Assert.Single(preview.Flags);
        Assert.Equal(PhotoFlagKind.BurstExtra, copy.Kind);
        Assert.Equal("IMG_20250614_101500 (1).jpg", copy.Name);
        Assert.Equal("IMG_20250614_101500.jpg", Path.GetFileName(copy.KeptPath));
        Assert.Contains("Looks identical", copy.Reason);
    }

    [Fact]
    public void Burst_finder_only_links_close_times_and_close_hashes()
    {
        var m = new ImageMetrics(100, 100, 80, 30, 0xFF00FF00FF00FF00);
        var t = new DateTime(2025, 1, 1, 12, 0, 0);
        var shots = new[]
        {
            new BurstCandidate("a.jpg", t, m, 10),
            new BurstCandidate("b.jpg", t.AddSeconds(2), m with { Sharpness = 95, Hash = m.Hash ^ 0b111 }, 10),
            new BurstCandidate("c.jpg", t.AddSeconds(4), m with { Hash = m.Hash ^ 0b1 }, 10),      // chained through b
            new BurstCandidate("d.jpg", t.AddSeconds(4.5), m with { Hash = ~m.Hash }, 10),          // looks different
            new BurstCandidate("e.jpg", t.AddSeconds(30), m, 10),                                   // much later
        };
        var burst = Assert.Single(BurstFinder.Find(shots, BurstFinder.MaxDistance(ScanDepth.Quick)));
        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, burst.Shots.Select(s => s.Path).OrderBy(p => p));
        Assert.Equal("b.jpg", burst.Keep.Path);
    }
}
