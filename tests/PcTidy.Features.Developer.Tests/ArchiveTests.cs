using System.IO.Compression;
using System.Text;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Archive;
using PcTidy.Features.Developer.Common;
using Xunit;

namespace PcTidy.Features.Developer.Tests;

/// <summary>Archive naming, the note inside each zip, which projects are suggested, and the "Archived projects" list.</summary>
public class ArchiveTests
{
    [Fact]
    public void Zip_names_follow_the_pattern_and_parse_back()
    {
        var name = ArchiveNaming.ZipName("WeatherApp", new DateTime(2026, 3, 7));
        Assert.Equal("WeatherApp (archived 2026-03-07).zip", name);
        Assert.Equal(("WeatherApp", new DateTime(2026, 3, 7)), ArchiveNaming.Parse(name));
        Assert.Equal(("my app (v2)", new DateTime(2025, 12, 31)), ArchiveNaming.Parse("my app (v2) (archived 2025-12-31) (2).zip"));
        Assert.Null(ArchiveNaming.Parse("WeatherApp.zip"));
        Assert.Null(ArchiveNaming.Parse("WeatherApp (archived 2026-13-40).zip"));
        Assert.Null(ArchiveNaming.Parse("WeatherApp (archived 2026-03-07).7z"));
        Assert.Equal(Path.Combine(@"D:\Archive", name), ArchiveNaming.ZipPath(@"D:\Archive", "WeatherApp", new DateTime(2026, 3, 7)));
    }

    [Fact]
    public void Manifest_round_trips_and_rejects_strangers()
    {
        var m = new ArchiveManifest { OriginalPath = @"C:\code\app", FolderName = "app", ArchivedUtc = DevSandbox.Anchor, SizeBytes = 12, FileCount = 3 };
        var back = ArchiveManifest.FromJson(m.ToJson());
        Assert.Equal(m, back);
        Assert.Null(ArchiveManifest.FromJson("{\"format\":\"other/1\",\"originalPath\":\"C:\\\\a\",\"folderName\":\"a\"}"));
        Assert.Null(ArchiveManifest.FromJson("{\"format\":\"pctidy-archive/1\",\"originalPath\":\"relative\",\"folderName\":\"a\"}"));
        Assert.Null(ArchiveManifest.FromJson("{\"format\":\"pctidy-archive/1\",\"originalPath\":\"C:\\\\a\",\"folderName\":null}"));
        Assert.Null(ArchiveManifest.FromJson("broken"));
    }

    [Fact]
    public async Task Old_projects_are_suggested_with_an_ArchiveToZip_that_carries_the_note()
    {
        using var s = new DevSandbox("archive-old");
        var old = s.Project("old-site", gitAgeDays: 400);
        s.Put(Path.Combine(old, "package.json"), "{}", s.Now.AddDays(-400));
        s.Put(Path.Combine(old, "node_modules", "a.js"), new byte[100], s.Now.AddDays(-400));
        var recent = s.Project("current", gitAgeDays: 20);
        var archive = Path.Combine(s.Root, "D", "Archive");
        s.ExtraOptions[DeveloperOptions.ArchiveFolder] = archive;

        var r = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var group = Assert.Single(r.Groups, g => g.Id == ArchiveScanner.OldGroupId);
        var item = Assert.Single(group.Items);
        Assert.Equal("old-site", item.Title);
        Assert.Equal(Badge.Check, item.Badge);
        Assert.False(item.DefaultSelected);
        Assert.Contains("build folders", item.Reason); // the tip about cleaning node_modules first
        var zip = Assert.Single(item.Actions);
        Assert.Equal(ActionKind.ArchiveToZip, zip.Kind);
        Assert.Equal(old, zip.SourcePath, ignoreCase: true);
        Assert.Equal(ArchiveNaming.ZipPath(archive, "old-site", s.Now.ToLocalTime()), zip.DestinationPath);
        Assert.Equal(item.SizeBytes, zip.ExpectedSize);
        var note = ArchiveManifest.FromJson(zip.Args![ArchiveNaming.ManifestArg])!;
        Assert.Equal(old, note.OriginalPath, ignoreCase: true);
        Assert.Equal("old-site", note.FolderName);
        Assert.DoesNotContain(group.Items, i => i.Title == "current");

        s.ExtraOptions[DeveloperOptions.ArchiveMonths] = 24; // 400 days < 24 months
        r = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(r.Groups, g => g.Id == ArchiveScanner.OldGroupId);
        Assert.Contains(r.Notes, n => n.Contains("24 months"));
    }

    [Fact]
    public async Task Archived_list_reads_the_note_and_offers_restore_to_the_original_place()
    {
        using var s = new DevSandbox("archive-list");
        var archive = Path.Combine(s.Root, "D", "Archive");
        Directory.CreateDirectory(archive);
        var original = Path.Combine(s.Repos, "gone");
        MakeZip(Path.Combine(archive, "gone (archived 2025-01-02).zip"), "gone",
            new ArchiveManifest { OriginalPath = original, FolderName = "gone", ArchivedUtc = DevSandbox.Anchor, SizeBytes = 5, FileCount = 1 }.ToJson());
        MakeZip(Path.Combine(archive, "nonote (archived 2025-01-02).zip"), "nonote", null);
        MakeZip(Path.Combine(archive, "holiday photos.zip"), "photos", null); // not one of PC Tidy's
        var here = Path.Combine(s.Repos, "back");
        Directory.CreateDirectory(here);
        MakeZip(Path.Combine(archive, "back (archived 2025-01-03).zip"), "back",
            new ArchiveManifest { OriginalPath = here, FolderName = "back", ArchivedUtc = DevSandbox.Anchor }.ToJson());
        s.ExtraOptions[DeveloperOptions.ArchiveFolder] = archive;

        var r = await new ArchiveScanner().ScanAsync(s.Context(), null, CancellationToken.None);
        var list = Assert.Single(r.Groups, g => g.Id == ArchiveScanner.ArchivedGroupId).Items;
        Assert.Equal(3, list.Count);

        var gone = Assert.Single(list, i => i.Title == "gone");
        var extract = Assert.Single(gone.Actions);
        Assert.Equal(ActionKind.ExtractZip, extract.Kind);
        Assert.Equal(original, extract.DestinationPath);
        Assert.Equal("gone", extract.Args![ArchiveNaming.UnwrapRootArg]);
        Assert.False(gone.DefaultSelected);

        Assert.Contains("no PC Tidy note", Assert.Single(list, i => i.Title == "nonote").DisabledReason);
        Assert.Contains("already at", Assert.Single(list, i => i.Title == "back").DisabledReason);
    }

    internal static void MakeZip(string path, string root, string? manifestJson)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry(root + "/readme.txt").Open())) w.Write("hello");
        if (manifestJson is not null)
            using (var w = new StreamWriter(zip.CreateEntry(ArchiveNaming.ManifestEntryName).Open(), new UTF8Encoding(false))) w.Write(manifestJson);
    }
}
