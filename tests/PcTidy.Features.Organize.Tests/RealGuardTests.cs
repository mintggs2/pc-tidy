using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Safety;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>
/// End-to-end scans with the REAL SafetyGuard on a SandboxKnownLocations PC. The fake guard in TestSupport is
/// "Plan §6 in miniature"; the root-folder probe bug (Downloads itself is blocked as a whole) only showed up with
/// the real one, so the main paths run against it here.
/// </summary>
public class RealGuardTests
{
    private static SafetyGuard Guard(Sandbox sb) => new(sb.Locations, sb.AppSettings);

    private static void Typical(Sandbox sb)
    {
        sb.File("photo.jpg");
        sb.File("report.pdf");
        sb.File("invoice_2026_10.pdf");
        sb.File("song.mp3");
        sb.File("backup.zip");
        sb.File("app.lnk");
        sb.File("movie.mkv.crdownload");
        sb.File(@"my-app\package.json", "{}");
        sb.File(@"my-app\index.js");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloads_is_sorted_with_the_real_guard(bool minimalPc)
    {
        using var sb = new Sandbox(minimalPc ? SandboxProfile.Minimal : SandboxProfile.Default);
        Typical(sb);
        var guard = Guard(sb);
        var planner = new OrganizePlanner(guard, sb.InUse, new ManagedFileWalker(), sb.AppSettings, sb.Clock);

        var p = await planner.PlanAsync(new OrganizeRequest
        {
            Roots = new[] { sb.Downloads },
            Settings = new OrganizeSettings(),
            Culture = System.Globalization.CultureInfo.InvariantCulture,
        }, null, CancellationToken.None);

        // Downloads itself can't be moved, but that must not stop its contents from being sorted.
        var root = Assert.Single(p.Roots);
        Assert.Null(root.SkipReason);
        Assert.Equal("Pictures", p.ActionFor("photo.jpg").RelativeDestFolder());
        Assert.Equal("Documents", p.ActionFor("report.pdf").RelativeDestFolder());
        Assert.Equal(Path.Combine("Documents", "Invoices"), p.ActionFor("invoice_2026_10.pdf").RelativeDestFolder());
        Assert.Equal("Music", p.ActionFor("song.mp3").RelativeDestFolder());
        Assert.Equal("Archives", p.ActionFor("backup.zip").RelativeDestFolder());
        Assert.Equal(SkipKind.Shortcut, p.SkipFor("app.lnk").Kind);
        Assert.Equal(SkipKind.StillDownloading, p.SkipFor("movie.mkv.crdownload").Kind);
        Assert.DoesNotContain(p.Actions, a => a.SourcePath.Contains("my-app", StringComparison.OrdinalIgnoreCase));

        // Every planned step passes the real guard's final check, exactly as the engine will run it.
        var plan = PlanAssembler.Build("Organize Downloads", p.Actions);
        Assert.NotEmpty(plan.Items);
        Assert.All(plan.Items, item =>
        {
            var verdict = guard.Check(item);
            Assert.True(verdict.Allowed, $"{item.Description}: {verdict.Reason}");
        });
    }

    [Fact]
    public async Task Optimize_all_scanner_uses_the_real_guard_and_leaves_a_onedrive_desktop_alone()
    {
        using var sb = new Sandbox(SandboxProfile.Default); // Desktop redirected into OneDrive
        Typical(sb);
        sb.File("note.txt", folder: sb.Locations.Desktop);
        var guard = Guard(sb);
        var ctx = new ScanContext
        {
            Locations = sb.Locations, Safety = guard, InUse = sb.InUse, Settings = sb.AppSettings, Walker = new ManagedFileWalker(), Preset = "Recommended",
        };

        Assert.Equal(new[] { sb.Downloads }, OrganizeScanner.DefaultRoots(sb.Locations, guard, sb.AppSettings));
        var result = await new OrganizeScanner(sb.Clock).ScanAsync(ctx, null, CancellationToken.None);
        var actions = result.AllItems.SelectMany(i => i.Actions).ToList();
        Assert.Contains(actions, a => a.Kind == ActionKind.MovePath && Path.GetFileName(a.SourcePath) == "photo.jpg");
        Assert.DoesNotContain(actions, a => a.SourcePath is { } s && PathUtil.IsUnder(s, sb.Locations.Desktop));
        Assert.All(actions, a => Assert.True(guard.Check(a).Allowed, a.Description));
    }

    [Fact]
    public void Real_guard_still_rejects_rules_that_point_into_protected_places()
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        var rule = new OrganizeRule
        {
            Name = "Into Windows",
            Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf")),
            Action = new RuleAction { Kind = RuleActionKind.MoveToFolder, Destination = sb.Locations.WindowsDir },
        };
        var problems = RuleValidator.Validate(rule, new[] { sb.Downloads }, Guard(sb), sb.AppSettings);
        Assert.Contains(problems, pr => pr.Blocking);
    }
}
