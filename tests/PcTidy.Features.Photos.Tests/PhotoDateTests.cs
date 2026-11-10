using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Photos.Photos;
using PcTidy.Features.Photos.Shared;
using Xunit;

namespace PcTidy.Features.Photos.Tests;

/// <summary>Date folders: EXIF date taken first, modified date as the fallback, ISO names in every culture.</summary>
public class PhotoDateTests
{
    private static PhotoRequest Request(params string[] roots) => new() { Roots = roots, FindBlurry = false, FindBursts = false };

    [Fact]
    public async Task Exif_date_taken_decides_the_folder_and_the_modified_date_is_the_fallback()
    {
        using var pc = TestPc.HandMade("dates");
        var pics = pc.K.Pictures;
        var withExif = TestPc.Put(Path.Combine(pics, "IMG_0001.jpg"), SyntheticPhotos.Jpeg(1, new DateTime(2024, 3, 5, 14, 30, 0)), daysOld: 2);
        var noExif = TestPc.Put(Path.Combine(pics, "scan.jpg"), SyntheticPhotos.Jpeg(2), daysOld: 1);
        File.SetLastWriteTime(noExif, new DateTime(2023, 11, 20, 12, 0, 0));
        var video = TestPc.Put(Path.Combine(pics, "clip.mp4"), new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, daysOld: 1);
        File.SetLastWriteTime(video, new DateTime(2022, 1, 9, 12, 0, 0));
        var notAPhoto = TestPc.Put(Path.Combine(pics, "notes.txt"), "hello");

        var preview = await pc.PhotoPlanner().PlanAsync(Request(pics), null, CancellationToken.None);

        var exifMove = preview.Moves.Single(m => TestPc.Same(m.SourcePath, withExif));
        Assert.Equal(DateSource.Taken, exifMove.Date.Source);
        Assert.True(TestPc.Same(Path.Combine(pics, "2024", "2024-03", "IMG_0001.jpg"), exifMove.DestinationPath), exifMove.DestinationPath);
        Assert.StartsWith("Taken", exifMove.Reason);

        var fallback = preview.Moves.Single(m => TestPc.Same(m.SourcePath, noExif));
        Assert.Equal(DateSource.Modified, fallback.Date.Source);
        Assert.True(TestPc.Same(Path.Combine(pics, "2023", "2023-11", "scan.jpg"), fallback.DestinationPath), fallback.DestinationPath);
        Assert.Contains("modified date", fallback.Reason);

        // A video without readable media metadata falls back to its modified date too.
        var clip = preview.Moves.Single(m => TestPc.Same(m.SourcePath, video));
        Assert.True(clip.IsVideo);
        Assert.Equal("2022-01", clip.Month);

        Assert.Contains(preview.Skipped, s => TestPc.Same(s.Path, notAPhoto) && s.Kind == SkipKind.NoRule);
        // Each move creates its folders first (year, then month) and never overwrites.
        Assert.Equal(new[] { ActionKind.CreateDirectory, ActionKind.CreateDirectory, ActionKind.MovePath }, exifMove.Items.Select(i => i.Kind));
        Assert.All(preview.Moves, m => Assert.Equal(NameClashPolicy.AutoNumber, m.Items.Last().ClashPolicy));
        Assert.All(preview.Moves.SelectMany(m => m.Items), i => Assert.True(pc.Host.Safety.Check(i).Allowed, i.Description));
    }

    [Fact]
    public async Task Folder_names_are_iso_in_every_culture()
    {
        using var pc = TestPc.HandMade("culture");
        var pics = pc.K.Pictures;
        TestPc.Put(Path.Combine(pics, "IMG_0002.JPG"), SyntheticPhotos.Jpeg(3, new DateTime(2025, 10, 31, 23, 59, 0)), daysOld: 2);
        TestPc.Put(Path.Combine(pics, "VIDEO.AVI"), new byte[] { 1, 2, 3 }, daysOld: 2);

        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        PhotoPreview preview;
        try
        {
            // Turkish has the dotless i ("AVI".ToLower() is "avı") and Thai has its own calendar: neither may leak in.
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            preview = await pc.PhotoPlanner().PlanAsync(Request(pics), null, CancellationToken.None);
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var thai = await pc.PhotoPlanner().PlanAsync(Request(pics), null, CancellationToken.None);
            Assert.Equal(preview.Moves.Select(m => m.DestinationPath), thai.Moves.Select(m => m.DestinationPath));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }

        Assert.Equal(2, preview.Moves.Count);
        var photo = preview.Moves.Single(m => m.Name == "IMG_0002.JPG");
        Assert.Equal("2025", photo.Year);
        Assert.Equal("2025-10", photo.Month);
        Assert.All(preview.Moves, m => Assert.Matches(new Regex(@"\\(19|20)\d{2}\\(19|20)\d{2}-\d{2}\\[^\\]+$"), m.DestinationPath));
    }

    [Fact]
    public async Task Name_clashes_get_a_number_and_files_already_in_place_stay()
    {
        using var pc = TestPc.HandMade("clash");
        var pics = pc.K.Pictures;
        var taken = new DateTime(2024, 6, 1, 10, 0, 0);
        TestPc.Put(Path.Combine(pics, "2024", "2024-06", "IMG_1.jpg"), SyntheticPhotos.Jpeg(4, taken), daysOld: 5);
        var loose = TestPc.Put(Path.Combine(pics, "IMG_1.jpg"), SyntheticPhotos.Jpeg(5, taken), daysOld: 5);

        var preview = await pc.PhotoPlanner().PlanAsync(Request(pics) with { IncludeSubfolders = true }, null, CancellationToken.None);

        var move = Assert.Single(preview.Moves);
        Assert.True(TestPc.Same(loose, move.SourcePath));
        Assert.Equal("IMG_1 (2).jpg", Path.GetFileName(move.DestinationPath));
        Assert.True(move.Renumbered);
        // Year folders are already sorted: not looked into again.
        Assert.Contains(preview.Notes, n => n.Contains("year folders"));
    }

    [Fact]
    public async Task Generated_pc_sorts_downloads_and_pictures_and_never_touches_online_only_photos()
    {
        using var pc = TestPc.Generated("testbed-dates", SandboxProfile.Default);
        // Pictures is redirected into OneDrive on the Default profile: allowed for this test.
        CloudFolders.Allow(pc.K.Pictures, pc.Host.Settings);
        var before = PcTidy.TestbedGen.TestbedBuilder.Manifest(pc.Root);

        var preview = await pc.PhotoPlanner().PlanAsync(new PhotoRequest { Roots = new[] { pc.K.Pictures, pc.K.Downloads }, IncludeSubfolders = true },
            null, CancellationToken.None);

        var holiday = preview.Moves.Single(m => m.Name == "IMG_20250615_093000.jpg");
        Assert.Equal(DateSource.Taken, holiday.Date.Source);
        Assert.True(TestPc.Same(Path.Combine(pc.K.Pictures, "2025", "2025-06", "IMG_20250615_093000.jpg"), holiday.DestinationPath));
        Assert.Contains("OneDrive only renames it", holiday.Reason);

        var download = preview.Moves.Single(m => TestPc.Same(m.SourcePath, Path.Combine(pc.K.Downloads, "PXL_20240802_181212.jpg")));
        Assert.True(TestPc.Same(Path.Combine(pc.K.Downloads, "2024", "2024-08", "PXL_20240802_181212.jpg"), download.DestinationPath));

        // Online-only photos (Camera Roll placeholders) are skipped with a reason and never planned.
        var placeholders = pc.Info!.Placeholders.Select(pc.Info.Abs).Where(p => PathUtil.IsUnder(p, pc.K.Pictures)).ToList();
        Assert.NotEmpty(placeholders);
        foreach (var p in placeholders)
        {
            Assert.Contains(preview.Skipped, s => TestPc.Same(s.Path, p) && s.Kind == SkipKind.OnlineOnly && s.Reason.Contains("Never downloaded"));
            Assert.DoesNotContain(preview.Moves, m => TestPc.Same(m.SourcePath, p));
            Assert.DoesNotContain(preview.Flags, f => TestPc.Same(f.SourcePath, p));
        }
        // Planning is read-only: nothing on disk changed.
        Assert.True(pc.DiffFrom(before).Identical, pc.DiffFrom(before).Format());
    }

    [Fact]
    public void Windows_property_system_reads_date_taken_read_only()
    {
        using var pc = TestPc.HandMade("props");
        var path = TestPc.Put(Path.Combine(pc.K.Pictures, "with-date.jpg"), SyntheticPhotos.Jpeg(6, new DateTime(2021, 4, 2, 8, 15, 0)));
        var before = File.ReadAllBytes(path);

        var taken = WindowsMediaProperties.Instance.PhotoDateTaken(path);
        // Not a video: no media-created date, and no crash.
        _ = WindowsMediaProperties.Instance.VideoDateCreated(path);

        Assert.Equal(new DateTime(2021, 4, 2, 8, 15, 0), taken);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Implausible_camera_dates_fall_back_to_the_modified_date()
    {
        var props = new FixedProperties(new DateTime(2099, 1, 1));
        var resolver = new MediaDateResolver(props, () => new DateTime(2026, 11, 10));
        var entry = new FileEntry(@"C:\x\clip.mov", "clip.mov", false, 10, FileAttributes.Normal, DateTime.UtcNow, new DateTime(2020, 5, 5, 12, 0, 0, DateTimeKind.Utc), DateTime.UtcNow, 1);
        var date = resolver.Resolve(entry, ScanDepth.Quick);
        Assert.Equal(DateSource.Modified, date.Source);
        Assert.Equal(2020, date.Local.Year);

        // An online-only file is never opened: straight to the modified date.
        var placeholder = entry with { Attributes = FileAttributes.Offline };
        var counting = new FixedProperties(new DateTime(2021, 1, 1));
        Assert.Equal(DateSource.Modified, new MediaDateResolver(counting).Resolve(placeholder, ScanDepth.Deep).Source);
        Assert.Equal(0, counting.Calls);
    }

    private sealed class FixedProperties(DateTime value) : IMediaProperties
    {
        public int Calls { get; private set; }
        public DateTime? PhotoDateTaken(string path) { Calls++; return value; }
        public DateTime? VideoDateCreated(string path) { Calls++; return value; }
    }
}
