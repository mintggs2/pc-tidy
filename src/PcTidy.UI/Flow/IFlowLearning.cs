using System.Windows;

namespace PcTidy.UI.Flow;

/// <summary>
/// What the flow pages need from "Learns from me", without depending on that section. The shell registers an
/// adapter that forwards to the Learning section's advisor when it is there, and does nothing when it isn't
/// (or when learning is off). Learning only changes what starts ticked; it never runs or hides anything.
/// </summary>
public interface IFlowLearning
{
    /// <summary>True when this group should start unticked (the user skipped it the last few times).</summary>
    bool IsUntickedByDefault(string featureId, string groupId);

    /// <summary>The plain-English why ("You skipped this 3 of the last 3 times"), or null.</summary>
    string? WhyUnticked(string featureId, string groupId);

    /// <summary>"Tick it again from now on": forgets the skips for this group.</summary>
    void ResetSkip(string featureId, string groupId);

    /// <summary>After a real run: one decision per offered group (accepted = something in it was ticked and run).</summary>
    void RecordDecisions(IReadOnlyList<GroupDecision> decisions, IReadOnlyList<Core.Model.RunRecord> runs);

    /// <summary>After the user undid runs: counts against the groups accepted in them.</summary>
    void RecordUndo(IReadOnlyList<Core.Model.RunRecord> runs);
}

/// <summary>A group the user was offered in a preview, and whether they took any of it.</summary>
public sealed record GroupDecision(string FeatureId, string GroupId, bool Accepted, string GroupTitle, string? Example);

/// <summary>Applies learned defaults to a selection (shared by the standard and the combined flow page).</summary>
public static class LearnedDefaults
{
    /// <summary>Groups that start unticked because of what was learned (only groups with something pickable).</summary>
    public static IReadOnlySet<GroupRow> Unticked(IFlowLearning? learning, IEnumerable<SectionModel> sections)
    {
        var off = new HashSet<GroupRow>();
        if (learning is null) return off;
        foreach (var s in sections)
            foreach (var g in s.Groups.Where(g => g.HasSelectable))
            {
                bool unticked;
                try { unticked = learning.IsUntickedByDefault(s.FeatureId, g.Group.Id); }
                catch (Exception) { unticked = false; } // learning must never break a preview
                if (unticked) off.Add(g);
            }
        return off;
    }

    /// <summary>The note shown above the list, or null when nothing was changed by learning.</summary>
    public static string? Note(IReadOnlyCollection<GroupRow> unticked, Func<GroupRow, bool> wouldHaveTicked, IFlowLearning? learning = null)
    {
        var groups = unticked.Where(wouldHaveTicked).GroupBy(g => g.Title).Select(g => g.First()).ToList();
        if (groups.Count == 0) return null;
        if (groups.Count == 1 && learning is not null && Why(learning, groups[0]) is { } why)
            return $"\"{groups[0].Title}\" is left unticked. {why} Tick it if you want it this time.";
        var names = groups.Select(g => g.Title).ToList();
        var list = names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";
        return $"Left unticked because you skipped {(names.Count == 1 ? "it" : "them")} the last few times: {list}. Tick {(names.Count == 1 ? "it" : "them")} if you want this time.";
    }

    private static string? Why(IFlowLearning learning, GroupRow g)
    {
        try { return learning.WhyUnticked(g.FeatureId, g.Group.Id) is { Length: > 0 } w ? w.TrimEnd('.') + "." : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The note as an info bar with "Tick again from now on": forgets the learned skips and ticks those groups'
    /// default items again (<paramref name="retick"/>). Null when learning changed nothing.
    /// </summary>
    public static UIElement? NoteBar(IFlowLearning? learning, IReadOnlyCollection<GroupRow> unticked, Func<GroupRow, bool> wouldHaveTicked, Action<IReadOnlyCollection<GroupRow>> retick)
    {
        if (learning is null || Note(unticked, wouldHaveTicked, learning) is not { } text) return null;
        var affected = unticked.Where(wouldHaveTicked).ToList();
        var bar = new Wpf.Ui.Controls.InfoBar { Message = text, IsOpen = true, IsClosable = true, Severity = Wpf.Ui.Controls.InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
        var reset = new Wpf.Ui.Controls.Button { Content = "Tick again from now on" };
        System.Windows.Automation.AutomationProperties.SetName(reset, "Forget the skips and tick these groups again from now on");
        UIElement? host = null;
        reset.Click += (_, _) =>
        {
            foreach (var g in affected)
            {
                try { learning.ResetSkip(g.FeatureId, g.Group.Id); }
                catch (Exception) { /* forgetting is best effort; the ticks below still apply */ }
            }
            retick(affected);
            if (host is FrameworkElement fe && fe.Parent is System.Windows.Controls.Panel p) p.Children.Remove(fe);
        };
        host = Controls.InfoBars.WithActions(bar, reset);
        return host;
    }
}
