using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Flow;

/// <summary>One step line in the simulation report.</summary>
public sealed class SimStepRow
{
    public SimStepRow(StepRecord step, string section)
    {
        Section = section;
        Description = step.Item.Description;
        WillRun = step.Status is StepStatus.Simulated or StepStatus.Done;
        Message = step.Message ?? (WillRun ? "Would run" : "Would be skipped");
        Size = step.Item.ExpectedSize > 0 ? Sizes.Format(step.Item.ExpectedSize) : "";
        Icon = !WillRun ? SymbolRegular.SubtractCircle24 : step.Item.NeedsAdmin ? SymbolRegular.Shield24 : !step.Item.Reversible ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24;
        Tags = string.Join(" · ", new[]
        {
            step.Item.NeedsAdmin ? "admin" : null,
            step.Item.Reversible ? null : "can't be undone",
            step.Item.IsSystemChange ? "system change" : null,
        }.Where(t => t != null));
        AutomationName = $"{Description}. {Message}. {Tags}";
    }

    public string Section { get; }
    public string Description { get; }
    public string Message { get; }
    public string Size { get; }
    public string Tags { get; }
    public bool WillRun { get; }
    public SymbolRegular Icon { get; }
    public string AutomationName { get; }
}

/// <summary>
/// "What would happen" report (Simulation mode): every step, what would be skipped and why,
/// admin prompts and restore point. Nothing was changed.
/// </summary>
public sealed class SimulationReportView : UserControl
{
    private readonly System.Windows.Controls.ListBox _list = new() { MinHeight = 120 };
    private readonly StackPanel _header = new();
    private readonly Button _runReal = new() { Content = "Run it for real", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24), Height = 40, MinWidth = 160 };
    private readonly Button _back = new() { Content = "Back to the list", Appearance = ControlAppearance.Secondary, Icon = new SymbolIcon(SymbolRegular.ArrowLeft24), Height = 40, Margin = new Thickness(0, 0, 8, 0) };

    public SimulationReportView()
    {
        _list.SetResourceReference(StyleProperty, "TidyFlatList");
        _list.ItemTemplate = BuildTemplate();
        AutomationProperties.SetName(_list, "Simulated steps");
        AutomationProperties.SetName(_runReal, "Run it for real");
        AutomationProperties.SetName(_back, "Back to the list");
        _runReal.Click += (_, _) => RunForRealRequested?.Invoke();
        _back.Click += (_, _) => BackRequested?.Invoke();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_header);

        var listCard = new Border { Padding = new Thickness(4, 4, 4, 4), Margin = new Thickness(0, 12, 0, 0) };
        listCard.SetResourceReference(StyleProperty, "TidyCard");
        listCard.Padding = new Thickness(6, 4, 2, 4);
        listCard.Child = _list;
        Grid.SetRow(listCard, 1);
        root.Children.Add(listCard);

        var bar = new Border();
        bar.SetResourceReference(StyleProperty, "StickyBar");
        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_back);
        buttons.Children.Add(_runReal);
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var note = new TextBlock { Text = "This was a simulation. Nothing on your PC was changed.", VerticalAlignment = VerticalAlignment.Center };
        note.SetResourceReference(StyleProperty, "BodyStrong");
        dock.Children.Add(note);
        bar.Child = dock;
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);
        Content = root;
    }

    public event Action? RunForRealRequested;
    public event Action? BackRequested;

    /// <summary>Hide "Run it for real" (e.g. when shown in a dialog).</summary>
    public bool ShowActions { set => ((FrameworkElement)((Grid)Content).Children[2]).Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public void Show(IReadOnlyList<RunRecord> runs)
    {
        var steps = runs.SelectMany(r => r.Steps.Select(s => new SimStepRow(s, r.Title))).ToList();
        _list.ItemsSource = steps;

        // Same counting as the bottom bar and the confirm dialog (plan items, housekeeping left out).
        var counted = runs.SelectMany(r => r.Steps).Where(s => PlanFacts.Runs(s.Item)).ToList();
        var wouldRun = counted.Where(s => s.Status is StepStatus.Simulated or StepStatus.Done).ToList();
        var c = PlanCounts.Of(wouldRun.Select(s => s.Item));
        int run = wouldRun.Count, skip = counted.Count - run;
        int admin = c.Admin;
        bool rp = c.SystemChange;
        int permanent = c.Permanent;
        var (mainNumber, mainCaption) = c.CleanBytes > 0 ? (Sizes.Format(c.CleanBytes), $"would be cleaned ({PlanFacts.Items(c.Quarantine + c.Permanent)})")
            : c.Moves > 0 ? (c.Moves.ToString("N0"), c.Moves == 1 ? "file would be sorted" : "files would be sorted")
            : c.OnlyOpens ? (c.Opens.ToString("N0"), c.Opens == 1 ? "page would open" : "pages would open")
            : (c.Total.ToString("N0"), c.Total == 1 ? "change would be made" : "changes would be made");

        _header.Children.Clear();
        var info = new InfoBar
        {
            Title = "Simulation: nothing was changed.",
            Message = "This is exactly what would happen if you ran it now. Every step was checked the same way a real run checks it.",
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
        };
        _header.Children.Add(info);

        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
        tiles.Children.Add(Tile(PlanFacts.PrimaryIcon(c), mainNumber, mainCaption));
        tiles.Children.Add(Tile(SymbolRegular.SubtractCircle24, skip.ToString("N0"), skip == 1 ? "step would be skipped" : "steps would be skipped"));
        tiles.Children.Add(Tile(SymbolRegular.Shield24, admin > 0 ? "Asks once" : "Not needed", admin > 0 ? $"admin for {admin} step{PlanFacts.S(admin)}" : "no admin prompt"));
        tiles.Children.Add(Tile(SymbolRegular.ShieldCheckmark24, rp ? "Made first" : "Not needed", rp ? "restore point" : permanent > 0 ? $"{permanent} can't be undone" : "everything undoable"));
        for (int i = 0; i < tiles.Children.Count; i++)
            ((FrameworkElement)tiles.Children[i]).Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 3 ? 0 : 5, 0);
        _header.Children.Add(tiles);
        var totals = new TextBlock
        {
            Text = "Would do: " + PlanFacts.TotalsLine(c) + (skip > 0 ? $" · {skip} skipped" : ""),
            Margin = new Thickness(2, 10, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        totals.SetResourceReference(StyleProperty, "SecondaryTrim");
        _header.Children.Add(totals);
        AutomationProperties.SetName(this, $"Simulation report: {totals.Text}");
    }

    private static Border Tile(SymbolRegular icon, string number, string caption)
    {
        var b = new Border();
        b.SetResourceReference(StyleProperty, "StatTile");
        var dock = new DockPanel();
        var si = new SymbolIcon { Symbol = icon, FontSize = 22, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        si.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        dock.Children.Add(si);
        var sp = new StackPanel();
        var n = new TextBlock { Text = number };
        n.SetResourceReference(StyleProperty, "StatNumber");
        var c = new TextBlock { Text = caption };
        c.SetResourceReference(StyleProperty, "Caption");
        sp.Children.Add(n);
        sp.Children.Add(c);
        dock.Children.Add(sp);
        b.Child = dock;
        return b;
    }

    private static DataTemplate BuildTemplate()
    {
        // Built in code so the view has no XAML dependency; columns: icon · description + reason · tags · size.
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(MarginProperty, new Thickness(12, 7, 12, 7));
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(84) })
        {
            var cd = new FrameworkElementFactory(typeof(ColumnDefinition));
            cd.SetValue(ColumnDefinition.WidthProperty, w);
            grid.AppendChild(cd);
        }
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(SimStepRow.Icon)));
        icon.SetValue(SymbolIcon.FontSizeProperty, 16.0);
        icon.SetValue(MarginProperty, new Thickness(0, 2, 12, 0));
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        icon.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        grid.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(Grid.ColumnProperty, 1);
        var d = new FrameworkElementFactory(typeof(TextBlock));
        d.SetBinding(TextBlock.TextProperty, new Binding(nameof(SimStepRow.Description)));
        d.SetResourceReference(StyleProperty, "Body");
        d.SetValue(TextBlock.FontSizeProperty, 13.5);
        text.AppendChild(d);
        var m = new FrameworkElementFactory(typeof(TextBlock));
        m.SetBinding(TextBlock.TextProperty, new Binding(nameof(SimStepRow.Message)));
        m.SetResourceReference(StyleProperty, "SecondaryTrim");
        m.SetValue(TextBlock.FontSizeProperty, 12.5);
        text.AppendChild(m);
        grid.AppendChild(text);

        var tags = new FrameworkElementFactory(typeof(TextBlock));
        tags.SetValue(Grid.ColumnProperty, 2);
        tags.SetBinding(TextBlock.TextProperty, new Binding(nameof(SimStepRow.Tags)));
        tags.SetResourceReference(StyleProperty, "Caption");
        tags.SetValue(MarginProperty, new Thickness(12, 0, 12, 0));
        tags.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(tags);

        var size = new FrameworkElementFactory(typeof(TextBlock));
        size.SetValue(Grid.ColumnProperty, 3);
        size.SetBinding(TextBlock.TextProperty, new Binding(nameof(SimStepRow.Size)));
        size.SetResourceReference(StyleProperty, "SizeTextStyle");
        size.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
        size.SetValue(TextBlock.FontSizeProperty, 13.0);
        grid.AppendChild(size);

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        border.AppendChild(grid);
        return new DataTemplate(typeof(SimStepRow)) { VisualTree = border };
    }
}
