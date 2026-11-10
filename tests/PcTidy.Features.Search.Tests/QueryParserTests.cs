using PcTidy.Features.Search.Query;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>Every operator of the Everything-style subset, parsed the same whatever the PC's language.</summary>
public class QueryParserTests
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero)); // a Tuesday

    private static ParsedQuery P(string text) => QueryParser.Parse(text, Clock);

    [Fact]
    public void Blank_is_empty()
    {
        Assert.True(P("").IsEmpty);
        Assert.True(P("   ").IsEmpty);
        Assert.True(QueryParser.Parse(null).IsEmpty);
    }

    [Fact]
    public void Words_are_and_ed()
    {
        var and = Assert.IsType<AndNode>(P("tax 2025").Root);
        Assert.Equal(new[] { "tax", "2025" }, and.Items.Cast<TextNode>().Select(t => t.Text));
    }

    [Fact]
    public void Pipe_is_or_and_binds_tighter_than_space()
    {
        var and = Assert.IsType<AndNode>(P("a b|c").Root);
        Assert.IsType<TextNode>(and.Items[0]);
        var or = Assert.IsType<OrNode>(and.Items[1]);
        Assert.Equal(new[] { "b", "c" }, or.Items.Cast<TextNode>().Select(t => t.Text));
    }

    [Fact]
    public void Bang_is_not_for_words_filters_and_groups()
    {
        Assert.Equal("draft", Assert.IsType<TextNode>(Assert.IsType<NotNode>(P("!draft").Root).Item).Text);
        Assert.IsType<ExtNode>(Assert.IsType<NotNode>(P("!ext:tmp").Root).Item);
        Assert.IsType<OrNode>(Assert.IsType<NotNode>(P("!<a|b>").Root).Item);
    }

    [Fact]
    public void Angle_brackets_group()
    {
        var and = Assert.IsType<AndNode>(P("<a|b> c").Root);
        Assert.IsType<OrNode>(and.Items[0]);
        Assert.Equal("c", Assert.IsType<TextNode>(and.Items[1]).Text);
    }

    [Fact]
    public void Greater_than_inside_a_group_still_works_as_a_size_operator()
    {
        var and = Assert.IsType<AndNode>(P("<size:>1gb|ext:iso> x").Root);
        var or = Assert.IsType<OrNode>(and.Items[0]);
        var size = Assert.IsType<SizeNode>(or.Items[0]);
        Assert.Equal((1L << 30) + 1, size.Min);
        Assert.IsType<ExtNode>(or.Items[1]);
    }

    [Fact]
    public void Quotes_keep_spaces_and_switch_off_operators()
    {
        Assert.Equal("my notes", Assert.IsType<TextNode>(P("\"my notes\"").Root).Text);
        Assert.Equal("ext:jpg", Assert.IsType<TextNode>(P("\"ext:jpg\"").Root).Text);
        Assert.Equal("a|b", Assert.IsType<TextNode>(P("\"a|b\"").Root).Text);
        Assert.Equal(@"C:\My Files", Assert.IsType<PathNode>(P("path:\"C:\\My Files\"").Root).Text);
    }

    [Fact]
    public void Wildcards_and_backslash_words()
    {
        var w = Assert.IsType<TextNode>(P("*.mp4").Root);
        Assert.True(w.Wildcard);
        var p = Assert.IsType<TextNode>(P(@"photos\2024").Root);
        Assert.True(p.MatchPath);
        // A drive path is a path word, not a one-letter filter.
        Assert.True(Assert.IsType<TextNode>(P(@"C:\Users").Root).MatchPath);
    }

    [Fact]
    public void Ext_lists()
    {
        Assert.Equal(new[] { "jpg", "png" }, Assert.IsType<ExtNode>(P("ext:jpg;png").Root).Extensions);
        Assert.Equal(new[] { "jpg", "png" }, Assert.IsType<ExtNode>(P("ext:.JPG,png").Root).Extensions);
        Assert.Empty(Assert.IsType<ExtNode>(P("ext:").Root).Extensions);
    }

    [Theory]
    [InlineData("size:>1gb", 1073741825L, long.MaxValue)]
    [InlineData("size:>=1gb", 1073741824L, long.MaxValue)]
    [InlineData("size:<10kb", 0L, 10239L)]
    [InlineData("size:<=10kb", 0L, 10240L)]
    [InlineData("size:=0", 0L, 0L)]
    [InlineData("size:1.5mb", 1572864L, 1572864L)]
    [InlineData("size:10mb..1mb", 1048576L, 10485760L)]
    [InlineData("size:empty", 0L, 0L)]
    [InlineData("size:gigantic", 134217729L, long.MaxValue)]
    [InlineData("SIZE:>1GB", 1073741825L, long.MaxValue)]
    public void Sizes(string text, long min, long max)
    {
        var s = Assert.IsType<SizeNode>(P(text).Root);
        Assert.Equal(min, s.Min);
        Assert.Equal(max, s.Max);
    }

    [Fact]
    public void Bad_size_is_a_friendly_warning_not_an_error()
    {
        var q = P("size:lots report");
        Assert.Single(q.Warnings);
        Assert.Equal("report", Assert.IsType<TextNode>(q.Root).Text);
    }

    [Theory]
    [InlineData("dm:today", "2026-11-10", "2026-11-11")]
    [InlineData("dm:yesterday", "2026-11-09", "2026-11-10")]
    [InlineData("dm:thisweek", "2026-11-09", "2026-11-16")]
    [InlineData("dm:lastweek", "2026-11-02", "2026-11-09")]
    [InlineData("dm:thismonth", "2026-11-01", "2026-12-01")]
    [InlineData("dm:lastmonth", "2026-10-01", "2026-11-01")]
    [InlineData("dm:lastyear", "2025-01-01", "2026-01-01")]
    [InlineData("dm:2026-11", "2026-11-01", "2026-12-01")]
    [InlineData("dm:2026", "2026-01-01", "2027-01-01")]
    [InlineData("dm:2026-02-28", "2026-02-28", "2026-03-01")]
    [InlineData("dm:2026-01..2026-03", "2026-01-01", "2026-04-01")]
    [InlineData("dm:last7days", "2026-11-04", "2026-11-11")]
    [InlineData("datemodified:2026-11-10", "2026-11-10", "2026-11-11")]
    public void Dates(string text, string from, string to)
    {
        var d = Assert.IsType<DateNode>(P(text).Root);
        Assert.Equal(DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture), d.FromUtc);
        Assert.Equal(DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture), d.ToUtc);
    }

    [Fact]
    public void Date_comparisons()
    {
        var after = Assert.IsType<DateNode>(P("dm:>2026-01-01").Root);
        Assert.Equal(new DateTime(2026, 1, 2), after.FromUtc);
        Assert.Equal(DateTime.MaxValue, after.ToUtc);
        var before = Assert.IsType<DateNode>(P("dm:<2026-01-01").Root);
        Assert.Equal(new DateTime(2026, 1, 1), before.ToUtc);
        Assert.Equal(DateTime.MinValue, before.FromUtc);
    }

    [Fact]
    public void Dates_follow_the_local_calendar_day()
    {
        // 10 Nov 2026 01:00 UTC is still 9 Nov in New York (UTC-5): "today" is the 9th there.
        var ny = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero), ny);
        var d = Assert.IsType<DateNode>(QueryParser.Parse("dm:today", clock).Root);
        Assert.Equal(new DateTime(2026, 11, 9, 5, 0, 0), d.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 10, 5, 0, 0), d.ToUtc);
    }

    [Fact]
    public void Path_folder_file_dupe()
    {
        Assert.Equal("projects", Assert.IsType<PathNode>(P("path:projects").Root).Text);
        Assert.True(Assert.IsType<PathNode>(P("path:*\\2024\\*").Root).Wildcard);
        var folder = Assert.IsType<KindNode>(P("folder:").Root);
        Assert.True(folder.Folders);
        Assert.Null(folder.Inner);
        var file = Assert.IsType<KindNode>(P("file:report").Root);
        Assert.False(file.Folders);
        Assert.Equal("report", Assert.IsType<TextNode>(file.Inner).Text);
        var dupe = P("dupe:");
        Assert.True(dupe.Dupe);
        Assert.IsType<MatchAllNode>(dupe.Root);
        var dupeExt = P("dupe: ext:jpg");
        Assert.True(dupeExt.Dupe);
        Assert.IsType<ExtNode>(dupeExt.Root);
    }

    [Fact]
    public void Unknown_filter_is_searched_as_text_with_a_note()
    {
        var q = P("color:red");
        Assert.Equal("color:red", Assert.IsType<TextNode>(q.Root).Text);
        Assert.Single(q.Warnings);
    }

    [Fact]
    public void Stray_operators_never_throw()
    {
        foreach (var text in new[] { "|", "||a", "a|", "!", "!!a", "<", ">", "<<a", "a>", "\"", "\"unclosed", "size:", "dm:", ":", "a:b:c", "<|>", "!<>" })
        {
            var q = P(text);
            Assert.NotNull(q);
        }
        Assert.Equal("a", Assert.IsType<TextNode>(Assert.IsType<NotNode>(Assert.IsType<NotNode>(P("!!a").Root).Item).Item).Text);
    }

    [Fact]
    public void Turkish_culture_parses_the_same()
    {
        using var _ = new CultureScope("tr-TR");
        // In Turkish "I".ToLower() is "ı" and "i".ToUpper() is "İ": filter names must still be recognised.
        Assert.IsType<KindNode>(P("FILE:report").Root);
        Assert.IsType<KindNode>(P("FOLDER:").Root);
        Assert.Equal(new[] { "ini" }, Assert.IsType<ExtNode>(P("EXT:INI").Root).Extensions);
        Assert.IsType<SizeNode>(P("SIZE:>1.5GB").Root);
        Assert.Equal(1572864L, Assert.IsType<SizeNode>(P("size:1.5mb").Root).Min); // decimal point, not comma
        Assert.IsType<DateNode>(P("DM:LASTWEEK").Root);
        Assert.IsType<PathNode>(P("PATH:İstanbul").Root);
        Assert.True(P("DUPE:").Dupe);
    }
}
