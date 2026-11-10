using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Tests;

public class BigAndOldTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private const long MB = 1024 * 1024;

    [Theory]
    [InlineData(501, 12, 12, true)]    // big, old, not opened
    [InlineData(500, 12, 12, false)]   // exactly 500 MB is not "over"
    [InlineData(2000, 5, 12, false)]   // changed 5 months ago
    [InlineData(2000, 7, 1, false)]    // opened last month: last-access may only exclude
    [InlineData(2000, 7, 7, true)]
    public void Rule(long sizeMb, int monthsSinceTouched, int monthsSinceAccess, bool expected)
    {
        var rule = BigAndOldRule.Default;
        Assert.Equal(expected, rule.Matches(sizeMb * MB, Now.AddMonths(-monthsSinceTouched), Now.AddMonths(-monthsSinceAccess), Now));
    }

    [Fact]
    public void Last_access_never_includes_a_recently_changed_file()
    {
        // Access far in the past (Windows often doesn't update it) can't make a recently changed file "old".
        Assert.False(BigAndOldRule.Default.Matches(900 * MB, Now.AddDays(-3), Now.AddYears(-5), Now));
    }

    [Fact]
    public void Query_skips_protected_cloud_placeholder_askfirst_and_project_files()
    {
        var old = Now.AddYears(-2);
        var t = new FakeTree(@"X:\BO");
        t.File(@"Videos\wedding.mov", 4000 * MB, old);
        t.File(@"Downloads\win.iso", 700 * MB, old);
        t.File(@"Downloads\recent.iso", 700 * MB, Now.AddDays(-10));
        t.File(@"Downloads\created-recently.iso", 700 * MB, old, created: Now.AddDays(-10));
        t.File(@"Windows\big.wim", 3000 * MB, old);
        t.File(@"OneDrive\online.mp4", 900 * MB, old, attrs: FileAttributes.ReparsePoint | (FileAttributes)0x00400000);
        t.File(@"Photos\raw.dng", 900 * MB, old);
        t.File(@"code\repo\vm.vhdx", 900 * MB, old);
        var safety = new FakeSafety();
        safety.Never.Add(@"X:\BO\Windows");
        safety.AskFirst.Add(@"X:\BO\Photos");
        safety.Projects.Add(@"X:\BO\code\repo");
        var ix = new SpaceIndexScanner(t.Walker, safety, new FakeTags(), new FakeDrives())
            .Scan(t.Root, new SpaceScanOptions { IncludeUnscannedNode = false }, null, CancellationToken.None);

        var found = SpaceQueries.BigAndOld(ix, SpaceIndex.RootNode, Now, safety).Select(ix.Name).ToArray();
        Assert.Equal(new[] { "wedding.mov", "win.iso" }, found); // biggest first
    }
}
