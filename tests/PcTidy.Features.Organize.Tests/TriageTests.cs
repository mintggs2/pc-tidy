using System.Windows.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.Triage;
using PcTidy.Features.Organize.UI;
using Xunit;

namespace PcTidy.Features.Organize.Tests;

/// <summary>Keyboard triage: a key sequence becomes ONE plan; nothing changes before the confirm.</summary>
public class TriageTests
{
    /// <summary>Files from newest to oldest: a.jpg, b.pdf, c.zip, d.txt, e.png (the triage order).</summary>
    private static Sandbox Downloads()
    {
        var sb = new Sandbox(SandboxProfile.Minimal);
        sb.File("a.jpg", ageDays: 1); sb.File("b.pdf", ageDays: 2); sb.File("c.zip", ageDays: 3); sb.File("d.txt", ageDays: 4); sb.File("e.png", ageDays: 5);
        sb.File("link.lnk"); sb.File("big.iso.crdownload");
        return sb;
    }

    private static TriageSession Load(Sandbox sb) => TriageSession.Load(sb.Downloads, new OrganizeSettings(), new ManagedFileWalker(), sb.Safety, sb.Clock.UtcNow);

    private static int KeyOf(TriageSession s, string label) => s.Targets.Single(t => t.Label == label).Number;

    [Fact]
    public void Targets_are_the_first_nine_move_rules_and_files_skip_what_is_never_moved()
    {
        using var sb = Downloads();
        var s = Load(sb);
        Assert.Equal(9, s.Targets.Count);
        Assert.Equal(Enumerable.Range(1, 9), s.Targets.Select(t => t.Number));
        Assert.Equal("Invoices, receipts and statements", s.Targets[0].Label);
        Assert.Contains(s.Targets, t => t.Label == "Pictures" && t.Action.Destination == "Pictures");
        Assert.Equal(new[] { "a.jpg", "b.pdf", "c.zip", "d.txt", "e.png" }, s.Files.Select(f => f.Name));
        // The rules' own suggestion is shown with its key.
        Assert.Equal($"Rules say: {KeyOf(s, "Pictures")} · Pictures", s.Files[0].Suggestion);
        Assert.Equal(TriageOutcome.Ignored, s.Press(TriageKey.Previous));
    }

    [Fact]
    public void A_key_sequence_builds_one_plan_with_every_decision()
    {
        using var sb = Downloads();
        var s = Load(sb);
        int pictures = KeyOf(s, "Pictures"), documents = KeyOf(s, "Documents");

        Assert.Equal(TriageOutcome.Decided, s.Press(TriageKey.Target1 + (pictures - 1)));   // a.jpg → Pictures
        Assert.Equal(TriageOutcome.Decided, s.Press(TriageKey.Keep));                        // b.pdf stays
        Assert.Equal(TriageOutcome.Decided, s.Press(TriageKey.Quarantine));                  // c.zip → quarantine
        Assert.Equal(TriageOutcome.NeedsName, s.Press(TriageKey.Rename));                    // d.txt → "notes"
        Assert.NotNull(s.Rename("bad|name"));
        Assert.Null(s.Rename("notes"));
        Assert.Equal(TriageOutcome.Navigated, s.Press(TriageKey.Previous));                  // back to d.txt
        Assert.Equal(TriageOutcome.Navigated, s.Press(TriageKey.Next));                      // e.png, left undecided
        Assert.Equal(TriageOutcome.Ignored, s.Press(TriageKey.Next));
        // Change of mind: b.pdf goes to Documents after all.
        for (int i = 0; i < 3; i++) s.Press(TriageKey.Previous);
        Assert.Equal("b.pdf", s.Current!.Name);
        s.Press(TriageKey.Target1 + (documents - 1));
        Assert.Equal(TriageOutcome.Finished, s.Press(TriageKey.Finish));
        Assert.Equal(TriageOutcome.Ignored, s.Press(TriageKey.Keep));

        // Nothing moved yet.
        Assert.True(File.Exists(Path.Combine(sb.Downloads, "a.jpg")));
        var plan = s.BuildPlan("Sort Downloads one by one");
        Assert.Equal(OrganizeModule.FeatureId, plan.FeatureId);
        Assert.Empty(s.Problems);
        var byKind = plan.Items.Where(i => i.Kind != ActionKind.CreateDirectory).ToDictionary(i => Path.GetFileName(i.SourcePath!));
        Assert.Equal(new[] { "a.jpg", "b.pdf", "c.zip", "d.txt" }, byKind.Keys.Order());
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "a.jpg"), byKind["a.jpg"].DestinationPath);
        Assert.Equal(ActionKind.MovePath, byKind["a.jpg"].Kind);
        Assert.Equal(Path.Combine(sb.Downloads, "Documents", "b.pdf"), byKind["b.pdf"].DestinationPath);
        Assert.Equal(ActionKind.QuarantinePath, byKind["c.zip"].Kind);
        Assert.Equal(ActionKind.RenamePath, byKind["d.txt"].Kind);
        Assert.Equal(Path.Combine(sb.Downloads, "notes.txt"), byKind["d.txt"].DestinationPath);
        // Folders come first, once each; every item carries what the engine re-checks.
        Assert.Equal(new[] { "Documents", "Pictures" }, plan.Items.TakeWhile(i => i.Kind == ActionKind.CreateDirectory).Select(i => Path.GetFileName(i.DestinationPath!)).Order());
        Assert.All(byKind.Values, i => { Assert.True(i.ExpectedSize > 0); Assert.NotNull(i.ExpectedLastWriteUtc); Assert.NotEqual(NameClashPolicy.Overwrite, i.ClashPolicy); });
    }

    [Theory]
    [InlineData("Invoice 2024.03", "That's the name it already has.", null)]        // R, Enter: nothing changes
    [InlineData("Invoice 2024.03 paid", null, "Invoice 2024.03 paid.pdf")]         // the dot in the name isn't an extension
    [InlineData("Invoice 2024.03 paid.pdf", null, "Invoice 2024.03 paid.pdf")]     // typing the extension doesn't double it
    [InlineData("setup v1.2", null, "setup v1.2.pdf")]
    public void A_name_with_dots_keeps_the_files_real_extension(string typed, string? problem, string? expected)
    {
        using var sb = new Sandbox(SandboxProfile.Minimal);
        sb.File("Invoice 2024.03.pdf", ageDays: 1);
        var s = Load(sb);
        Assert.Equal("Invoice 2024.03.pdf", s.Current!.Name);
        Assert.Equal(TriageOutcome.NeedsName, s.Press(TriageKey.Rename));
        Assert.Equal(problem, s.Rename(typed));
        if (expected is null) { Assert.Equal(TriageDecisionKind.Undecided, s.Files[0].Decision.Kind); return; }
        var rename = Assert.Single(s.BuildPlan("t").Items);
        Assert.Equal(Path.Combine(sb.Downloads, expected), rename.DestinationPath);
    }

    [Fact]
    public void Taken_names_get_a_number_and_the_guard_can_refuse_a_decision()
    {
        using var sb = Downloads();
        sb.File(Path.Combine("Pictures", "a.jpg"));
        var s = Load(sb);
        s.Press(TriageKey.Target1 + (KeyOf(s, "Pictures") - 1));
        var plan = s.BuildPlan("t");
        Assert.Equal(Path.Combine(sb.Downloads, "Pictures", "a (2).jpg"), plan.Items.Single(i => i.Kind == ActionKind.MovePath).DestinationPath);

        // A destination the guard won't allow (a rule pointing into Windows) stays out of the plan, with a reason.
        var settings = new OrganizeSettings();
        settings.Rules.Insert(0, new OrganizeRule { Id = "bad", Name = "Into Windows", Conditions = ConditionGroup.AllOf(RuleCondition.Ext("txt")), Action = new RuleAction { Destination = sb.Locations.WindowsDir } });
        var s2 = TriageSession.Load(sb.Downloads, settings, new ManagedFileWalker(), sb.Safety, sb.Clock.UtcNow);
        s2.Press(TriageKey.Target1);
        var plan2 = s2.BuildPlan("t");
        Assert.Empty(plan2.Items);
        Assert.Single(s2.Problems);
    }

    [Fact]
    public void Real_keys_map_to_triage_keys()
    {
        Assert.True(TriageViewModel.TryMap(Key.D1, out var k)); Assert.Equal(TriageKey.Target1, k);
        Assert.True(TriageViewModel.TryMap(Key.NumPad9, out k)); Assert.Equal(TriageKey.Target9, k);
        Assert.True(TriageViewModel.TryMap(Key.K, out k)); Assert.Equal(TriageKey.Keep, k);
        Assert.True(TriageViewModel.TryMap(Key.Q, out k)); Assert.Equal(TriageKey.Quarantine, k);
        Assert.True(TriageViewModel.TryMap(Key.R, out k)); Assert.Equal(TriageKey.Rename, k);
        Assert.True(TriageViewModel.TryMap(Key.Left, out k)); Assert.Equal(TriageKey.Previous, k);
        Assert.True(TriageViewModel.TryMap(Key.Right, out k)); Assert.Equal(TriageKey.Next, k);
        Assert.False(TriageViewModel.TryMap(Key.Escape, out _)); // Esc asks to stop (the window handles it); Done/Enter finishes
        Assert.False(TriageViewModel.TryMap(Key.D0, out _));
        Assert.False(TriageViewModel.TryMap(Key.Tab, out _));
    }
}
