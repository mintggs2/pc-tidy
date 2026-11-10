using PcTidy.Features.Privacy.Sweep;
using PcTidy.TestbedGen;

namespace PcTidy.Features.Privacy.Tests;

/// <summary>Privacy sweep: planning over a sandbox with the REAL SafetyGuard, and run → undo → identical.</summary>
public class SweepTests
{
    /// <summary>Recent files, jump lists (automatic + custom) and Quick access, laid out the way Windows does under Roaming.</summary>
    private static (string[] Lnk, string[] Jump, string Quick) PutRecent(IKnownLocations k)
    {
        var recent = RecentActivity.RecentFolder(k);
        var lnk = new[] { "Budget 2026.xlsx.lnk", "Trip photos.lnk", "Ünïcode notes.txt.lnk" }
            .Select(n => TestPc.Put(Path.Combine(recent, n), "shortcut " + n)).ToArray();
        TestPc.Put(Path.Combine(recent, "desktop.ini"), "[.ShellClassInfo]");
        var jump = new[]
        {
            TestPc.Put(Path.Combine(RecentActivity.AutomaticDestinations(k), "5f7b5f1e01b83767.automaticDestinations-ms"), "jump a"),
            TestPc.Put(Path.Combine(RecentActivity.AutomaticDestinations(k), "9b9cdc69c1c24e2b.automaticDestinations-ms"), "jump b"),
            TestPc.Put(Path.Combine(RecentActivity.CustomDestinations(k), "28c8b86deab549a1.customDestinations-ms"), "custom"),
        };
        var quick = TestPc.Put(Path.Combine(RecentActivity.AutomaticDestinations(k), RecentActivity.QuickAccessFile), "quick access + pins");
        return (lnk, jump, quick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recent_and_jump_lists_are_planned_as_quarantine_and_the_real_guard_allows_them(bool defaultProfile)
    {
        using var pc = TestPc.HandMade("plan-recent", defaultProfile ? SandboxProfile.Default : SandboxProfile.Minimal);
        var (lnk, jump, quick) = PutRecent(pc.K);

        var result = await new PrivacySweepScanner().ScanAsync(pc.Context(), null, CancellationToken.None);

        var group = result.Groups.Single(g => g.Id == PrivacyIds.RecentGroup);
        var recentRow = group.Items.Single(i => i.Title == "Recent files list");
        Assert.Equal(lnk.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), recentRow.Actions.Select(a => a.SourcePath!).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(recentRow.Actions, a => a.SourcePath!.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase));
        var jumpRow = group.Items.Single(i => i.Title == "Jump lists");
        Assert.Equal(jump.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), jumpRow.Actions.Select(a => a.SourcePath!).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("pinned", jumpRow.Reason, StringComparison.OrdinalIgnoreCase);
        var quickRow = group.Items.Single(i => i.Title.Contains("Quick access", StringComparison.Ordinal));
        Assert.Equal(quick, quickRow.Actions.Single().SourcePath);

        foreach (var row in group.Items)
        {
            Assert.Equal(Badge.Check, row.Badge);
            Assert.False(row.DefaultSelected);
            Assert.Equal(row.Actions.Count, row.FileCount);
            Assert.Equal(row.Actions.Sum(a => a.ExpectedSize), row.SizeBytes);
            Assert.All(row.Actions, a =>
            {
                Assert.Equal(ActionKind.QuarantinePath, a.Kind);
                Assert.True(a.Reversible);
                Assert.Equal(PrivacyIds.Privacy, a.FeatureId);
                // Recent lives under RoamingAppData: the real guard must let the Privacy sweep quarantine it.
                var v = pc.Host.Safety.Check(a);
                Assert.True(v.Allowed, $"{a.SourcePath}: {v.Reason}");
            });
        }
    }

    [Fact]
    public async Task Sweep_on_a_generated_pc_runs_then_undo_puts_everything_back_identically()
    {
        using var pc = TestPc.Generated("e2e-sweep", SandboxProfile.Minimal);
        PutRecent(pc.K);
        var before = TestbedBuilder.Manifest(pc.Root);

        var result = await new PrivacySweepScanner().ScanAsync(pc.Context(), null, CancellationToken.None);
        var picked = result.Groups.Single(g => g.Id == PrivacyIds.RecentGroup).Items;
        var plan = PlanBuilder.FromSelection(PrivacyIds.Privacy, "Privacy sweep", picked);
        Assert.Equal(7, plan.Items.Count);

        var run = await pc.Host.Executor.ExecuteAsync(plan, TestPc.Real, null, CancellationToken.None);
        Assert.True(run.DoneCount == plan.Items.Count, Runs.Problems(run));
        Assert.All(plan.Items, i => Assert.False(File.Exists(i.SourcePath)));
        Assert.True(File.Exists(Path.Combine(RecentActivity.RecentFolder(pc.K), "desktop.ini")));

        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        var diff = pc.DiffFrom(before);
        Assert.True(diff.Identical, diff.Format());
    }

    [Fact]
    public async Task Nothing_is_ticked_every_row_explains_what_is_lost_and_windows_only_items_are_not_tickable()
    {
        using var pc = TestPc.Generated("sweep-rows", SandboxProfile.Default);
        PutRecent(pc.K);
        var scanner = new PrivacySweepScanner();
        Assert.False(scanner.IncludedInOptimizeAll);

        var result = await scanner.ScanAsync(pc.Context(), null, CancellationToken.None);

        Assert.All(result.Groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
        Assert.All(result.AllItems, i =>
        {
            Assert.False(i.DefaultSelected, i.Title);
            Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Title);
            Assert.True(i.Details?.ContainsKey("What's lost") == true, i.Title + " doesn't say what's lost");
        });
        var windows = result.Groups.Single(g => g.Id == PrivacyIds.WindowsGroup).Items;
        Assert.Equal(new[] { "Clipboard history", "Activity history" }, windows.Select(i => i.Title));
        Assert.All(windows, i => { Assert.NotNull(i.DisabledReason); Assert.Empty(i.Actions); });
        var history = result.Groups.Single(g => g.Id == PrivacyIds.BrowsersGroup).Items.Single(i => i.Title == "Browser history");
        Assert.NotNull(history.DisabledReason);
        Assert.Contains("Privacy extras", history.DisabledReason);
        // No item anywhere touches saved passwords, Local State or anything but a cookie database / recent list.
        Assert.DoesNotContain(result.AllItems.SelectMany(i => i.Actions), a =>
            (a.SourcePath ?? "").Contains("Login Data", StringComparison.OrdinalIgnoreCase) || (a.SourcePath ?? "").Contains("logins.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cookie_keeper_rows_use_the_saved_keep_list_and_the_real_guard_allows_them()
    {
        using var pc = TestPc.Generated("sweep-cookies", SandboxProfile.Default);
        new PrivacySettings { KeepSites = new() { "https://www.GitHub.com/login", "google.com", "not a site" } }.Store(pc.Host.Settings);

        var result = await new PrivacySweepScanner().ScanAsync(pc.Context(), null, CancellationToken.None);

        var rows = result.Groups.Single(g => g.Id == PrivacyIds.BrowsersGroup).Items.Where(i => i.Actions.Count > 0).ToList();
        // Generated PC: Chrome, Edge and Brave (Default + Profile 1) and one Firefox profile, each with a cookie database.
        Assert.Equal(7, rows.Count);
        foreach (var row in rows)
        {
            Assert.Null(row.DisabledReason);
            Assert.Equal(Badge.Check, row.Badge);
            Assert.False(row.DefaultSelected);
            var a = row.Actions.Single();
            Assert.Equal(ActionKind.CookiesDeleteExcept, a.Kind);
            Assert.Equal("github.com\ngoogle.com", a.Args!["keepHosts"]);
            Assert.True(Directory.Exists(a.Args["profileDir"]));
            Assert.NotEmpty(a.RequiresClosedApps!);
            var v = pc.Host.Safety.Check(a);
            Assert.True(v.Allowed, $"{a.Args["profileDir"]}: {v.Reason}");
        }
        Assert.Contains(rows, r => r.Actions[0].Args!["browser"] == "firefox");
    }

    [Theory]
    [InlineData("https://www.Example.com:443/a?b", "example.com")]
    [InlineData(".github.com", "github.com")]
    [InlineData("WWW.reddit.com", "reddit.com")]
    [InlineData("localhost", "")]
    [InlineData("not a site", "")]
    [InlineData("", "")]
    public void Keep_sites_are_normalized_to_hosts(string raw, string expected)
        => Assert.Equal(expected, CookieKeeper.NormalizeSite(raw));

    [Fact]
    public async Task A_pc_without_recent_files_or_browsers_shows_no_recent_group_and_no_cookie_rows()
    {
        using var pc = TestPc.HandMade("sweep-empty");
        var result = await new PrivacySweepScanner().ScanAsync(pc.Context(), null, CancellationToken.None);
        Assert.DoesNotContain(result.Groups, g => g.Id == PrivacyIds.RecentGroup);
        Assert.DoesNotContain(result.AllItems, i => i.Actions.Count > 0);
        Assert.Empty(result.Notes);
    }
}
