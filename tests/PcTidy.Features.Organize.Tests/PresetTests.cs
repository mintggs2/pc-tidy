using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>Organize presets: Simple, By date (ISO), Projects &amp; Areas (PARA-lite), Numbered (Johnny.Decimal).</summary>
public class PresetTests
{
    private static Dictionary<string, string> Destinations(IEnumerable<OrganizeRule> rules)
        => rules.Where(r => r.Action.Kind == RuleActionKind.MoveToFolder).ToDictionary(r => r.Id, r => r.Action.Destination);

    private static string Cat(string id) => DefaultRules.CategoryRuleId(id);

    [Fact]
    public void Simple_is_exactly_the_default_rules()
    {
        var simple = RulePresets.Find(RulePresets.SimpleId)!.Create();
        var defaults = DefaultRules.Create();
        Assert.Equal(defaults.Select(r => r.Id), simple.Select(r => r.Id));
        Assert.Equal(Destinations(defaults), Destinations(simple));
    }

    [Fact]
    public void By_date_adds_iso_year_and_month_to_every_move()
    {
        var d = Destinations(RulePresets.Find(RulePresets.ByDateId)!.Create());
        Assert.Equal(@"Pictures\{year}\{year}-{month:00}", d[Cat(OrganizeCategories.Pictures)]);
        Assert.Equal(@"Pictures\Screenshots\{year}\{year}-{month:00}", d[DefaultRules.ScreenshotsId]);
        Assert.Equal(@"Documents\Invoices\{year}", d[DefaultRules.InvoicesId]);
        Assert.All(d.Values, v => Assert.True(DestinationResolver.HasDateTokens(v), v));
    }

    [Fact]
    public void Para_lite_has_projects_areas_resources_and_archive()
    {
        var rules = RulePresets.Find(RulePresets.ParaId)!.Create();
        var d = Destinations(rules);
        Assert.Equal(@"2 Areas\Finance", d[DefaultRules.InvoicesId]);
        Assert.Equal(@"3 Resources\Pictures", d[Cat(OrganizeCategories.Pictures)]);
        Assert.Equal(@"3 Resources\Documents", d[Cat(OrganizeCategories.Documents)]);
        Assert.Equal(@"4 Archive\{year}", d["preset.para.archive"]);
        // Projects can't be guessed: an example rule, switched off.
        Assert.False(rules.Single(r => r.Id == "preset.para.projects").Enabled);
        // The archive rule comes after the starters (old installers still go to quarantine) and before the categories.
        int archive = rules.FindIndex(r => r.Id == "preset.para.archive");
        Assert.True(archive > rules.FindIndex(r => r.Id == DefaultRules.OldInstallersId));
        Assert.True(archive < rules.FindIndex(r => r.Id == Cat(OrganizeCategories.Pictures)));
        Assert.Equal(RuleActionKind.Quarantine, rules.Single(r => r.Id == DefaultRules.OldInstallersId).Action.Kind);
    }

    [Fact]
    public void Numbered_uses_johnny_decimal_areas_and_categories()
    {
        var d = Destinations(RulePresets.Find(RulePresets.JohnnyDecimalId)!.Create());
        Assert.Equal(@"10-19 Admin\11 Invoices and receipts", d[DefaultRules.InvoicesId]);
        Assert.Equal(@"20-29 Media\21 Pictures", d[Cat(OrganizeCategories.Pictures)]);
        Assert.Equal(@"20-29 Media\22 Screenshots", d[DefaultRules.ScreenshotsId]);
        Assert.Equal(@"30-39 Software\31 Installers", d[Cat(OrganizeCategories.Installers)]);
        Assert.Equal(@"90-99 Archive\91 Archives", d[Cat(OrganizeCategories.Archives)]);
        // Every number is unique and the category sits inside its area's range.
        var categories = d.Values.Select(v => v.Split('\\')).ToList();
        Assert.Equal(categories.Count, categories.Select(c => c[1][..2]).Distinct().Count());
        Assert.All(categories, c => Assert.Equal(c[0][0], c[1][0]));
    }

    [Theory]
    [InlineData(RulePresets.SimpleId)]
    [InlineData(RulePresets.ByDateId)]
    [InlineData(RulePresets.ParaId)]
    [InlineData(RulePresets.JohnnyDecimalId)]
    public void Every_preset_is_valid_relative_and_ends_with_other(string id)
    {
        using var sb = new Sandbox();
        var rules = RulePresets.Find(id)!.Create();
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Equal(DefaultRules.OtherId, rules[^1].Id);
        Assert.All(rules, r => Assert.True(RulePresets.IsPresetRule(r), r.Id));
        foreach (var r in rules.Where(r => r.Enabled))
        {
            Assert.False(Path.IsPathFullyQualified(r.Action.Destination ?? ""), r.Action.Destination);
            Assert.DoesNotContain(RuleValidator.Validate(r, new[] { sb.Downloads }, sb.Safety, sb.AppSettings), p => p.Blocking);
        }
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(RulePresets.Summary(r))));
    }

    [Fact]
    public void Two_rules_with_the_same_id_dont_break_the_preview()
    {
        var current = DefaultRules.Create();
        current.Add(current[0].Clone()); // a hand-edited or merged settings file
        var changes = RulePresets.Preview(current, RulePresets.Find(RulePresets.JohnnyDecimalId)!);
        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Preview_lists_the_changes_and_keeps_the_users_own_rules_on_top()
    {
        var current = DefaultRules.Create();
        var mine = new OrganizeRule { Id = "mine-1", Name = "My scans", Conditions = ConditionGroup.AllOf(RuleCondition.NameHas("scan")), Action = new RuleAction { Destination = "Scans" } };
        current.Insert(0, mine);
        var preset = RulePresets.Find(RulePresets.JohnnyDecimalId)!;

        var changes = RulePresets.Preview(current, preset);
        Assert.Contains(changes, c => c.Kind == RuleChangeKind.Kept && c.RuleName == "My scans" && c.Text.Contains("Your own rule"));
        var pictures = Assert.Single(changes, c => c.RuleName == "Pictures");
        Assert.Equal(RuleChangeKind.Changed, pictures.Kind);
        Assert.Contains(@"20-29 Media\21 Pictures", pictures.Text);
        Assert.Contains("was:", pictures.Text);

        var applied = RulePresets.Apply(current, preset);
        Assert.Equal("mine-1", applied[0].Id);
        Assert.Equal("Scans", applied[0].Action.Destination);
        Assert.Equal(current.Count, applied.Count);

        // Same rules again: nothing to change, so "Use this preset" stays off.
        var vm = new PresetsViewModel(DefaultRules.Create());
        Assert.Equal(RulePresets.SimpleId, vm.SelectedPreset!.Id);
        Assert.Contains("Nothing would change", vm.ChangeSummary);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        vm.SelectedPreset = RulePresets.Find(RulePresets.ParaId);
        Assert.Contains(vm.Changes, c => c.Kind == "New" && c.RuleName == "Not changed for 6 months");
        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);
        Assert.Contains(vm.Result!, r => r.Id == "preset.para.archive");
    }

    [Fact]
    public async Task A_numbered_preset_sorts_into_its_folders_and_never_picks_them_up_again()
    {
        using var sb = new Sandbox();
        sb.File("cat.jpg"); sb.File("invoice 42.pdf");
        var settings = new OrganizeSettings { Rules = RulePresets.Apply(DefaultRules.Create(), RulePresets.Find(RulePresets.JohnnyDecimalId)!) };
        var preview = await sb.PlanAsync(settings);
        Assert.Equal(@"20-29 Media\21 Pictures", preview.ActionFor("cat.jpg").RelativeDestFolder());
        Assert.Equal(@"10-19 Admin\11 Invoices and receipts", preview.ActionFor("invoice 42.pdf").RelativeDestFolder());

        // After the move, the area folder is a sorting folder: it stays put, and nothing inside is sorted again.
        Directory.CreateDirectory(Path.Combine(sb.Downloads, "20-29 Media", "21 Pictures"));
        File.Move(Path.Combine(sb.Downloads, "cat.jpg"), Path.Combine(sb.Downloads, "20-29 Media", "21 Pictures", "cat.jpg"));
        var again = await sb.PlanAsync(settings);
        Assert.DoesNotContain(again.Actions, a => a.Name == "cat.jpg");
        Assert.Equal(SkipKind.AlreadySorted, again.SkipFor("20-29 Media").Kind);
    }

    [Fact]
    public async Task By_date_preset_files_land_in_iso_folders()
    {
        using var sb = new Sandbox();
        var path = sb.File("song.mp3", ageDays: 40);
        var settings = new OrganizeSettings { Rules = RulePresets.Find(RulePresets.ByDateId)!.Create() };
        var preview = await sb.PlanAsync(settings);
        var local = File.GetLastWriteTimeUtc(path).ToLocalTime();
        Assert.Equal($@"Music\{local:yyyy}\{local:yyyy}-{local:MM}", preview.ActionFor("song.mp3").RelativeDestFolder());
    }
}
