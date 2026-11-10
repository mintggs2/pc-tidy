using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>Matching every operator against a small known index (in memory).</summary>
public class QueryEngineTests
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero));
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private const long MB = 1024 * 1024;

    private static readonly IndexSnapshot Index = new(new[]
    {
        Synthetic.Tree(@"X:\Users\tester",
            (@"Pictures\Holiday 2025\IMG_0001.jpg", 3 * MB, Now.AddDays(-200)),
            (@"Pictures\Holiday 2025\IMG_0002.JPG", 4 * MB, Now.AddDays(-200)),
            (@"Pictures\Holiday 2025\notes.txt", 100, Now),
            (@"Pictures\Screenshots\", 0, Now),
            (@"Documents\Taxes\tax 2025.pdf", 2 * MB, Now.AddDays(-8)),
            (@"Documents\Taxes\tax 2024.pdf", 2 * MB, Now.AddDays(-400)),
            (@"Documents\Projects\report draft.docx", 50_000, Now.AddDays(-1)),
            (@"Documents\Projects\report final.docx", 60_000, Now.AddDays(-1)),
            (@"Downloads\Win11.iso", 5L * 1024 * MB, Now.AddDays(-30)),
            (@"Downloads\setup.exe", 80 * MB, Now.AddDays(-40)),
            (@"Downloads\IMG_0001.jpg", 3 * MB, Now.AddDays(-5)),
            (@"Downloads\README", 10, Now.AddDays(-5))),
        Synthetic.Tree(@"Y:\",
            (@"Backup\Pictures\IMG_0001.jpg", 3 * MB, Now.AddDays(-100)),
            (@"Backup\Pictures\IMG_0002.jpg", 999, Now.AddDays(-100)),
            (@"Music\song.mp3", 5 * MB, Now.AddDays(-700))),
    }, 1);

    private static List<SearchHit> Find(string query)
    {
        var r = QueryEngine.Search(Index, QueryParser.Parse(query, Clock));
        return Enumerable.Range(0, r.Count).Select(i => r[i]).ToList();
    }

    private static List<string> Names(string query) => Find(query).Select(h => h.Name).ToList();

    [Fact]
    public void Words_match_names_ignoring_case_and_are_anded()
    {
        Assert.Equal(new[] { "tax 2024.pdf", "tax 2025.pdf", "Taxes" }, Names("TAX"));
        Assert.Equal(new[] { "tax 2025.pdf" }, Names("tax 2025"));
    }

    [Fact]
    public void Results_are_sorted_by_name_across_roots()
    {
        var names = Names("img");
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        Assert.Equal(5, names.Count);
    }

    [Fact]
    public void Or_not_and_groups()
    {
        Assert.Equal(new[] { "setup.exe", "Win11.iso" }, Names("ext:iso|ext:exe"));
        Assert.Equal(new[] { "report final.docx" }, Names("report !draft"));
        Assert.Equal(new[] { "report draft.docx", "tax 2025.pdf" }, Names("<draft|tax> !2024 file:"));
    }

    [Fact]
    public void Quotes_match_text_with_spaces()
    {
        Assert.Equal(new[] { "report draft.docx" }, Names("\"report draft\""));
        Assert.Empty(Names("\"draft report\""));
    }

    [Fact]
    public void Wildcards_match_the_whole_name()
    {
        Assert.Equal(new[] { "IMG_0002.JPG", "IMG_0002.jpg" }, Names("img_000?.jpg").Where(n => n.Contains("0002")).ToList());
        Assert.Equal(new[] { "README" }, Names("read*"));
        Assert.Empty(Names("ead*"));
    }

    [Fact]
    public void Ext_matches_files_case_insensitively_and_empty_means_no_extension()
    {
        Assert.Equal(5, Names("ext:jpg").Count);
        Assert.Equal(new[] { "README" }, Names("ext:"));
        Assert.Equal(7, Names("ext:jpg;pdf").Count);
    }

    [Fact]
    public void Size_filters_skip_folders()
    {
        Assert.Equal(new[] { "Win11.iso" }, Names("size:>1gb"));
        Assert.Equal(new[] { "IMG_0002.jpg", "notes.txt", "README" }, Names("size:<1kb"));
        Assert.Equal(new[] { "setup.exe", "Win11.iso" }, Names("size:huge|size:gigantic"));
    }

    [Fact]
    public void Date_filters()
    {
        Assert.Contains("notes.txt", Names("dm:today"));
        Assert.Equal(new[] { "report draft.docx", "report final.docx" }, Names("dm:yesterday"));
        Assert.Equal(new[] { "tax 2025.pdf" }, Names("dm:lastweek ext:pdf"));
        Assert.Equal(new[] { "tax 2024.pdf" }, Names("dm:2025-10 ext:pdf"));
    }

    [Fact]
    public void Path_matches_folder_names_and_full_paths()
    {
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_0002.jpg" }, Names("path:backup ext:jpg"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_0002.JPG" }, Names(@"pictures\holiday ext:jpg"));
        Assert.Equal(new[] { "song.mp3" }, Names(@"path:y:\music\ file:"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_0002.jpg" }, Names(@"path:Y:\*\IMG_*"));
    }

    [Fact]
    public void Folder_and_file_kinds()
    {
        Assert.Contains("Screenshots", Names("folder:"));
        Assert.DoesNotContain("notes.txt", Names("folder:"));
        Assert.Equal(new[] { "Pictures", "Pictures" }, Names("folder:pictures"));
        Assert.DoesNotContain("Pictures", Names("file:pic"));
        Assert.All(Find("file:"), h => Assert.False(h.IsFolder));
    }

    [Fact]
    public void Dupe_keeps_files_with_the_same_name_and_size()
    {
        var hits = Find("dupe:");
        // IMG_0001.jpg (3 MB) is in Pictures, Downloads and the backup: all three. IMG_0002 differs in size: none.
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal("IMG_0001.jpg", h.Name));
        Assert.Equal(3, Find("dupe: ext:jpg").Count);
        Assert.Empty(Find("dupe: path:downloads"));
    }

    [Fact]
    public void Hits_carry_folder_size_and_date()
    {
        var iso = Assert.Single(Find("win11"));
        Assert.Equal(@"X:\Users\tester\Downloads", iso.Folder);
        Assert.Equal(@"X:\Users\tester\Downloads\Win11.iso", iso.FullPath);
        Assert.Equal(5L * 1024 * MB, iso.Size);
        Assert.Equal(Now.AddDays(-30), iso.ModifiedUtc);
        var song = Assert.Single(Find("song"));
        Assert.Equal(@"Y:\Music", song.Folder); // a drive root joins without a double backslash
    }

    [Fact]
    public void Sorting_by_size_date_and_folder()
    {
        var r = QueryEngine.Search(Index, QueryParser.Parse("ext:pdf|ext:iso|ext:exe", Clock));
        var bySize = r.SortedBy(SearchSort.Size, descending: true);
        Assert.Equal("Win11.iso", bySize[0].Name);
        var byDate = r.SortedBy(SearchSort.Modified, descending: false);
        Assert.Equal("tax 2024.pdf", byDate[0].Name);
        var byFolder = r.SortedBy(SearchSort.Folder, false);
        Assert.StartsWith(@"X:\Users\tester\Documents", byFolder[0].Folder);
        Assert.Equal(r.Count, byFolder.SortedBy(SearchSort.Name, false).Count);
        Assert.Equal("setup.exe", byFolder.SortedBy(SearchSort.Name, false)[0].Name);
    }

    [Fact]
    public void Truncation_is_reported()
    {
        var r = QueryEngine.Search(Index, QueryParser.Parse("file:", Clock), maxResults: 3);
        Assert.Equal(3, r.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Turkish_culture_matches_the_same()
    {
        using var _ = new CultureScope("tr-TR");
        // "IMG" vs "img": with Turkish casing rules these would differ; ordinal-ignore-case keeps them equal.
        Assert.Equal(5, Names("img ext:JPG").Count);
        Assert.Equal(new[] { "README" }, Names("readme"));
        Assert.Equal(new[] { "IMG_0001.jpg", "IMG_0002.JPG" }, Names(@"PICTURES\HOLIDAY EXT:JPG"));
    }
}
