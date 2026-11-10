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
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var circle = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28) };
        circle.SetResourceReference(Border.BackgroundProperty, s.IsUndone ? "ControlFillColorSecondaryBrush" : s.Failed > 0 && s.Quarantined + s.Moved + s.Changed == 0 ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush");
        var check = new SymbolIcon { Symbol = s.IsUndone ? SymbolRegular.ArrowUndo24 : SymbolRegular.Checkmark24, FontSize = 30, Filled = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        check.SetResourceReference(ForegroundProperty, s.IsUndone ? "TextFillColorPrimaryBrush" : "TextOnAccentFillColorPrimaryBrush");
        circle.Child = check;
        head.Children.Add(circle);
        var titles = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var headline = T(s.IsUndone ? "Everything was put back" : s.Headline, "BigNumber");
        if (headline.Text.Length > 22) headline.FontSize = 30; // "6.2 GB moved to quarantine" must fit the card
        titles.Children.Add(headline);
        titles.Children.Add(T(s.IsUndone
            ? "Undo finished. Your files and settings are as they were before this run."
            : $"Finished in {s.DurationText}" + (s.Quarantined > 0 ? " · nothing was deleted" : ""), "Secondary"));
        head.Children.Add(titles);
        stack.Children.Add(head);
        if (!s.IsUndone && s.SpaceNote is { } note) stack.Children.Add(T(note, "Secondary", new Thickness(74, 10, 0, 0)));
        // Clearly less than the confirm dialog said: say how much, and where the rest went.
        if (s.ShortfallLine is { } shortfall) stack.Children.Add(T(shortfall, "Secondary", new Thickness(74, 6, 0, 0)));
        AutomationProperties.SetName(this, s.IsUndone ? "Run undone" : "Run finished: " + s.Headline + ". " + s.SpaceNote + " " + s.ShortfallLine);

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

        string rp = s.RestorePoint != null || s.HadSystemChange ? " " + s.RestorePointLine.TrimEnd('.') + "." : "";
        if (!s.IsUndone && s.AnyReversible)
            stack.Children.Add(T("Undo puts everything back exactly as it was." + rp, "Caption", new Thickness(0, 10, 0, 0)));

        card.Child = stack;
        Content = card;
    }

    private static Border Tile(string number, string label)
    {
        var b = new Border();
        b.SetResourceReference(StyleProperty, "InnerTile");
        var sp = new StackPanel();
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
