using System.Globalization;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Takeout;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>Google Takeout mode: sidecar parsing, Takeout's naming quirks, date folders (also under tr-TR), and a real run + undo.</summary>
public class TakeoutTests
{
    // 2019-09-20 12:00 UTC and 2019-12-31 12:00 UTC.
    private const long Sep2019 = 1568980800;
    private const long Dec2019 = 1577793600;

    private static string Sidecar(string title, long? taken, long? created = null)
    {
        var sb = new StringBuilder("{\n  \"title\": \"").Append(title).Append("\",\n  \"description\": \"\",\n  \"imageViews\": \"3\"");
        if (created is { } c) sb.Append($",\n  \"creationTime\": {{ \"timestamp\": \"{c}\", \"formatted\": \"Jan 1, 2020, 10:00:00 AM UTC\" }}");
        if (taken is { } t) sb.Append($",\n  \"photoTakenTime\": {{ \"timestamp\": \"{t}\", \"formatted\": \"Sep 20, 2019, 12:00:00 PM UTC\" }}");
        sb.Append(",\n  \"geoData\": { \"latitude\": 0.0, \"longitude\": 0.0 }\n}");
        return sb.ToString();
    }

    [Fact]
    public void Sidecars_are_parsed_culture_invariant_and_odd_files_are_not_sidecars()
    {
        var s = TakeoutSidecar.Parse("a.json", Sidecar("IMG_0001.JPG", Sep2019, Dec2019))!;
        Assert.Equal("IMG_0001.JPG", s.Title);
        Assert.Equal(new DateTime(2019, 9, 20, 12, 0, 0, DateTimeKind.Utc), s.PhotoTakenUtc);
        Assert.Equal(DateTimeKind.Utc, s.PhotoTakenUtc!.Value.Kind);
        Assert.Equal(new DateTime(2019, 12, 31, 12, 0, 0), s.CreationUtc);
        // Numbers instead of strings, a 0 "unknown" time, an album's metadata.json, junk.
        Assert.Equal(new DateTime(2019, 9, 20, 12, 0, 0), TakeoutSidecar.Parse("b.json", $"{{\"title\":\"x.jpg\",\"photoTakenTime\":{{\"timestamp\":{Sep2019}}}}}")!.PhotoTakenUtc);
        Assert.Null(TakeoutSidecar.Parse("c.json", Sidecar("x.jpg", 0, Dec2019))!.PhotoTakenUtc);
        Assert.Null(TakeoutSidecar.Parse("metadata.json", "{\"title\":\"Holiday\",\"description\":\"\",\"access\":\"protected\"}"));
        Assert.Null(TakeoutSidecar.Parse("d.json", "not json"));
        Assert.Null(TakeoutSidecar.Parse("e.json", "[1,2,3]"));
        Assert.Null(TakeoutSidecar.Parse("f.json", "{\"photoTakenTime\":{\"timestamp\":\"abc\"}}"));
    }

    [Fact]
    public void Takeout_names_are_understood()
    {
        Assert.Equal(("img_0001.jpg", 0), TakeoutLayout.SidecarKey("IMG_0001.jpg.json"));
        Assert.Equal(("img_0001.jpg", 0), TakeoutLayout.SidecarKey("IMG_0001.jpg.supplemental-metadata.json"));
        Assert.Equal(("img_0001.jpg", 0), TakeoutLayout.SidecarKey("IMG_0001.jpg.supplemental-met.json"));
        Assert.Equal(("img_0001.jpg", 0), TakeoutLayout.SidecarKey("IMG_0001.jpg.s.json"));
        Assert.Equal(("img_0001.jpg", 1), TakeoutLayout.SidecarKey("IMG_0001.jpg(1).json"));
        Assert.Equal(("img_0001.jpg", 2), TakeoutLayout.SidecarKey("IMG_0001.jpg.supplemental-metadata(2).json"));
        Assert.Equal(new[] { ("img_0001.jpg", 1), ("img_0001(1).jpg", 0) }, TakeoutLayout.MediaKeys("IMG_0001(1).jpg"));
        Assert.Equal("IMG_7.jpg", TakeoutLayout.OriginalOfEdited("IMG_7-edited.jpg"));
        Assert.Equal("IMG_7.jpg", TakeoutLayout.OriginalOfEdited("IMG_7-bearbeitet.jpg"));
        Assert.Null(TakeoutLayout.OriginalOfEdited("edited.jpg"));
        Assert.Equal(TakeoutFolderKind.Year, TakeoutLayout.Classify("Photos from 2019"));
        Assert.Equal(TakeoutFolderKind.Year, TakeoutLayout.Classify("Fotos von 2021"));
        Assert.Equal(TakeoutFolderKind.Album, TakeoutLayout.Classify("Holiday 2019"));
        Assert.Equal(TakeoutFolderKind.Bin, TakeoutLayout.Classify("Trash"));
        Assert.False(TakeoutLayout.IsSidecarCandidate("metadata.json"));
    }

    /// <summary>A small but quirky export under Downloads\takeout-20261110\Takeout\Google Photos.</summary>
    private static string BuildExport(string downloads)
    {
        var photos = Path.Combine(downloads, "takeout-20261110", "Takeout", "Google Photos");
        void Put(string rel, byte[] bytes) { var p = Path.Combine(photos, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, bytes); Sandbox.SetAge(p, 30); }
        void Json(string rel, string json) => Put(rel, Encoding.UTF8.GetBytes(json));
        byte[] Pic(int seed) { var b = new byte[3000 + seed]; new Random(seed).NextBytes(b); b[0] = 0xFF; b[1] = 0xD8; return b; }

        var year = "Photos from 2019";
        Put($@"{year}\IMG_0001.JPG", Pic(1)); Json($@"{year}\IMG_0001.JPG.json", Sidecar("IMG_0001.JPG", Sep2019));
        Put($@"{year}\IMG_0002.jpg", Pic(2)); Json($@"{year}\IMG_0002.jpg.supplemental-metadata.json", Sidecar("IMG_0002.jpg", Dec2019));
        Put($@"{year}\IMG_0003.jpg", Pic(3)); Json($@"{year}\IMG_0003.jpg.json", Sidecar("IMG_0003.jpg", Sep2019));
        Put($@"{year}\IMG_0003-edited.jpg", Pic(33));
        Put($@"{year}\IMG_0004(1).jpg", Pic(4)); Json($@"{year}\IMG_0004.jpg(1).json", Sidecar("IMG_0004.jpg", Dec2019));
        Put($@"{year}\IMG_0005.jpg", Pic(5)); Json($@"{year}\IMG_0005.jpg.json", Sidecar("IMG_0005.jpg", Sep2019));
        Json($@"{year}\orphan.jpg.json", Sidecar("orphan.jpg", Sep2019));
        // An album (with a Turkish dotted capital I in its name) holding the same IMG_0001.JPG Google exported twice.
        var album = "Tatil İstanbul";
        Put($@"{album}\IMG_0001.JPG", Pic(1)); Json($@"{album}\IMG_0001.JPG.json", Sidecar("IMG_0001.JPG", Sep2019));
        Put($@"{album}\IMG_0099.jpg", Pic(99)); Json($@"{album}\IMG_0099.jpg.json", Sidecar("IMG_0099.jpg", Dec2019));
        Json($@"{album}\metadata.json", "{\"title\":\"Tatil İstanbul\",\"description\":\"\"}");
        Put(@"Trash\IMG_0666.jpg", Pic(66));
        Json(@"print-subscriptions.json", "{}");
        return Path.Combine(downloads, "takeout-20261110");
    }

    private static TakeoutPlanner Planner(Sandbox sb) => new(sb.Safety, sb.InUse, new ManagedFileWalker(), sb.Clock);

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    [Fact]
    public async Task Photos_are_sorted_by_the_sidecar_date_with_sidecars_moved_along_even_in_turkish()
    {
        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            // Turkish casing ("I".ToLower() is "ı") and formats must not change anything.
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            using var sb = new Sandbox(SandboxProfile.Minimal);
            Directory.CreateDirectory(sb.Locations.Pictures);
            var source = BuildExport(sb.Downloads);
            var dest = TakeoutPlanner.DefaultDestination(sb.Locations);
            var preview = await Planner(sb).PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = dest, TimeZone = TimeZoneInfo.Utc }, null, CancellationToken.None);

            Assert.Single(preview.PhotoFolders);
            PlannedAction Photo(string name) => preview.Actions.Single(a => a.Kind == PlannedActionKind.Move && a.Name == name);
            MovePair(dest, Photo("IMG_0001.JPG"), @"2019\2019-09\IMG_0001.JPG", @"2019\2019-09\IMG_0001.JPG.json");
            MovePair(dest, Photo("IMG_0002.jpg"), @"2019\2019-12\IMG_0002.jpg", @"2019\2019-12\IMG_0002.jpg.supplemental-metadata.json");
            MovePair(dest, Photo("IMG_0004(1).jpg"), @"2019\2019-12\IMG_0004(1).jpg", @"2019\2019-12\IMG_0004.jpg(1).json");
            MovePair(dest, Photo("IMG_0099.jpg"), @"2019\2019-12\IMG_0099.jpg", @"2019\2019-12\IMG_0099.jpg.json");
            // The edited copy goes next to its original; the shared sidecar moves once, with the original.
            MovePair(dest, Photo("IMG_0003.jpg"), @"2019\2019-09\IMG_0003.jpg", @"2019\2019-09\IMG_0003.jpg.json");
            var edited = Photo("IMG_0003-edited.jpg");
            Assert.Equal(@"2019\2019-09\IMG_0003-edited.jpg", Rel(dest, edited.DestinationPath!));
            Assert.Single(edited.Items, i => i.Kind == ActionKind.MovePath);
            Assert.Contains("Edited copy", edited.Reason);
            Assert.Contains("Google's info file", Photo("IMG_0001.JPG").Reason);

            // The album's copy of IMG_0001 is the same photo: offered for quarantine with its sidecar, never ticked.
            var dup = Assert.Single(preview.Actions, a => a.Kind == PlannedActionKind.DuplicateQuarantine);
            Assert.Contains("Tatil İstanbul", dup.SourcePath);
            Assert.Equal(Badge.Check, dup.Badge);
            Assert.False(dup.DefaultSelected);
            Assert.Equal(2, dup.Items.Count(i => i.Kind == ActionKind.QuarantinePath));
            // ORG-1: tied to the copy it relies on: runs only after that copy's move, and the engine re-checks both by hash.
            var original = Photo("IMG_0001.JPG");
            var originalMove = original.Items.First(i => i.Kind == ActionKind.MovePath);
            var dupPhoto = dup.Items[0];
            Assert.Equal(new[] { originalMove.Id }, dupPhoto.DependsOn);
            Assert.Equal(originalMove.DestinationPath, dupPhoto.Args!["keepPath"]);
            Assert.Equal(FileHash.Full(dupPhoto.SourcePath!), dupPhoto.Args["verifyHash"]);
            Assert.Equal("file", dupPhoto.Args["verifyMode"]);
            Assert.Equal(new[] { dupPhoto.Id }, dup.Items[1].DependsOn); // its sidecar only together with it

            // Stays put: the bin, Takeout's own info files, a sidecar without its photo.
            Assert.Contains(preview.Skipped, s => s.Name == "IMG_0666.jpg" && s.Reason.Contains("bin"));
            Assert.Contains(preview.Skipped, s => s.Name == "metadata.json");
            Assert.Contains(preview.Skipped, s => s.Name == "print-subscriptions.json");
            Assert.Contains(preview.Skipped, s => s.Name == "orphan.jpg.json");
            Assert.All(preview.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
            Assert.All(preview.Actions.SelectMany(a => a.Items), i => Assert.True(sb.Safety.Check(i).Allowed, i.Description));

            // One plan: folders first and once each, every source moved exactly once.
            var plan = PlanAssembler.Build("t", preview.Actions);
            var folders = plan.Items.TakeWhile(i => i.Kind == ActionKind.CreateDirectory).Select(i => Rel(sb.Locations.Pictures, i.DestinationPath!)).ToList();
            Assert.Equal(new[] { "Google Photos", @"Google Photos\2019", @"Google Photos\2019\2019-09", @"Google Photos\2019\2019-12" }, folders);
            Assert.Equal(plan.Items.Count(i => i.SourcePath is not null), plan.Items.Where(i => i.SourcePath is not null).Select(i => i.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(6, preview.SidecarsMoved);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    private static void MovePair(string dest, PlannedAction a, string photo, string sidecar)
    {
        var moves = a.Items.Where(i => i.Kind == ActionKind.MovePath).ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(photo, Rel(dest, moves[0].DestinationPath!));
        Assert.Equal(sidecar, Rel(dest, moves[1].DestinationPath!));
        Assert.Equal(Badge.Safe, a.Badge);
        Assert.True(a.DefaultSelected);
    }

    [Fact]
    public async Task Album_folders_can_be_kept_and_missing_dates_fall_back_to_exif()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var source = BuildExport(sb.Downloads);
        // A photo with no sidecar: the EXIF date taken decides.
        var exifPhoto = Path.Combine(source, "Takeout", "Google Photos", "Photos from 2019", "camera.jpg");
        File.WriteAllBytes(exifPhoto, DateAndLoopTests.JpegWithExif("2018:07:04 10:20:30"));
        Sandbox.SetAge(exifPhoto, 30);
        var dest = TakeoutPlanner.DefaultDestination(sb.Locations);
        var preview = await Planner(sb).PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = dest, KeepAlbums = true, TimeZone = TimeZoneInfo.Utc }, null, CancellationToken.None);

        var albumCopy = preview.Actions.Single(a => a.Kind == PlannedActionKind.Move && a.SourcePath.Contains("Tatil") && a.Name == "IMG_0001.JPG");
        Assert.Equal(@"Albums\Tatil İstanbul\IMG_0001.JPG", Rel(dest, albumCopy.DestinationPath!));
        Assert.Equal(@"Albums\Tatil İstanbul", albumCopy.Group);
        Assert.DoesNotContain(preview.Actions, a => a.Kind == PlannedActionKind.DuplicateQuarantine);
        var camera = preview.Actions.Single(a => a.Name == "camera.jpg");
        Assert.Equal(@"2018\2018-07\camera.jpg", Rel(dest, camera.DestinationPath!));
        Assert.Contains("from the photo itself", camera.Reason);
    }

    [Fact]
    public async Task A_destination_inside_the_export_or_no_google_photos_folder_plans_nothing()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var source = BuildExport(sb.Downloads);
        var inside = await Planner(sb).PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = Path.Combine(source, "Sorted") }, null, CancellationToken.None);
        Assert.Empty(inside.Actions);
        Assert.Contains(inside.Notes, n => n.Contains("outside the Takeout folder"));

        var empty = Directory.CreateDirectory(Path.Combine(sb.Downloads, "nothing here")).FullName;
        var none = await Planner(sb).PlanAsync(new TakeoutRequest { SourceFolder = empty, DestinationFolder = TakeoutPlanner.DefaultDestination(sb.Locations) }, null, CancellationToken.None);
        Assert.Empty(none.Actions);
        Assert.Contains(none.Notes, n => n.Contains("No Google Photos folder"));
    }

    [Fact]
    public async Task Names_already_taken_in_the_destination_get_a_number_and_the_sidecar_follows()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var source = BuildExport(sb.Downloads);
        var dest = TakeoutPlanner.DefaultDestination(sb.Locations);
        var taken = Path.Combine(dest, "2019", "2019-09", "IMG_0005.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        File.WriteAllText(taken, "a different photo");
        var preview = await Planner(sb).PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = dest, TimeZone = TimeZoneInfo.Utc }, null, CancellationToken.None);
        var a = preview.Actions.Single(x => x.Name == "IMG_0005.jpg");
        Assert.True(a.Renumbered);
        MovePair(dest, a, @"2019\2019-09\IMG_0005 (2).jpg", @"2019\2019-09\IMG_0005 (2).jpg.json");
    }

    /// <summary>Real engine on a sandbox PC: sort the export, check photo + sidecar sit together, undo, tree identical.</summary>
    [Fact]
    public async Task Takeout_sort_then_undo_leaves_the_sandbox_identical()
    {
        var root = Path.Combine(Sandbox.TestbedRoot, "takeout-e2e-" + Guid.NewGuid().ToString("N")[..6]);
        using var host = PcTidyHost.CreateSandbox(root, SandboxProfile.Minimal);
        try
        {
            var k = host.Locations;
            foreach (var d in new[] { k.Downloads, k.Pictures, k.Documents, k.Desktop }) Directory.CreateDirectory(d);
            var source = BuildExport(k.Downloads);
            var before = TestbedBuilder.Manifest(root);

            var planner = new TakeoutPlanner(host.Safety, host.InUse, host.Walker);
            var preview = await planner.PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = TakeoutPlanner.DefaultDestination(k), TimeZone = TimeZoneInfo.Utc }, null, CancellationToken.None);
            // What the user would confirm: the default ticks plus the duplicate album copy.
            var plan = PlanAssembler.Build("Sort Google Takeout photos", preview.Actions);
            Assert.All(plan.Items, i => Assert.True(host.Safety.Check(i).Allowed, $"{i.Description}: {host.Safety.Check(i).Reason}"));

            // "Try it first" must say what the real run does: the album duplicate (and its info file) would be
            // quarantined, because the year-folder copy it relies on would be moved first.
            var simulated = await host.Executor.ExecuteAsync(PlanAssembler.Build("Sort Google Takeout photos", preview.Actions), new ExecuteOptions { Mode = ExecutionMode.Simulate, ContinueWithoutRestorePoint = _ => Task.FromResult(true) }, null, CancellationToken.None);
            var dupes = simulated.Steps.Where(s => s.Item.Kind == ActionKind.QuarantinePath).ToList();
            Assert.NotEmpty(dupes);
            Assert.All(dupes, s => Assert.True(s.Status == StepStatus.Simulated, $"{s.Status} {s.Item.Description}: {s.Message}"));
            var afterSimulate = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(root)).Differences
                .Where(d => !d.Path.StartsWith("Data", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(afterSimulate.Count == 0, new PcTidy.TestbedGen.Manifest.ManifestDiff(afterSimulate).Format()); // a simulation changes nothing

            var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) }, null, CancellationToken.None);
            Assert.All(run.Steps.Where(s => s.Item.Kind == ActionKind.QuarantinePath), s => Assert.Equal(StepStatus.Done, s.Status));
            Assert.True(run.DoneCount == plan.Items.Count, string.Join("\n", run.Steps.Where(s => s.Status != StepStatus.Done).Select(s => $"{s.Status} {s.Item.Description}: {s.Message}")));
            var month = Path.Combine(TakeoutPlanner.DefaultDestination(k), "2019", "2019-09");
            Assert.True(File.Exists(Path.Combine(month, "IMG_0001.JPG")));
            Assert.True(File.Exists(Path.Combine(month, "IMG_0001.JPG.json")));
            Assert.True(File.Exists(Path.Combine(month, "IMG_0003-edited.jpg")));
            Assert.False(File.Exists(Path.Combine(source, "Takeout", "Google Photos", "Photos from 2019", "IMG_0001.JPG")));
            Assert.False(File.Exists(Path.Combine(source, "Takeout", "Google Photos", "Tatil İstanbul", "IMG_0001.JPG")));

            var undo = await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
            Assert.True(undo.Complete, string.Join("\n", undo.Messages));
            var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(root));
            var real = diff.Differences.Where(d => !d.Path.StartsWith("Data", StringComparison.OrdinalIgnoreCase) && !d.Path.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(real.Count == 0, new PcTidy.TestbedGen.Manifest.ManifestDiff(real).Format());
        }
        finally
        {
            try { TestbedBuilder.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
