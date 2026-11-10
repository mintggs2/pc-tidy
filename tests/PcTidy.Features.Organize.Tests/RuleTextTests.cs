using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>The rules list says what each rule catches in plain words, never in pattern syntax.</summary>
public class RuleTextTests
{
    private static string For(string ruleId) => RuleText.Conditions(DefaultRules.Create().Single(r => r.Id == ruleId).Conditions);

    [Fact]
    public void Default_rules_read_as_plain_words()
    {
        Assert.Equal("Pictures · names starting with Screenshot, Screen Shot or Snip", For(DefaultRules.ScreenshotsId));
        Assert.Equal("Any name", For(DefaultRules.OtherId));
        Assert.Equal(".torrent files", For(DefaultRules.TorrentsId));
        Assert.Equal("Installers · added more than 30 days ago", For(DefaultRules.OldInstallersId));
        Assert.Equal(".pdf files · name has “invoice”, “receipt” or “statement”", For(DefaultRules.InvoicesId));
        // Common types come first, so a long list starts with the ones people know.
        Assert.StartsWith(".jpg, .png, .heic, .webp and ", For(DefaultRules.CategoryRuleId(OrganizeCategories.Pictures)));
        Assert.All(DefaultRules.Create(), r => Assert.DoesNotContain("matches", RuleText.Conditions(r.Conditions)));
    }

    [Theory]
    [InlineData("*draft", "Names ending with draft")]
    [InlineData("*2026*", "Names containing 2026")]
    [InlineData("IMG_????.jpg", "Names like IMG_????.jpg")]
    [InlineData("notes.txt", "Named notes.txt")]
    [InlineData("Scan*;*draft", "Names starting with Scan or names ending with draft")]
    public void Name_patterns_are_described(string glob, string expected)
        => Assert.Equal(expected, RuleText.Conditions(ConditionGroup.AllOf(RuleCondition.Glob(glob))));

    [Fact]
    public void Sizes_ages_and_any_mode_are_described()
    {
        var big = new RuleCondition { Kind = ConditionKind.Size, MinBytes = 1024L * 1024 * 1024 };
        var recent = new RuleCondition { Kind = ConditionKind.Age, DateField = DateField.Modified, MaxDays = 7 };
        Assert.Equal("Bigger than 1 GB · changed in the last 7 days", RuleText.Conditions(ConditionGroup.AllOf(big, recent)));
        Assert.Equal(".mp4 files or .mov files", RuleText.Conditions(ConditionGroup.AnyOf(RuleCondition.Ext("mp4"), RuleCondition.Ext("mov"))));
        Assert.Equal("No conditions yet", RuleText.Conditions(new ConditionGroup()));
    }
}
