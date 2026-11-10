using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Xunit;
using Badge = PcTidy.Core.Model.Badge;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PcTidy.UI.Tests.Shell;

/// <summary>Preselection from other sections, learned "start unticked" groups, and what is recorded for learning.</summary>
public class NavigationAndLearningTests
{
    private static ScanItem Item(string id, Badge badge = Badge.Safe, bool selected = true, string? src = null, bool reversible = true, string? disabled = null) => new()
    {
        Id = id, Title = id, Reason = "because", Badge = badge, DefaultSelected = selected, DisabledReason = disabled,
        Actions = new[] { new PlanItem { Kind = ActionKind.QuarantinePath, FeatureId = "cleanup", Description = id, SourcePath = src ?? $@"C:\t\{id}", Reversible = reversible } },
    };

    private static SelectionModel Model(out SectionModel section)
    {
        var result = new ScanResult
        {
            FeatureId = "cleanup", Title = "Clean",
            Groups = new[]
            {
                new ScanGroup { Id = "temp", Title = "Temp files", Items = new[] { Item("a"), Item("b"), Item("c", src: @"C:\t\special") } },
                new ScanGroup { Id = "bin", Title = "Recycle Bin", Badge = Badge.Permanent, Items = new[] { Item("p", Badge.Permanent, false, reversible: false) } },
                new ScanGroup { Id = "info", Title = "Not cleaned", Items = new[] { Item("x", disabled: "Protected") } },
            },
        };
        section = new SectionModel("cleanup", "Clean", SymbolRegular.Broom24, result);
        var model = new SelectionModel(false);
        model.Load(new[] { section }, (_, _, i) => OptimizePresets.IsTickableByDefault(i.Item));
        return model;
    }

    [Fact]
    public void Preselect_ticks_exactly_the_asked_items_by_id_or_path_and_never_permanent_or_disabled_ones()
    {
        var model = Model(out _);
        var r = model.Preselect(new[] { "a", @"c:\T\SPECIAL", "p", "x", "missing" });
        Assert.Equal(2, r.Picked);
        Assert.Equal(2, r.NotPickable);
        Assert.Equal(1, r.NotFound);
        Assert.Equal(new[] { "a", "c" }, model.CheckedItems().Select(i => i.Item.Id).OrderBy(x => x));
        Assert.Contains("Nothing runs until you confirm", r.Message);
    }

    private sealed class FakeLearning : IFlowLearning
    {
        public HashSet<(string, string)> Off { get; } = new();
        public List<GroupDecision> Recorded { get; } = new();
        public List<string> Undone { get; } = new();
        public bool IsUntickedByDefault(string featureId, string groupId) => Off.Contains((featureId, groupId));
        public string? WhyUnticked(string featureId, string groupId) => "You skipped this 3 of the last 3 times.";
        public void ResetSkip(string featureId, string groupId) => Off.Remove((featureId, groupId));
        public void RecordDecisions(IReadOnlyList<GroupDecision> decisions, IReadOnlyList<RunRecord> runs) => Recorded.AddRange(decisions);
        public void RecordUndo(IReadOnlyList<RunRecord> runs) => Undone.AddRange(runs.Select(r => r.RunId));
    }

    [Fact]
    public void A_group_skipped_the_last_times_starts_unticked_and_says_why()
    {
        var learning = new FakeLearning();
        learning.Off.Add(("cleanup", "temp"));
        var model = Model(out var section);
        var off = LearnedDefaults.Unticked(learning, new[] { section });
        Assert.Single(off);
        model.Retick((_, g, i) => OptimizePresets.IsTickableByDefault(i.Item) && !off.Contains(g));
        Assert.Empty(model.CheckedItems());
        var note = LearnedDefaults.Note(off.ToList(), g => true, learning);
        Assert.Contains("Temp files", note);
        Assert.Contains("3 of the last 3", note);
    }

    [Fact]
    public void Learning_that_throws_never_breaks_a_preview()
    {
        var model = Model(out var section);
        Assert.Empty(LearnedDefaults.Unticked(new ThrowingLearning(), new[] { section }));
        Assert.Empty(LearnedDefaults.Unticked(null, new[] { section }));
    }

    private sealed class ThrowingLearning : IFlowLearning
    {
        public bool IsUntickedByDefault(string featureId, string groupId) => throw new InvalidOperationException("damaged file");
        public string? WhyUnticked(string featureId, string groupId) => null;
        public void ResetSkip(string featureId, string groupId) { }
        public void RecordDecisions(IReadOnlyList<GroupDecision> decisions, IReadOnlyList<RunRecord> runs) { }
        public void RecordUndo(IReadOnlyList<RunRecord> runs) { }
    }

    [Fact]
    public void Decisions_cover_every_pickable_group_with_accept_or_reject()
    {
        var model = Model(out _);
        model.Preselect(new[] { "a" });
        var d = model.Decisions();
        Assert.Equal(new[] { "bin", "temp" }, d.Select(x => x.GroupId).OrderBy(x => x)); // the info-only group isn't offered
        Assert.True(d.Single(x => x.GroupId == "temp").Accepted);
        Assert.False(d.Single(x => x.GroupId == "bin").Accepted);
        Assert.Equal("a", d.Single(x => x.GroupId == "temp").Example);
    }

    [Fact]
    public void Navigation_keys_can_name_a_group()
    {
        Assert.Equal(("cleanup", "cleanup.browser-privacy"), PcTidy.App.Shell.ShellNavigation.SplitKey("cleanup#cleanup.browser-privacy"));
        Assert.Equal(("cleanup", (string?)null), PcTidy.App.Shell.ShellNavigation.SplitKey("cleanup"));
        Assert.Equal(("cleanup", (string?)null), PcTidy.App.Shell.ShellNavigation.SplitKey("cleanup#"));
    }
}
