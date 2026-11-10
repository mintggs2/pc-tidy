using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Features.Cleanup.Tests;

public sealed class HiddenFilesScannerTests
{
    /// <summary>Scaled-down "GB" (1 MB) so the fake system files stay tiny on disk.</summary>
    private const long Gb = 1024L * 1024;

    private static FakePc PcWithSystemFiles(string state, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var pc = FakePc.Create(test: test);
        pc.File(Path.Combine(pc.K.SystemDrive, "hiberfil.sys"), 26 * Gb, FakePc.Recent, FileAttributes.Hidden);
        pc.File(Path.Combine(pc.K.SystemDrive, "pagefile.sys"), 4 * Gb, FakePc.Recent, FileAttributes.Hidden);
        pc.State(state);
        return pc;
    }

    [Fact]
    public async Task Full_hibernation_offers_reduced_and_off_unticked_with_admin()
    {
        using var pc = PcWithSystemFiles("HibernateEnabled=1\nHiberFileType=2\nTotalMemoryBytes=" + 64 * Gb + "\nHasBattery=0");
        var r = await pc.HiddenScan();
        var g = r.Groups.Single(x => x.Id == HiddenFilesScanner.HibernationGroupId);

        var info = g.Items.Single(i => i.Actions.Count == 0);
        Assert.Equal(26 * Gb, info.SizeBytes);
        Assert.Equal("Full", info.CurrentState);

        var reduced = g.Items.Single(i => i.Actions.Any(a => a.Args!["type"] == "reduced"));
        var off = g.Items.Single(i => i.Actions.Any(a => a.Args!["type"] == "off"));
        foreach (var item in new[] { reduced, off })
        {
            Assert.False(item.DefaultSelected);
            Assert.Equal(Badge.Check, item.Badge);
            Assert.True(item.NeedsAdmin);
            var a = item.Actions.Single();
            Assert.Equal(ActionKind.RunSystemTool, a.Kind);
            Assert.Equal(ToolIds.HibernateType, a.ToolId);
            Assert.Equal("full", a.Args!["undoType"]);
            Assert.True(a.NeedsAdmin);
            Assert.Equal("cleanup.hidden", a.FeatureId);
        }
        Assert.Equal(26 * Gb - 64 * Gb / 5, reduced.SizeBytes);
        Assert.Equal(26 * Gb, off.SizeBytes);
    }

    [Fact]
    public async Task Reduced_hibernation_on_a_laptop_only_offers_off_with_a_battery_warning()
    {
        using var pc = PcWithSystemFiles("HibernateEnabled=1\nHiberFileType=1\nHasBattery=1");
        var g = (await pc.HiddenScan()).Groups.Single(x => x.Id == HiddenFilesScanner.HibernationGroupId);
        var change = g.Items.Single(i => i.Actions.Count > 0);
        Assert.Equal("off", change.Actions.Single().Args!["type"]);
        Assert.Equal("reduced", change.Actions.Single().Args!["undoType"]);
        Assert.Contains("battery", change.Reason);
    }

    [Fact]
    public async Task Hibernation_off_shows_no_group_and_reports_are_action_free()
    {
        using var pc = FakePc.Create();
        pc.File(Path.Combine(pc.K.SystemDrive, "pagefile.sys"), 4 * Gb, FakePc.Recent, FileAttributes.Hidden);
        pc.State("HibernateEnabled=0\nPagingFiles=?:\\pagefile.sys\nReservedStorage=1\nCompactOS=0");
        var r = await pc.HiddenScan();

        Assert.DoesNotContain(r.Groups, g => g.Id == HiddenFilesScanner.HibernationGroupId);
        var reports = r.Groups.Where(g => g.Id != HiddenFilesScanner.HibernationGroupId).SelectMany(g => g.Items).ToList();
        Assert.All(reports, i => Assert.Empty(i.Actions));
        var page = r.Groups.Single(g => g.Id == HiddenFilesScanner.PagefileGroupId).Items.Single();
        Assert.Equal(4 * Gb, page.SizeBytes);
        Assert.StartsWith("Managed by Windows", page.CurrentState);
        var reserved = r.Groups.Single(g => g.Id == HiddenFilesScanner.ReservedGroupId).Items;
        Assert.Equal("On", reserved.Single(i => i.Title == "Reserved storage").CurrentState);
        Assert.Equal("Off", reserved.Single(i => i.Title == "CompactOS").CurrentState);
    }

    [Fact]
    public async Task Restore_storage_needs_admin_when_it_cannot_be_read_and_search_index_is_measured()
    {
        using var pc = FakePc.Create();
        pc.File(Path.Combine(pc.K.ProgramData, "Microsoft", "Search", "Data", "Applications", "Windows", "Windows.db"), 300_000, FakePc.Recent);
        var r = await pc.HiddenScan();

        var restore = r.Groups.Single(g => g.Id == HiddenFilesScanner.RestoreGroupId).Items.Single();
        Assert.Equal("Measured with admin", restore.CurrentState);
        Assert.True(restore.NeedsAdmin);
        var index = r.Groups.Single(g => g.Id == HiddenFilesScanner.SearchIndexGroupId).Items.Single();
        Assert.Equal(300_000, index.SizeBytes);
        Assert.Empty(index.Actions);
    }

    [Fact]
    public async Task Minimal_pc_hidden_scan_has_no_errors()
    {
        using var pc = FakePc.Create(SandboxProfile.Minimal);
        var r = await pc.HiddenScan();
        Assert.DoesNotContain(r.Notes, n => n.Contains("couldn't be checked"));
        // No developer cross-references in what the user reads.
        Assert.DoesNotContain(r.Notes, n => n.Contains("main Clean list"));
        Assert.All(r.AllItems.SelectMany(i => i.Actions), a => Assert.True(a.NeedsAdmin));
        Assert.All(r.AllItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }

    [Fact]
    public void Scanner_identity_matches_the_plan()
    {
        var s = new HiddenFilesScanner();
        Assert.Equal("cleanup.hidden", s.FeatureId);
        Assert.Equal(Release.V2, s.Release);
        Assert.False(s.IncludedInOptimizeAll);
    }
}
