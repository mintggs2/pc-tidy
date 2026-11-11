using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.UI.Controls;
using Wpf.Ui.Controls;

namespace PcTidy.Features.PcCare.View;

/// <summary>
/// PC care > Fixes (build list B5): 3-column cards, each with a one-line description, what it changes, whether undo
/// exists and a Run button. Run builds a one-item plan and hands it to the page, which shows the standard confirm
/// (IFlowHost.ReviewAndRunAsync). No toggles. While one fix runs its button becomes a spinner and the others are disabled
/// with "Available when the current task finishes".
/// </summary>
public partial class FixesPanel : UserControl
{
    private const string TaskName = "fix";

    private readonly Dictionary<string, (Wpf.Ui.Controls.Button Run, FrameworkElement Spinner, System.Windows.Controls.TextBlock Busy)> _cards = new();
    private LongTaskGate _gate = new();
    private Func<ActionPlan, Task<bool>>? _review;
    private string? _running;

    public FixesPanel()
    {
        InitializeComponent();
        NetworkNote.Text = FixRecipes.NetworkResetNote;
        foreach (var recipe in FixRecipes.All) CardsGrid.Children.Add(Card(recipe));
    }

    /// <param name="review">Shows the standard confirm and runs the plan; true when it ran (false = cancelled).</param>
    internal void Init(LongTaskGate gate, Func<ActionPlan, Task<bool>> review)
    {
        _gate.Changed -= UpdateButtons;
        _gate = gate;
        _gate.Changed += UpdateButtons;
        _review = review;
        UpdateButtons();
    }

    private Border Card(FixRecipe recipe)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "TidyCard");
        var dock = new DockPanel { LastChildFill = true };

        // Bottom: the Run button, its spinner, and the busy reason.
        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var run = new Wpf.Ui.Controls.Button
        {
            Content = recipe.RunLabel, Appearance = ControlAppearance.Secondary, Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(run, $"{recipe.RunLabel}: {recipe.Title}");
        run.Click += (_, _) => _ = RunAsync(recipe);
        var spinner = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        spinner.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
        var runningText = new System.Windows.Controls.TextBlock { Text = "Running…", VerticalAlignment = VerticalAlignment.Center };
        runningText.SetResourceReference(StyleProperty, "Body");
        spinner.Children.Add(runningText);
        AutomationProperties.SetName(spinner, recipe.Title + " is running");
        var busy = new System.Windows.Controls.TextBlock { Text = LongTaskGate.BusyText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        busy.SetResourceReference(StyleProperty, "Caption");
        busy.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        bottom.Children.Add(run);
        bottom.Children.Add(spinner);
        bottom.Children.Add(busy);
        dock.Children.Add(bottom);

        var body = new StackPanel();
        body.Children.Add(Text(recipe.Title, "CardTitle"));
        body.Children.Add(Text(recipe.Description, "Body", top: 4));
        var flags = FixRecipes.Flags(recipe);
        if (flags.Count > 0)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var f in flags) pills.Children.Add(new BadgePill { Text = f, Tone = PillTone.Caution, Margin = new Thickness(0, 0, 6, 4) });
            body.Children.Add(pills);
        }
        body.Children.Add(Text("What it changes", "CaptionStrong", top: 8));
        body.Children.Add(Text(recipe.WhatItChanges, "Caption"));
        body.Children.Add(Text("Undo", "CaptionStrong", top: 6));
        body.Children.Add(Text(recipe.Undo, "Caption"));
        if (recipe.Guidance.Count > 0)
        {
            body.Children.Add(Text("Also try", "CaptionStrong", top: 6));
            foreach (var g in recipe.Guidance) body.Children.Add(Text("• " + g, "Caption"));
        }
        dock.Children.Add(body);

        card.Child = dock;
        AutomationProperties.SetName(card, recipe.Title + ": " + recipe.Description);
        _cards[recipe.Id] = (run, spinner, busy);
        return card;
    }

    private static System.Windows.Controls.TextBlock Text(string text, string style, double top = 0)
    {
        var t = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(StyleProperty, style);
        // Caption-sized facts ("What it changes", "Undo") use the secondary brush so they keep 4.5:1 (UI review H1).
        if (style.StartsWith("Caption", StringComparison.Ordinal)) t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private void UpdateButtons()
    {
        foreach (var (id, (run, spinner, busy)) in _cards)
        {
            bool mine = _running == id;
            run.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
            spinner.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            run.IsEnabled = _review is not null && !_gate.IsBusy;
            busy.Visibility = _gate.IsBusy && !mine ? Visibility.Visible : Visibility.Collapsed;
            busy.Text = _gate.Reason;
            run.ToolTip = _gate.IsBusy && !mine ? _gate.Reason : null;
            ToolTipService.SetShowOnDisabled(run, true);
            AutomationProperties.SetHelpText(run, _gate.IsBusy ? _gate.Reason : "");
        }
    }

    /// <summary>One-item plan → the page's standard confirm → run. Never runs without the confirm.</summary>
    internal async Task RunAsync(FixRecipe recipe)
    {
        if (_review is null || !_gate.TryEnter(TaskName)) return;
        _running = recipe.Id;
        UpdateButtons();
        try
        {
            await _review(FixRecipes.PlanFor(recipe)).ConfigureAwait(true);
        }
        finally
        {
            _running = null;
            _gate.Exit(TaskName);
            UpdateButtons();
        }
    }
}
