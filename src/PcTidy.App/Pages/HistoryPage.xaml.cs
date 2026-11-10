using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.App.Composition;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages;

/// <summary>One History row: a single run, or all sections of one Optimize-everything run.</summary>
public sealed class RunGroupRow : ListRow
{
    public RunGroupRow(IReadOnlyList<RunRecord> runs, SymbolRegular icon, IReadOnlySet<string> unfinishedIds)
    {
        Runs = runs;
        Icon = icon;
        var first = runs.OrderBy(r => r.CreatedUtc).First();
        Summary = new RunSummary(runs);
        Title = runs.Count > 1 ? "Optimize & Organize everything" : first.Title;
        if (first.Mode == ExecutionMode.Simulate) Title += " (simulation)";
        Unfinished = runs.FirstOrDefault(r => r.Status == RunStatus.Interrupted || unfinishedIds.Contains(r.RunId));
        var bits = new List<string>();
        if (Unfinished != null) bits.Add("Stopped part way");
        bits.Add(Summary.IsUndone ? "Put back" : Summary.Headline);
        if (runs.Count > 1) bits.Add($"{runs.Count} sections");
        if (Summary.Skipped > 0) bits.Add($"{Summary.Skipped} skipped");
        Detail = string.Join(" · ", bits);
        When = RunSummary.When(first.CreatedUtc);
        var status = Unfinished != null ? RunStatus.Interrupted
            : runs.All(r => r.Status == RunStatus.Undone) ? RunStatus.Undone
            : runs.Any(r => r.Status == RunStatus.Failed) ? RunStatus.Failed
            : runs.Any(r => r.Status == RunStatus.CompletedWithSkips) ? RunStatus.CompletedWithSkips
            : first.Status;
        Status = RunSummary.StatusText(status);
        CanUndo = first.Mode == ExecutionMode.Real && Summary.AnyReversible && !Summary.IsUndone && Unfinished is null;
        SelectionRadius = new CornerRadius(0);
    }

    public IReadOnlyList<RunRecord> Runs { get; }
    public RunSummary Summary { get; }
    public SymbolRegular Icon { get; }
    public string Title { get; }
    public string Detail { get; }
    public string When { get; }
    public string Status { get; }
    public bool CanUndo { get; }
    /// <summary>The run that stopped part way (crash, power cut): it offers Resume / Roll back instead of Undo.</summary>
    public RunRecord? Unfinished { get; }
    public bool IsUnfinished => Unfinished != null;
    public bool ShowsUndo => !IsUnfinished;
    public string FeatureTitle => Runs.Count > 1 ? "Optimize" : Runs[0].Title;
    public string UndoAutomationName => $"Undo {Title}, {When}";
    public string ResumeAutomationName => $"Resume {Title}: finish the steps that didn't run";
    public string RollbackAutomationName => $"Roll back {Title}: undo the steps that already ran";
    public override string AutomationName => $"{Title}, {When}, {Detail}, {Status}";
}

/// <summary>History: every run with Undo, details and log; unfinished runs offer Resume / Roll back.</summary>
public partial class HistoryPage : UserControl, IFixedHeightPage
{
    private readonly AppServices _services;
    private List<RunGroupRow> _all = new();
    private List<string> _filters = new();
    private bool _refreshing;

    public HistoryPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Filter.SelectionChanged += (_, _) => ApplyFilter();
        // Narrow windows: a slimmer details card leaves the run titles room next to their buttons.
        SizeChanged += (_, e) => DetailColumn.Width = new GridLength(e.NewSize.Width < 900 ? 256 : 300);
        Loaded += (_, _) => RefreshAsync().Forget(Host, "Couldn't read History");
    }

    private FlowHost Host => (FlowHost)_services.Get<IShellFlowHost>();
    private MainWindow? Window => System.Windows.Window.GetWindow(this) as MainWindow;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await LoadAsync(); }
        finally { _refreshing = false; }
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<RunRecord> runs, unfinished;
        try
        {
            // The journal reads files: keep that off the UI thread (until the engine's reads are truly async).
            var journal = _services.Get<IRunJournal>();
            (runs, unfinished) = await Task.Run(async () => (await journal.ListAsync(CancellationToken.None), await journal.FindUnfinishedAsync(CancellationToken.None)));
        }
        catch (Exception ex)
        {
            Host.ShowMessage("Couldn't read History", ex.Message, isError: true);
            return;
        }

        ShowUnfinished(unfinished);
        var unfinishedIds = unfinished.Select(r => r.RunId).ToHashSet();
        var modules = _services.Modules;
        SymbolRegular IconFor(RunRecord r) => r.ParentRunId != null ? SymbolRegular.Sparkle24
            : modules.FirstOrDefault(m => m.Id == r.FeatureId || m.Scanners.Any(s => s.FeatureId == r.FeatureId)) is { } m ? MainWindow.IconOf(m)
            : GroupIcons.For(r.FeatureId);

        // Sections of one Optimize-everything run share a ParentRunId: they are one row with one Undo.
        // Simulations changed nothing, so they aren't history.
        _all = runs.Where(r => r.Mode == ExecutionMode.Real).GroupBy(r => r.ParentRunId ?? r.RunId)
                   .Select(g => new RunGroupRow(g.ToList(), IconFor(g.First()), unfinishedIds))
                   .OrderByDescending(r => r.Runs.Max(x => x.CreatedUtc))
                   .ToList();

        var keep = Filter.SelectedText;
        _filters = new[] { "All runs" }.Concat(_all.Select(r => r.FeatureTitle).Distinct().Take(6)).ToList();
        Filter.Items = string.Join("|", _filters);
        Filter.SelectedIndex = Math.Max(0, _filters.IndexOf(keep));
        ApplyFilter();
    }

    /// <summary>One banner per unfinished run, with Resume / Roll back right under it (InfoBar can't host buttons).</summary>
    private void ShowUnfinished(IReadOnlyList<RunRecord> unfinished)
    {
        UnfinishedPanel.Children.Clear();
        UnfinishedPanel.Visibility = unfinished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var run in unfinished)
        {
            int done = run.Steps.Count(s => s.Status == StepStatus.Done);
            var bar = new InfoBar
            {
                Title = $"\"{run.Title}\" didn't finish.",
                Message = $"It stopped after step {done} of {run.Steps.Count} (maybe the PC turned off). Finish it, or roll back what was done. Every step knows whether it already happened.",
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Margin = new Thickness(0, 0, 0, 10),
            };
            UnfinishedPanel.Children.Add(InfoBars.WithActions(bar, ResumeButton(run), RollbackButton(run)));
        }
    }

    private Wpf.Ui.Controls.Button ResumeButton(RunRecord run)
    {
        var b = new Wpf.Ui.Controls.Button { Content = "Resume", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24) };
        AutomationProperties.SetName(b, $"Resume {run.Title}: finish the steps that didn't run");
        b.Click += (_, _) => ResumeAsync(run).Forget(Host, "Couldn't resume");
        return b;
    }

    private Wpf.Ui.Controls.Button RollbackButton(RunRecord run)
    {
        var b = new Wpf.Ui.Controls.Button { Content = "Roll back", Icon = new SymbolIcon(SymbolRegular.ArrowUndo24) };
        AutomationProperties.SetName(b, $"Roll back {run.Title}: undo the steps that already ran");
        b.Click += (_, _) => RollbackAsync(run).Forget(Host, "Couldn't roll back");
        return b;
    }

    private async Task ResumeAsync(RunRecord run)
    {
        if (await Host.ResumeAsync(run)) await RefreshAsync();
    }

    private async Task RollbackAsync(RunRecord run)
    {
        if (await Host.RollbackAsync(run)) await RefreshAsync();
    }

    private void ApplyFilter()
    {
        var filter = Filter.SelectedIndex > 0 && Filter.SelectedIndex < _filters.Count ? _filters[Filter.SelectedIndex] : null;
        var q = SearchBox.Text?.Trim() ?? "";
        var rows = _all.Where(r => filter == null || r.FeatureTitle == filter)
                       .Where(r => q.Length == 0 || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Detail.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                   || r.Runs.Any(x => x.Steps.Any(s => s.Item.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
                       .ToList();
        RunList.ItemsSource = rows;
        bool noRuns = _all.Count == 0;
        // No history at all: one calm empty state across the whole page, no filter or search to fiddle with.
        Filter.Visibility = noRuns ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = noRuns ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (noRuns) { Empty.Title = "No runs yet"; Empty.Message = "Every clean, sort and change shows up here with its own Undo."; }
        else if (rows.Count == 0) { Empty.Title = "No matching runs"; Empty.Message = "Try another word or filter."; }
        RunList.SelectedItem = rows.FirstOrDefault();
        ShowDetail(RunList.SelectedItem as RunGroupRow);
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnSelect(object sender, SelectionChangedEventArgs e) => ShowDetail(RunList.SelectedItem as RunGroupRow);

    private void ShowDetail(RunGroupRow? row)
    {
        bool show = row is not null;
        DetailCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ListCard, show ? 1 : 3);
        if (row is null) return;
        var s = row.Summary;
        DetailWhen.Text = $"{row.When.ToUpper(CultureInfo.CurrentCulture)} · {row.FeatureTitle.ToUpper(CultureInfo.CurrentCulture)}";
        DetailHeadline.Text = s.IsUndone ? "Put back" : s.Headline;
        var until = row.Runs.Min(r => r.CreatedUtc).ToLocalTime().AddDays(_services.Get<AppSettings>().QuarantineDays);
        DetailSub.Text = row.IsUnfinished ? "Stopped part way. Resume it or roll it back."
            : s.Quarantined > 0 && !s.IsUndone ? $"{s.Quarantined:N0} item{PlanFacts.S(s.Quarantined)} in quarantine until {until:d MMM}"
            : RunSummary.StatusText(row.Runs[0].Status);

        DetailFacts.Children.Clear();
        DetailFacts.RowDefinitions.Clear();
        DetailFacts.ColumnDefinitions.Clear();
        DetailFacts.ColumnDefinitions.Add(new ColumnDefinition());
        DetailFacts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var facts = new List<(string, string)>();
        if (row.Runs.Count > 1)
            foreach (var r in row.Runs.OrderBy(r => SafeOrder.Rank(r.FeatureId)))
                facts.Add((r.Title, new RunSummary(new[] { r }).Headline));
        else
        {
            // Same counting as the confirm dialog and the Done card: "48 of 49" when something was skipped.
            var p = s.Planned;
            if (p.Quarantine > 0) facts.Add(("Moved to quarantine", RunSummary.OfPlanned(s.Quarantined, p.Quarantine) + (s.BytesQuarantined > 0 ? $" · {Sizes.Format(s.BytesQuarantined)}" : "")));
            if (p.Moves > 0) facts.Add(("Files moved", RunSummary.OfPlanned(s.Moved, p.Moves)));
            if (p.Startup > 0) facts.Add(("Startup items", RunSummary.OfPlanned(s.StartupChanged, p.Startup)));
            if (p.Settings > 0) facts.Add(("Settings", RunSummary.OfPlanned(s.SettingsChanged, p.Settings)));
            if (p.Permanent > 0) facts.Add(("Done for good", RunSummary.OfPlanned(s.PermanentDone, p.Permanent)));
            if (s.BytesFreed > 0) facts.Add(("Space freed", Sizes.Format(s.BytesFreed)));
        }
        foreach (var (reason, count, _) in s.SkipReasons.Take(3)) facts.Add(("Skipped: " + reason, $"{count}"));
        for (int i = 0; i < facts.Count; i++)
        {
            DetailFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(facts[i].Item1, "SecondaryTrim"); Grid.SetRow(l, i);
            var v = Text(facts[i].Item2, "Body"); v.FontSize = 13; v.HorizontalAlignment = HorizontalAlignment.Right; v.Margin = new Thickness(8, 3, 0, 3);
            l.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            DetailFacts.Children.Add(l);
            DetailFacts.Children.Add(v);
        }
        DetailRestore.Text = s.RestorePointLine;
        DetailUndo.IsEnabled = row.CanUndo;
        DetailUndo.Visibility = row.IsUnfinished ? Visibility.Collapsed : Visibility.Visible;
        DetailUndo.Content = row.Runs.Count > 1 ? "Undo this whole run" : "Undo this run";
        AutomationProperties.SetName(DetailUndo, (string)DetailUndo.Content);
    }

    private void OnUndoRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunGroupRow row) UndoAsync(row).Forget(Host, "Undo didn't finish");
    }

    private void OnUndoSelected(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is RunGroupRow row) UndoAsync(row).Forget(Host, "Undo didn't finish");
    }

    private void OnResumeRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunGroupRow { Unfinished: { } run }) ResumeAsync(run).Forget(Host, "Couldn't resume");
    }

    private void OnRollbackRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunGroupRow { Unfinished: { } run }) RollbackAsync(run).Forget(Host, "Couldn't roll back");
    }

    private async Task UndoAsync(RunGroupRow row)
    {
        await Host.UndoRunsAsync(row.Runs, CancellationToken.None);
        await RefreshAsync();
    }

    private void OnSteps(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is RunGroupRow row) ShowStepsAsync(row).Forget(Host);
    }

    private async Task ShowStepsAsync(RunGroupRow row)
    {
        var list = new System.Windows.Controls.ListBox { Height = 420, Width = 640 };
        list.SetResourceReference(StyleProperty, "TidyFlatList");
        list.ItemsSource = row.Runs.SelectMany(r => r.Steps).Select(s => $"{StepText(s.Status),-10}  {s.Item.Description}" + (string.IsNullOrEmpty(s.Message) ? "" : $"  ·  {s.Message}")).ToList();
        list.ItemTemplate = new DataTemplate { VisualTree = TextFactory() };
        AutomationProperties.SetName(list, "Every step of this run");
        await Host.ShowDialogAsync(new ContentDialog(Window!.DialogHost) { Title = row.Title, Content = list, CloseButtonText = "Close", DialogMaxWidth = 720 });
    }

    private static FrameworkElementFactory TextFactory()
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        f.SetValue(TextBlock.MarginProperty, new Thickness(8, 4, 8, 4));
        f.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        f.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return f;
    }

    private static string StepText(StepStatus s) => s switch
    {
        StepStatus.Done => "Done", StepStatus.Skipped => "Skipped", StepStatus.Failed => "Failed", StepStatus.Undone => "Put back",
        StepStatus.UndoFailed => "Not put back", StepStatus.Simulated => "Would run", StepStatus.Started => "Started", _ => "Not run",
    };

    /// <summary>Reveals the run's journal file (the technical log) in Explorer; the folder if the file can't be found.</summary>
    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var runsDir = DataPaths.Runs(_services.Get<AppSettings>().DataRoot);
        string target = runsDir;
        if (RunList.SelectedItem is RunGroupRow row && Directory.Exists(runsDir))
        {
            try
            {
                var match = Directory.EnumerateFileSystemEntries(runsDir, row.Runs[0].RunId + "*").FirstOrDefault();
                if (match != null) target = match;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Host.OpenInExplorer(target);
    }

    private static TextBlock Text(string text, string style)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }
}
