using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Planning;
using PcTidy.Features.Insights.Scripts;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.Features.Insights.View;

/// <summary>
/// The Insights page: "Scan this script" on top, then a grid of read-only cards per category. Cards that don't apply say
/// "Not available on this PC". The only changes go through the standard confirm (IFlowHost): opening a page, OneDrive
/// "Free up space" and compressing an old folder, each ticked by hand.
/// </summary>
public partial class InsightsView : UserControl, IFixedHeightPage
{
    private readonly IServiceProvider _services;
    private readonly InsightsScanner _scanner;
    private CancellationTokenSource? _scanCts;
    private bool _running;

    public InsightsView(IServiceProvider services, InsightsScanner scanner)
    {
        InitializeComponent();
        _services = services;
        _scanner = scanner;
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    // ------------------------------------------------------------------ check this PC

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = StartScanAsync();

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>Reads everything and shows the cards (public for screenshots and Home quick actions).</summary>
    public async Task StartScanAsync()
    {
        if (_scanCts is not null || _running) return;
        _scanCts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ScanMessage.Text = "Starting…";
        ShowState(scanning: true);
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var progress = new Progress<ScanProgress>(p => ScanMessage.Text = p.Message);
            var report = await _scanner.ScanReportAsync(ctx, progress, _scanCts.Token).ConfigureAwait(true);
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
            ShowState(scanning: false);
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("Check didn't finish", "Something went wrong while reading this PC: " + ex.Message + " Nothing was changed.", isError: true);
            ShowState(scanning: false);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
        }
    }

    private void ShowState(bool scanning)
    {
        ScanningState.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ReadyState.Visibility = !scanning && Sections.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Sections.Visibility = !scanning && Sections.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Before the first check the big button in the card is the only one; afterwards the header offers "Check again".
        ScanButton.Visibility = Sections.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ShowReport(InsightsReport report)
    {
        Sections.Children.Clear();
        foreach (var group in report.Cards.Where(c => c.Id != InsightsIds.ScriptScan).GroupBy(c => c.Category))
        {
            var title = new TextBlock { Text = InsightsScanner.CategoryTitle(group.Key), Margin = new Thickness(0, 8, 0, 2) };
            title.SetResourceReference(StyleProperty, "SectionTitle");
            var note = new TextBlock { Text = InsightsScanner.CategoryDescription(group.Key), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(StyleProperty, "Caption");
            Sections.Children.Add(title);
            Sections.Children.Add(note);
            // Cards that apply, each as tall as its own content. Cards that can't be read here go in one compact list
            // below them, instead of tall, mostly empty boxes next to the long ones.
            var available = group.Where(c => c.IsAvailable).ToList();
            if (available.Count > 0)
            {
                var grid = new ResponsiveGrid { MinItemWidth = 300, MaxColumns = 3, Gap = 16, Margin = new Thickness(0, 0, 0, 16) };
                foreach (var card in available) grid.Children.Add(BuildCard(card));
                Sections.Children.Add(grid);
            }
            var unavailable = group.Where(c => !c.IsAvailable).ToList();
            if (unavailable.Count > 0) Sections.Children.Add(BuildNotAvailableList(unavailable));
        }
        ShowState(scanning: false);
    }

    // ------------------------------------------------------------------ one card

    private FrameworkElement BuildCard(InsightCard card)
    {
        var body = new StackPanel();
        // Top-aligned: a card is as tall as its own content, not the tallest card in its row.
        var border = new Border { Child = body, VerticalAlignment = VerticalAlignment.Top };
        border.SetResourceReference(StyleProperty, "TidyCard");
        AutomationProperties.SetName(border, $"{card.Title}: {card.Headline}");
        if (!card.IsAvailable) border.Opacity = 0.75;

        // Title row: icon, then the title (up to two lines, never cut to a few letters) with the status pill under it.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var iconTile = new Border { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        iconTile.SetResourceReference(StyleProperty, "IconTile");
        iconTile.Child = new SymbolIcon { Symbol = Enum.TryParse<SymbolRegular>(card.Icon, out var sym) ? sym : SymbolRegular.Info24, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(iconTile, Dock.Left);
        head.Children.Add(iconTile);
        var (pillText, tone) = Pill(card.Status);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = Text(card.Title, "CardTitle");
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.MaxHeight = 46;
        titleText.ToolTip = card.Title;
        titleStack.Children.Add(titleText);
        titleStack.Children.Add(new BadgePill { Text = pillText, Tone = tone, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        head.Children.Add(titleStack);
        body.Children.Add(head);

        body.Children.Add(Text(card.Headline, "BodyStrong"));
        body.Children.Add(Text(card.Explanation, "Secondary", top: 6));
        if (card.IsHeuristic) body.Children.Add(Text("Best guess: PC Tidy reads what Windows reports and can't be certain.", "Caption", top: 4));

        if (card.Facts.Count > 0)
        {
            var facts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < card.Facts.Count; i++)
            {
                facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Text(card.Facts[i].Label, "CaptionStrong", wrap: false);
                label.Margin = new Thickness(0, 0, 12, 2);
                var value = Text(card.Facts[i].Value, "Caption");
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                facts.Children.Add(label);
                facts.Children.Add(value);
            }
            body.Children.Add(facts);
        }

        if (card.IsAvailable) body.Children.Add(Text("What to do: " + card.WhatToDo, "Body", top: 10));
        if (card.Steps.Count > 0)
        {
            var steps = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < card.Steps.Count; i++) steps.Children.Add(Text($"{i + 1}. {card.Steps[i]}", "Secondary", top: 2));
            body.Children.Add(steps);
        }

        if (card.Items.Count > 0) body.Children.Add(BuildRows(card));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var link in card.Links)
            buttons.Children.Add(MakeButton(link.Label, SymbolRegular.Open24, () => _ = OpenLinkAsync(link)));
        if (card.Navigate is { } nav)
            buttons.Children.Add(MakeButton(nav.Label, SymbolRegular.ArrowRight24, () => Navigate(nav)));
        if (buttons.Children.Count > 0) body.Children.Add(buttons);
        return border;
    }

    private FrameworkElement BuildRows(InsightCard card)
    {
        var rows = card.Items.Select(i => new InsightRow(i)).ToList();
        var list = new ListBox
        {
            ItemsSource = rows, MaxHeight = 260, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = (DataTemplate)FindResource("InsightRowTemplate"),
        };
        AutomationProperties.SetName(list, card.Title + " items");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

        var review = new Button { Content = "Review ticked items", Appearance = ControlAppearance.Primary, IsEnabled = false, Margin = new Thickness(0, 8, 0, 0), Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 } };
        AutomationProperties.SetName(review, "Review the ticked items in " + card.Title);
        foreach (var r in rows)
            r.PropertyChanged += (_, _) =>
            {
                int n = rows.Count(x => x.IsChecked);
                review.IsEnabled = n > 0;
                review.Content = n > 0 ? $"Review {n} ticked" : "Review ticked items";
            };
        review.Click += (_, _) => _ = ReviewAsync(card, rows.Where(x => x.IsChecked).Select(x => x.Item).ToList());

        var panel = new StackPanel();
        panel.Children.Add(Text("Nothing is ticked for you.", "Caption", top: 10));
        panel.Children.Add(list);
        panel.Children.Add(review);
        return panel;
    }

    private async Task ReviewAsync(InsightCard card, IReadOnlyList<ScanItem> ticked)
    {
        if (Host is not { } host || ticked.Count == 0 || _running) return;
        _running = true;
        try
        {
            var plan = PlanBuilder.FromSelection(InsightsIds.FeatureId, card.Title, ticked);
            if (plan.Items.Count == 0) return;
            var run = await host.ReviewAndRunAsync(plan, CancellationToken.None).ConfigureAwait(true);
            if (run is not null) await StartScanAsync().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task OpenLinkAsync(InsightLink link)
    {
        if (Host is not { } host) return;
        await host.ReviewAndRunAsync(InsightPlans.OpenPlan(link.Target, link.Label), CancellationToken.None).ConfigureAwait(true);
    }

    private void Navigate(InsightNavigation nav)
    {
        if (Shell is { } shell) shell.NavigateTo(nav.ModuleId);
        else Host?.ShowMessage(nav.Label, "Open it from the menu on the left.");
    }

    private static (string, PillTone) Pill(InsightStatus s) => s switch
    {
        InsightStatus.Good => ("All good", PillTone.Success),
        InsightStatus.Attention => ("Worth a look", PillTone.Caution),
        InsightStatus.NotAvailable => ("Not available", PillTone.Neutral),
        InsightStatus.NeedsAdmin => ("Needs admin", PillTone.Neutral),
        _ => ("Info", PillTone.Info),
    };

    private static TextBlock Text(string text, string style, double top = 0, bool wrap = true)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.SetResourceReference(StyleProperty, style);
        return t;
    }

    /// <summary>One compact card for a category's "Not available on this PC" checks: the name and the reason, one line each.</summary>
    private FrameworkElement BuildNotAvailableList(IReadOnlyList<InsightCard> cards)
    {
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var pill = new BadgePill { Text = "Not available", Tone = PillTone.Neutral, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(Text(cards.Count == 1 ? "Couldn't be checked on this PC" : $"{cards.Count} checks couldn't be done on this PC", "BodyStrong"));
        stack.Children.Add(head);
        foreach (var card in cards)
        {
            var line = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            line.SetResourceReference(StyleProperty, "Secondary");
            var name = new System.Windows.Documents.Run(card.Title + ": ") { FontWeight = FontWeights.SemiBold };
            line.Inlines.Add(name);
            line.Inlines.Add(new System.Windows.Documents.Run(card.Explanation));
            AutomationProperties.SetName(line, $"{card.Title}: not available on this PC. {card.Explanation}");
            stack.Children.Add(line);
            if (card.Links.Count > 0)
            {
                var links = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var link in card.Links) links.Children.Add(LinkButton(link));
                stack.Children.Add(links);
            }
        }
        var border = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 16), Opacity = 0.85 };
        border.SetResourceReference(StyleProperty, "TidyCard");
        return border;
    }

    private Button LinkButton(InsightLink link) => MakeButton(link.Label, SymbolRegular.Open24, () => _ = OpenLinkAsync(link));

    private static Button MakeButton(string label, SymbolRegular icon, Action click)
    {
        // The label wraps inside the button rather than being cut mid-word in a narrow card.
        var content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        var b = new Button { Content = content, Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 6), Icon = new SymbolIcon { Symbol = icon }, HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------ scan this script

    private void OnPickScript(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a script to scan (PC Tidy reads it, it never runs it)",
            Filter = "Scripts (*.ps1;*.psm1;*.bat;*.cmd;*.reg;*.vbs;*.js)|*.ps1;*.psm1;*.bat;*.cmd;*.reg;*.vbs;*.js",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = ScanScriptAsync(dialog.FileName);
    }

    private void OnScriptDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && ScriptAnalyzer.LanguageOf(files[0]) is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnScriptDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = ScanScriptAsync(files[0]);
    }

    private async Task ScanScriptAsync(string path)
    {
        PickScriptButton.IsEnabled = false;
        try
        {
            var read = await ScriptFileReader.ReadAsync(path, CancellationToken.None).ConfigureAwait(true);
            if (read.Text is null)
            {
                Host?.ShowMessage("Couldn't scan that file", read.Problem ?? "PC Tidy couldn't read it.");
                return;
            }
            var report = await Task.Run(() => ScriptAnalyzer.Analyze(path, read.Text, read.Truncated)).ConfigureAwait(true);
            ShowScriptReport(report);
        }
        finally
        {
            PickScriptButton.IsEnabled = true;
        }
    }

    internal void ShowScriptReport(ScriptReport report)
    {
        var (text, tone) = report.Overall switch
        {
            ScriptOverall.Dangerous => ("Don't run this", PillTone.Critical),
            ScriptOverall.Unclear => ("Can't vouch for it", PillTone.Caution),
            ScriptOverall.MostlyPlacebo => ("Mostly placebo", PillTone.Neutral),
            ScriptOverall.KnownSettings => ("Known settings", PillTone.Success),
            _ => ("Nothing recognised", PillTone.Neutral),
        };
        ScriptVerdictPill.Text = text;
        ScriptVerdictPill.Tone = tone;
        ScriptFileName.Text = report.FileName + (report.Truncated ? " (only the first 2 MB was read)" : "");
        ScriptHeadline.Text = report.Headline;
        ScriptAdvice.Text = report.Advice;
        // Red flags first, then the rest in file order.
        FindingsList.ItemsSource = report.Findings.OrderByDescending(f => f.RedFlag).ThenBy(f => f.Line).Select(f => new FindingRow(f)).ToList();
        FindingsList.Visibility = report.Findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScriptResult.Visibility = Visibility.Visible;
    }
}
