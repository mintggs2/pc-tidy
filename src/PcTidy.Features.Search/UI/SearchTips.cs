namespace PcTidy.Features.Search.UI;

/// <summary>One line of the syntax help.</summary>
public sealed record SearchTip(string Example, string Meaning);

/// <summary>The syntax help shown under the search box (same words Everything uses).</summary>
public static class SearchTips
{
    public static readonly IReadOnlyList<SearchTip> All = new SearchTip[]
    {
        new("tax 2025", "Both words in the name"),
        new("jpg|png", "Either word"),
        new("!draft", "Not containing \"draft\""),
        new("\"my notes\"", "Exact text, spaces included"),
        new("*.mp4  IMG_20??*", "Wildcards: whole name"),
        new("ext:jpg;png", "These file types"),
        new("size:>1gb", "Bigger than 1 GB (also <, 10mb..50mb)"),
        new("dm:today", "Changed today (yesterday, lastweek, lastmonth)"),
        new("dm:2026-11", "Changed in November 2026 (or 2026, 2026-11-10)"),
        new("path:projects", "Anywhere in the folder path"),
        new("folder:  file:", "Only folders / only files"),
        new("dupe:", "Files with the same name and size"),
        new("<a|b> c", "Grouping with < >"),
    };
}
