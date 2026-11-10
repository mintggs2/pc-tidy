using PcTidy.App.Pages;
using PcTidy.App.WhatIs;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests;

/// <summary>Fixes from the v3 UI review (docs\reviews\v3-ui-review.md) that live in the shared flow and the shell.</summary>
public class V3UiReviewFixTests
{
    private static PlanItem Cookies(string browser, string profile, params string[] keep) => new()
    {
        Kind = ActionKind.CookiesDeleteExcept, FeatureId = "privacy", Description = $"Delete all {browser} cookies ({profile})", Reversible = true,
        Args = new Dictionary<string, string> { ["browser"] = browser.ToLowerInvariant(), ["profileDir"] = profile, ["keepHosts"] = string.Join("\n", keep) },
    };

    private static ActionPlan Plan(params PlanItem[] items) => new() { FeatureId = "privacy", Title = "Privacy sweep", Items = items };

    // ---------------- UI3-20: the cookie confirm names the action ----------------

    [Fact]
    public void Clearing_cookies_is_named_as_such_and_says_you_will_be_signed_out()
    {
        var items = new[] { Cookies("Chrome", "Default"), Cookies("Chrome", "Profile 1"), Cookies("Edge", "Default"), Cookies("Firefox", "abc.default") };
        var model = new ConfirmModel(new[] { Plan(items) }, null, 7);

        Assert.DoesNotContain(model.Groups, g => g.Kind == ConfirmModel.KindSettings); // never "Change 4 settings"
        var line = Assert.Single(model.Groups, g => g.Kind == ConfirmModel.KindCookies);
        Assert.Equal("Delete all cookies (0 kept sites)", line.Title);
        Assert.Contains("signed out of every website in Chrome, Edge and Firefox", line.Detail);
        Assert.Contains("Close Chrome, Edge and Firefox first", line.Detail);
        Assert.Contains("4 browser profiles", line.Detail);

        Assert.Equal("Cookies in 4 browser profiles", PlanFacts.TotalsLine(items));
        Assert.Equal("Delete cookies (4 profiles)", PlanFacts.PrimaryAction(items));
        Assert.Equal(4, model.Counts.Total); // the same count as before: only the words changed
    }

    [Fact]
    public void Cookie_confirm_counts_the_kept_sites()
    {
        var items = new[] { Cookies("Chrome", "Default", "github.com", "google.com"), Cookies("Edge", "Default", "github.com", "google.com") };
        var line = Assert.Single(new ConfirmModel(new[] { Plan(items) }, null, 7).Groups, g => g.Kind == ConfirmModel.KindCookies);
        Assert.Equal("Delete cookies except 2 kept sites", line.Title);
        Assert.Contains("except github.com and google.com", line.Detail);
    }

    [Fact]
    public void Cookies_next_to_other_settings_still_count_the_settings()
    {
        var setting = new PlanItem { Kind = ActionKind.RegistrySetValue, FeatureId = "privacy", Description = "Turn off tips", Reversible = true };
        var items = new[] { Cookies("Chrome", "Default"), setting };
        Assert.Equal("Cookies in 1 browser profile · 1 setting", PlanFacts.TotalsLine(items));
        Assert.Equal("Apply 2 changes", PlanFacts.PrimaryAction(items));
        var model = new ConfirmModel(new[] { Plan(items) }, null, 7);
        Assert.Contains(model.Groups, g => g.Kind == ConfirmModel.KindSettings && g.Title == "Change 1 setting");
        Assert.Contains(model.Groups, g => g.Kind == ConfirmModel.KindCookies);
    }

    // ---------------- UI3-18: the admin line names the service, not the whole step again ----------------

    [Fact]
    public void Admin_line_lists_service_names()
    {
        var service = new PlanItem
        {
            Kind = ActionKind.ServiceSetStartType, FeatureId = "services.repair", ServiceName = "BITS", ServiceStartType = ServiceStart.Manual,
            Description = "Put 'Background Intelligent Transfer Service' (BITS) back to its Windows default, Manual (now Disabled).",
            NeedsAdmin = true, IsSystemChange = true, Reversible = true,
        };
        var admin = Assert.Single(new ConfirmModel(new[] { Plan(service) }, null, 7).Groups, g => g.Kind == ConfirmModel.KindAdmin);
        Assert.Equal(new[] { "BITS" }, admin.Examples);
    }

    // ---------------- UI3-35: Home's score counts what Services warns about ----------------

    private static readonly DriveDetails Roomy = new("C:\\", "Windows", DriveMedia.Ssd, 500L << 30, 300L << 30, true);

    [Fact]
    public void Health_score_takes_points_off_for_switched_off_services_and_says_what_it_covers()
    {
        var score = new HealthScore(new[] { Roomy }, 0, 0, false, d => "C:", switchedOffServices: 1);
        Assert.Equal(95, score.Score);
        Assert.True(score.AnyPointsOff);
        var reason = Assert.Single(score.Reasons, r => r.OpenSection == "services");
        Assert.Contains("switched off", reason.Text);
        Assert.Contains("switched-off Windows services", score.Covers);

        var notRead = new HealthScore(new[] { Roomy }, 0, 0, false, d => "C:");
        Assert.Equal(100, notRead.Score);
        Assert.False(notRead.ServicesChecked);
        Assert.DoesNotContain("services", notRead.Covers);
        Assert.Contains("Insights", notRead.Covers);
    }

    // ---------------- UI3-33: "Belongs to" for temp files ----------------

    [Theory]
    [InlineData(@"C:\Users\tester\AppData\Local\Temp\chrome_installer.log", "Chrome (installer log)")]
    [InlineData(@"C:\Users\tester\AppData\Local\Temp\steam_update.log", "Steam (log)")]
    [InlineData(@"C:\Users\tester\AppData\Local\Temp\msedge_tmp123.tmp", "Microsoft Edge (temporary file)")]
    [InlineData(@"C:\Users\tester\AppData\Local\Temp\a1b2c3.tmp", null)]
    public void Temp_files_name_the_app_from_their_file_name(string path, string? owner)
        => Assert.Equal(owner, WhatIsClassifier.TempFileOwner(path));
}
