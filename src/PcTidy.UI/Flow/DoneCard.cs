using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Flow;

/// <summary>
/// Done card: what was freed / moved / changed, what was skipped and why, and a big "Undo this run" button.
/// For an Optimize-everything run pass all section records: the button becomes "Undo this whole run".
/// </summary>
public sealed class DoneCard : UserControl
{
    private readonly int _quarantineDays;
    private IReadOnlyList<RunRecord> _runs = Array.Empty<RunRecord>();
    private readonly Button _undo = new() { Height = 44, MinWidth = 200, FontSize = 15, FontWeight = FontWeights.SemiBold, Appearance = ControlAppearance.Secondary, Icon = new SymbolIcon(SymbolRegular.ArrowUndo24) };

    public DoneCard(int quarantineDays = 7)
    {
        _quarantineDays = quarantineDays;
        _undo.Click += (_, _) => UndoRequested?.Invoke(_runs);
        Focusable = false;
    }

    /// <summary>The user wants these runs undone (the page forwards to IShellFlowHost.UndoRunsAsync).</summary>
    public event Action<IReadOnlyList<RunRecord>>? UndoRequested;
    public event Action? HistoryRequested;
    /// <summary>
    /// "Try the admin steps again": the steps skipped only because the Windows admin prompt was declined, timed out or
    /// the helper couldn't start (<see cref="AdminRetry"/>), as plans for the standard confirm.
    /// </summary>
    public event Action<IReadOnlyList<ActionPlan>>? TryAdminAgainRequested;
    /// <summary>"Check if this helped" was clicked (raised before Measure opens, so a dialog around the card can close first).</summary>
    public event Action<RunCheckRequest>? CheckRequested;

    private IRunCheckLauncher? _runCheck;

    /// <summary>
    /// Opens Measure for "Check if this helped" (from the shell's services). Null, or not available, hides the link;
    /// it is shown only for runs from Tweaks, Games, Speed and Services (<see cref="RunCheck.For"/>).
    /// </summary>
    public IRunCheckLauncher? RunCheckLauncher
    {
        get => _runCheck;
        set { _runCheck = value; Build(); }
    }

    /// <summary>The run "Check if this helped" opens, or null when the link is hidden.</summary>
    public RunCheckRequest? CheckRequest => new RunSummary(_runs).IsUndone ? null : RunCheck.For(_runs, _runCheck?.IsAvailable == true);

    public IReadOnlyList<RunRecord> Runs
    {
        get => _runs;
        set { _runs = value; _beforeAfter = Array.Empty<string>(); Build(); }
    }

    private IReadOnlyList<string> _beforeAfter = Array.Empty<string>();

    /// <summary>Measured before/after lines (set after <see cref="Runs"/>); empty = the section isn't shown.</summary>
    public IReadOnlyList<string> BeforeAfter
    {
        get => _beforeAfter;
        set { _beforeAfter = value; Build(); }
    }

    /// <summary>After a successful undo: switch the card to the "put back" state.</summary>
    public void MarkUndone()
    {
        _runs = _runs.Select(r => r with { Status = RunStatus.Undone }).ToList();
        Build();
    }

    private void Build()
    {
        var s = new RunSummary(_runs);
        var card = new Border { Padding = new Thickness(28, 24, 28, 24), VerticalAlignment = VerticalAlignment.Top };
        card.SetResourceReference(StyleProperty, "TidyCard");
        var stack = new StackPanel();

        // Headline
        // DockPanel, not a horizontal StackPanel: the titles get the card's remaining width, so a long headline wraps
        // instead of running off the edge (a long "moved to quarantine" headline was cut off in 3.1.2).
        var head = new DockPanel { LastChildFill = true };
        var circle = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28) };
        circle.SetResourceReference(Border.BackgroundProperty, s.IsUndone ? "ControlFillColorSecondaryBrush" : s.Failed > 0 && s.Quarantined + s.Moved + s.Changed == 0 ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush");
        var check = new SymbolIcon { Symbol = s.IsUndone ? SymbolRegular.ArrowUndo24 : SymbolRegular.Checkmark24, FontSize = 30, Filled = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        check.SetResourceReference(ForegroundProperty, s.IsUndone ? "TextFillColorPrimaryBrush" : "TextOnAccentFillColorPrimaryBrush");
        circle.Child = check;
        circle.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(circle, Dock.Left);
        head.Children.Add(circle);
        var titles = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // After Undo, a run that also deleted something for good doesn't claim everything came back.
        var headline = T(s.IsUndone ? (s.PermanentDone > 0 ? "Put back all that could be" : "Everything was put back") : s.Headline, "BigNumber");
        if (headline.Text.Length > 22) headline.FontSize = 30; // "6.2 GB moved to quarantine" must fit the card
        titles.Children.Add(headline);
        // What else happened to space, above all what can't be undone: its own line, never squeezed into the headline.
        if (!s.IsUndone && s.HeadlineDetail is { } detail)
        {
            var second = T(detail, "BodyStrong", new Thickness(0, 2, 0, 2));
            second.FontSize = 18;
            titles.Children.Add(second);
        }
        titles.Children.Add(T(s.IsUndone
            ? (s.PermanentDone > 0
                ? $"Undo finished. What can't be undone ({PlanFacts.Items(s.PermanentDone)}) stays as it is now; everything else is as it was."
                : "Undo finished. Your files and settings are as they were before this run.")
            : $"Finished in {s.DurationText}" + (s.PermanentDone > 0 ? $" · {PlanFacts.Items(s.PermanentDone)} can't be undone" : s.Quarantined > 0 ? " · nothing was deleted" : ""), "Secondary"));
        head.Children.Add(titles);
        stack.Children.Add(head);
        if (!s.IsUndone && s.SpaceNote is { } note) stack.Children.Add(T(note, "Secondary", new Thickness(74, 10, 0, 0)));
        // Clearly less than the confirm dialog said: say how much, and where the rest went.
        if (s.ShortfallLine is { } shortfall) stack.Children.Add(T(shortfall, "Secondary", new Thickness(74, 6, 0, 0)));
        AutomationProperties.SetName(this, s.IsUndone ? "Run undone"
            : string.Join(" ", new[] { "Run finished: " + s.FullHeadline + ".", s.PermanentDone > 0 ? s.UndoNote + "." : null, s.SpaceNote, s.ShortfallLine }.Where(x => !string.IsNullOrEmpty(x))));

        // Tiles: "48 of 49" when something was skipped, so the numbers match the confirm dialog.
        var p = s.Planned;
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 22, 0, 0) };
        if (p.Quarantine > 0) tiles.Children.Add(Tile(RunSummary.OfPlanned(s.Quarantined, p.Quarantine), "moved to quarantine"));
        if (p.Moves > 0) tiles.Children.Add(Tile(RunSummary.OfPlanned(s.Moved, p.Moves), p.Moves == 1 ? "file moved" : "files moved"));
        if (p.Startup > 0) tiles.Children.Add(Tile(RunSummary.OfPlanned(s.StartupChanged, p.Startup), p.Startup == 1 ? "startup item changed" : "startup items changed"));
        if (p.Settings > 0) tiles.Children.Add(Tile(RunSummary.OfPlanned(s.SettingsChanged, p.Settings), p.Settings == 1 ? "setting changed" : "settings changed"));
        if (p.Permanent > 0) tiles.Children.Add(Tile(RunSummary.OfPlanned(s.PermanentDone, p.Permanent), "done for good"));
        tiles.Children.Add(Tile(s.Skipped.ToString("N0"), "skipped"));
        if (s.AnyReversible && tiles.Children.Count < 4)
        {
            var until = DateTime.Now.AddDays(_quarantineDays).ToString("d MMM", CultureInfo.CurrentCulture);
            tiles.Children.Add(Tile($"{_quarantineDays} days", $"to undo (until {until})"));
        }
        // One row (up to 5 tiles); more than that wraps into rows of 3 with the same gaps.
        int n = tiles.Children.Count, cols = n <= 5 ? Math.Max(1, n) : 3;
        tiles.Columns = cols;
        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            ((FrameworkElement)tiles.Children[i]).Margin = new Thickness(col == 0 ? 0 : 4, i < cols ? 0 : 8, col == cols - 1 ? 0 : 4, 0);
        }
        stack.Children.Add(tiles);

        // Skips
        if (s.Skipped > 0)
        {
            stack.Children.Add(new InfoBar
            {
                Title = $"{s.Skipped} skipped.",
                Message = s.SkipLine + ". Skipped items were left exactly as they were.",
                IsOpen = true,
                IsClosable = false,
                Severity = s.Failed > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                Margin = new Thickness(0, 16, 0, 0),
            });
            // Admin steps that never reached the admin helper: one button runs just those again (standard confirm first).
            var retry = s.IsUndone ? Array.Empty<ActionPlan>() : AdminRetry.Plans(_runs);
            int retrySteps = retry.Sum(x => x.Items.Count);
            // Plans (and their run ids) are made fresh on every click: a simulation of them must not use up the id.
            if (retrySteps > 0)
            {
                var again = new Button
                {
                    Content = $"Try the {retrySteps} admin step{PlanFacts.S(retrySteps)} again",
                    Icon = new SymbolIcon(SymbolRegular.ShieldKeyhole24),
                    Appearance = ControlAppearance.Primary,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                AutomationProperties.SetName(again, (string)again.Content);
                AutomationProperties.SetHelpText(again, AdminRetry.Hint);
                again.Click += (_, _) => TryAdminAgainRequested?.Invoke(AdminRetry.Plans(_runs));
                stack.Children.Add(again);
                stack.Children.Add(T(AdminRetry.Hint, "Caption", new Thickness(0, 4, 0, 0)));
            }
        }

        // Before / after (only what was measured both times)
        if (!s.IsUndone && _beforeAfter.Count > 0)
        {
            stack.Children.Add(T("Before and after", "CardTitle", new Thickness(0, 18, 0, 4)));
            foreach (var line in _beforeAfter.Take(6)) stack.Children.Add(T("• " + line, "Secondary", new Thickness(0, 2, 0, 0)));
        }

        // Actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        _undo.Content = _runs.Count > 1 ? "Undo this whole run" : "Undo this run";
        AutomationProperties.SetName(_undo, (string)_undo.Content);
        _undo.Visibility = s.AnyReversible && !s.IsUndone ? Visibility.Visible : Visibility.Collapsed;
        if (_undo.Parent is Panel old) old.Children.Remove(_undo);
        actions.Children.Add(_undo);
        var history = new System.Windows.Controls.Button { Content = "Open in History", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        history.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetName(history, "Open in History");
        history.Click += (_, _) => HistoryRequested?.Invoke();
        actions.Children.Add(history);
        stack.Children.Add(actions);

        // "Check if this helped": Tweaks, Games, Speed and Services runs, when this build has Measure.
        if (CheckRequest is { } runCheck)
        {
            var link = new System.Windows.Controls.Button { Content = RunCheck.LinkText, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            link.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(link, RunCheck.LinkText + ": open Measure for this run");
            AutomationProperties.SetHelpText(link, RunCheck.LinkCaption);
            link.Click += (_, _) =>
            {
                CheckRequested?.Invoke(runCheck);
                _runCheck?.Open(runCheck);
            };
            stack.Children.Add(link);
            stack.Children.Add(T(RunCheck.LinkCaption, "Caption", new Thickness(0, 2, 0, 0)));
        }

        string rp = s.RestorePoint != null || s.HadSystemChange ? " " + s.RestorePointLine.TrimEnd('.') + "." : "";
        if (!s.IsUndone && s.AnyReversible)
            stack.Children.Add(T((s.PermanentDone > 0
                ? (s.BytesPermanent > 0 ? "Undo puts everything else back exactly as it was; what was deleted for good can't come back."
                    : "Undo puts everything else back exactly as it was; the steps that can't be undone stay as they are.")
                : "Undo puts everything back exactly as it was.") + rp, "Caption", new Thickness(0, 10, 0, 0)));

        card.Child = stack;
        Content = card;
    }

    private static Border Tile(string number, string label)
    {
        var b = new Border();
        b.SetResourceReference(StyleProperty, "InnerTile");
        var sp = new StackPanel();
        // "48 of 49": the big number stays short (one line), "of 49" leads the label underneath.
        int of = number.IndexOf(" of ", StringComparison.Ordinal);
        if (of > 0) { label = number[(of + 1)..] + " " + label; number = number[..of]; }
        sp.Children.Add(T(number, "StatNumber"));
        sp.Children.Add(T(label, "Secondary"));
        b.Child = sp;
        return b;
    }

    private static TextBlock T(string text, string style, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        if (margin is { } m) t.Margin = m;
        return t;
    }
}

/// <summary>Admin steps a run skipped because the admin helper wasn't there (<see cref="PcTidy.Core.AdminSteps"/>), as plans to try again.</summary>
public static class AdminRetry
{
    public const string Hint = "Windows asks for admin rights once. Click Yes in its prompt; nothing else from this run is repeated.";

    /// <summary>
    /// One plan per run that has such steps, with only those steps, in their original order. A step already tried
    /// again in a later run on the same card (same item id, any other result) isn't offered twice.
    /// </summary>
    public static IReadOnlyList<ActionPlan> Plans(IReadOnlyList<RunRecord> runs)
    {
        var handled = runs.SelectMany(r => r.Steps).Where(st => !PcTidy.Core.AdminSteps.CanTryAgain(st)).Select(st => st.Item.Id).ToHashSet(StringComparer.Ordinal);
        var offered = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<ActionPlan>();
        foreach (var run in runs.Where(r => r.Mode == ExecutionMode.Real).OrderByDescending(r => r.CreatedUtc))
        {
            var items = run.Steps.Where(PcTidy.Core.AdminSteps.CanTryAgain).Select(st => st.Item)
                .Where(i => !handled.Contains(i.Id) && offered.Add(i.Id)).ToList();
            if (items.Count > 0)
                plans.Add(new ActionPlan { FeatureId = run.FeatureId, Title = run.Title, Items = items });
        }
        plans.Reverse();
        // Sections of one combined run share a new parent, so the confirm and History show them as one run again.
        if (plans.Count > 1)
        {
            var parent = ActionPlan.NewRunId();
            plans = plans.Select(p => p with { ParentRunId = parent }).ToList();
        }
        return plans;
    }
}
