using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Index;
using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.UI;
using Wpf.Ui.Controls;
using Xunit;

namespace PcTidy.Features.Search.Tests;

public class ModuleTests
{
    [Fact]
    public void Module_metadata()
    {
        var m = new SearchModule();
        Assert.Equal("search", m.Id);
        Assert.Equal("Search", m.Title);
        Assert.Equal(NavGroup.Organize, m.Group);
        Assert.Equal("Search24", m.Icon);
        Assert.Equal(Release.V3, m.Release);
        Assert.True(Enum.IsDefined(typeof(SymbolRegular), Enum.Parse<SymbolRegular>(m.Icon)));
        var scanner = Assert.IsType<SearchScanner>(Assert.Single(m.Scanners));
        Assert.False(scanner.IncludedInOptimizeAll);
        Assert.Equal("search", scanner.FeatureId);
        Assert.Equal(Release.V3, scanner.Release);
        Assert.False(string.IsNullOrWhiteSpace(m.Summary));
    }

    [Fact]
    public void Every_icon_named_in_the_XAML_exists()
    {
        var ui = Path.Combine(SearchSandbox.FindCodeRoot(), "src", "PcTidy.Features.Search", "UI");
        var names = Directory.EnumerateFiles(ui, "*.xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:SymbolIcon |Icon=""|ActionIcon="")(\w+?)(?:\}|"")").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True(Enum.TryParse<SymbolRegular>(n, out _), $"Unknown icon {n}"));
    }

    [Fact]
    public void Virtual_list_builds_only_requested_rows()
    {
        var seg = Synthetic.Generate(@"X:\", 50_000);
        var results = QueryEngine.Search(new IndexSnapshot(new[] { seg }, 1), QueryParser.Parse("file:"));
        var list = new VirtualHitList(results);
        Assert.Equal(results.Count, list.Count);
        var row = list.Row(1234);
        Assert.Equal(results[1234].Name, row.Name);
        Assert.Equal(1234, list.IndexOf(row));
        Assert.Equal(-1, list.IndexOf("nope"));
        Assert.Same(row, list[1234]);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(row));
    }

    [Fact]
    public void Tips_cover_every_filter()
    {
        var text = string.Join(" ", SearchTips.All.Select(t => t.Example));
        foreach (var f in new[] { "ext:", "size:", "dm:", "path:", "folder:", "file:", "dupe:", "|", "!", "\"", "*", "<" })
            Assert.Contains(f, text);
    }
}
