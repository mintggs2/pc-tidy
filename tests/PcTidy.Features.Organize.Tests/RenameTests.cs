using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rename;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

public class RenameTests
{
    private static readonly RenameOptions Fix = new() { FixMessyNames = true };
    private static string Fixed(string name) => RenameHelper.NewName(name, DateTime.Today, null, Fix, null);

    [Theory]
    [InlineData("report (1).pdf", "report.pdf")]
    [InlineData("report (12) (3).pdf", "report.pdf")]
    [InlineData("photo - Copy.jpg", "photo.jpg")]
    [InlineData("photo - Copy (2).jpg", "photo.jpg")]
    [InlineData("Copy of notes.txt", "notes.txt")]
    [InlineData("Copy (2) of notes.txt", "notes.txt")]
    [InlineData("Game - Shortcut.lnk", "Game.lnk")]
    [InlineData("my_holiday_photo.JPG", "my holiday photo.jpg")]
    [InlineData("v1.2.final.docx", "v1 2 final.docx")]
    [InlineData("too    many   spaces .txt", "too many spaces.txt")]
    [InlineData("trailing dots... .txt", "trailing dots.txt")]
    [InlineData("  leading.txt", "leading.txt")]
    [InlineData("bad<name>|here?.txt", "badnamehere.txt")]
    [InlineData("backup.TAR.GZ", "backup.tar.gz")]
    [InlineData("CON.txt", "CON file.txt")]
    [InlineData("nul.tar.gz", "nul file.tar.gz")]
    [InlineData("com1", "com1 file")]
    [InlineData("LPT9.log", "LPT9 file.log")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("___.txt", "Untitled.txt")]
    [InlineData("Ünïcödé_ファイル.PNG", "Ünïcödé ファイル.png")]
    public void Fix_messy_names(string before, string after) => Assert.Equal(after, Fixed(before));

    [Theory]
    [InlineData("report (1).pdf")]
    [InlineData("my_file - Copy.TXT")]
    [InlineData("CON.txt")]
    public void Fixing_twice_changes_nothing_more(string name) => Assert.Equal(Fixed(name), Fixed(Fixed(name)));

    [Fact]
    public void Reserved_names_are_recognised()
    {
        foreach (var n in new[] { "CON", "con.txt", "Nul .tar.gz", "COM1.log", "lpt3", "AUX.x", "PRN" }) Assert.True(WindowsNames.IsReserved(n), n);
        foreach (var n in new[] { "CONSOLE.txt", "COM10.txt", "nully", "my con.txt" }) Assert.False(WindowsNames.IsReserved(n), n);
    }

    private static List<RenameSource> Sources(string folder, params string[] names)
        => names.Select(n => new RenameSource(Path.Combine(folder, n), new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc))).ToList();

    [Fact]
    public void Collisions_inside_the_batch_and_with_existing_files_are_blocked()
    {
        var folder = @"C:\fake";
        var files = Sources(folder, "a (1).txt", "a (2).txt", "b (1).txt", "b.txt", "c (1).txt");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(folder, "c.txt") };
        foreach (var f in files) existing.Add(f.Path);

        var rows = RenameHelper.Preview(files, Fix, existing.Contains);

        Assert.All(rows.Where(r => r.OldName.StartsWith("a")), r => Assert.Contains("same name", r.Problem));
        Assert.Contains("already there", rows.Single(r => r.OldName == "b (1).txt").Problem); // b.txt isn't moving
        Assert.False(rows.Single(r => r.OldName == "b.txt").Changed);
        Assert.Contains("already there", rows.Single(r => r.OldName == "c (1).txt").Problem);
        Assert.Empty(RenameHelper.ToPlanItems(rows));
    }

    [Fact]
    public void Case_only_renames_are_allowed_and_swaps_go_through_a_temporary_name()
    {
        var folder = @"C:\fake";
        var caseOnly = RenameHelper.Preview(Sources(folder, "Photo.JPG"), new RenameOptions { LowercaseExtension = true }, _ => true);
        var row = Assert.Single(caseOnly);
        Assert.True(row.IsCaseOnly);
        Assert.True(row.CanRename);
        var item = Assert.Single(RenameHelper.ToPlanItems(caseOnly));
        Assert.Equal(ActionKind.RenamePath, item.Kind);
        Assert.Equal(Path.Combine(folder, "Photo.jpg"), item.DestinationPath);

        // Swap: "one" ↔ "two" via find/replace with a regex.
        var swap = RenameHelper.Preview(Sources(folder, "one.txt", "two.txt"),
            new RenameOptions { Find = "^(one|two)$", Replace = "x", UseRegex = true }, _ => true);
        Assert.All(swap, r => Assert.Contains("same name", r.Problem)); // both → x.txt
        var realSwap = new[]
        {
            new RenameRow(Path.Combine(folder, "one.txt"), "one.txt", "two.txt", null, null),
            new RenameRow(Path.Combine(folder, "two.txt"), "two.txt", "one.txt", null, null),
        };
        var items = RenameHelper.ToPlanItems(realSwap);
        Assert.Equal(4, items.Count);
        Assert.All(items.Take(2), i => Assert.Contains("~pctidy-", i.DestinationPath));
        Assert.All(items.Skip(2), i => Assert.Contains("~pctidy-", i.SourcePath));
        Assert.All(items, i => Assert.Equal(NameClashPolicy.Skip, i.ClashPolicy));
    }

    [Fact]
    public void Find_replace_case_numbering_and_date_tokens()
    {
        var files = Sources(@"C:\fake", "IMG_001.jpg", "IMG_002.jpg");
        var rows = RenameHelper.Preview(files, new RenameOptions
        {
            Find = "IMG_", Replace = "Trip {year} ", Case = NameCase.Upper, AddNumber = true, NumberStart = 7, NumberDigits = 3, NumberPosition = NumberPosition.Prefix,
        }, _ => false);
        var year = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).ToLocalTime().Year;
        Assert.Equal($"007 TRIP {year} 001.jpg", rows[0].NewName);
        Assert.Equal($"008 TRIP {year} 002.jpg", rows[1].NewName);

        var plain = RenameHelper.Preview(Sources(@"C:\fake", "a.b.txt"), new RenameOptions { Find = ".", Replace = "$" }, _ => false);
        Assert.Equal("a$b.txt", plain[0].NewName); // plain mode: '.' and '$' are literal

        var title = RenameHelper.Preview(Sources(@"C:\fake", "hello WORLD.txt"), new RenameOptions { Case = NameCase.Title }, _ => false);
        Assert.Equal("Hello World.txt", title[0].NewName);
    }

    [Fact]
    public void Reserved_or_illegal_results_and_long_paths_are_flagged()
    {
        var rows = RenameHelper.Preview(Sources(@"C:\fake", "x.txt", "y.txt"), new RenameOptions { Find = "^x$", Replace = "CON", UseRegex = true }, _ => false);
        Assert.Contains("reserves", rows[0].Problem);
        Assert.False(rows[1].Changed);

        var deep = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 50), 5));
        var longRows = RenameHelper.Preview(Sources(deep, "file (1).txt"), Fix, _ => false);
        Assert.Null(longRows[0].Problem);
        Assert.Contains("260", longRows[0].Warning);
    }

    [Fact]
    public void Regex_timeouts_and_bad_patterns_never_crash_the_preview()
    {
        var name = new string('a', 40) + "!.txt";
        var rows = RenameHelper.Preview(Sources(@"C:\fake", name), new RenameOptions { Find = "^(a+)+$", UseRegex = true, IncludeExtension = true, RegexTimeout = TimeSpan.FromMilliseconds(30) }, _ => false);
        Assert.Contains("too long", rows[0].Problem);

        var bad = RenameHelper.Preview(Sources(@"C:\fake", "a.txt"), new RenameOptions { Find = "([", UseRegex = true }, _ => false);
        Assert.Contains("isn't valid", bad[0].Problem);
    }

    [Fact]
    public void Real_files_get_size_and_time_on_their_plan_items()
    {
        using var sb = new Sandbox();
        var path = sb.File("messy_name (1).txt", "hello");
        var rows = RenameHelper.Preview(new[] { new RenameSource(path, System.IO.File.GetLastWriteTimeUtc(path)) }, Fix);
        var item = Assert.Single(RenameHelper.ToPlanItems(rows));
        Assert.Equal(Path.Combine(sb.Downloads, "messy name.txt"), item.DestinationPath);
        Assert.Equal(5, item.ExpectedSize);
        Assert.NotNull(item.ExpectedLastWriteUtc);
        Assert.Equal(OrganizeModule.FeatureId, item.FeatureId);
    }
}
