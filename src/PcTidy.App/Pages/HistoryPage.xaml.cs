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
        bits.Add(Summary.IsUndone ? (Summary.PermanentDone > 0 ? "Put back all that could be" : "Put back") : Summary.FullHeadline);
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
public partial class HistoryPage : UserControl, IFixedHeightPage, INavigationTarget
{
    private readonly AppServices _services;
    private List<RunGroupRow> _all = new();
    private List<string> _filters = new();
    private bool _refreshing;
    private string? _pendingRunId;

    /// <summary>
    /// A <see cref="HistoryOpenRequest"/> (Measure's "Open History") opens the Runs view with that run selected and its
    /// details showing; the rail passes null and History opens as usual.
    /// </summary>
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not HistoryOpenRequest { RunId: { Length: > 0 } id }) return;
        _pendingRunId = id;
        if (ShowingRestorePoints) ViewPicker.SelectedIndex = 0;
        if (_all.Count > 0) SelectPendingRun();
        if (IsLoaded) RefreshAsync().Forget(Host, "Couldn't read History"); // the run may be newer than the list
    }

    /// <summary>The run History was asked to show, if it is in the list: filters cleared, selected, scrolled to.</summary>
    private void SelectPendingRun()
    {
        if (_pendingRunId is not { } id) return;
        var row = _all.FirstOrDefault(r => r.Runs.Any(x => x.RunId == id || x.ParentRunId == id));
        if (row is null) return;
        _pendingRunId = null;
        if (!RunList.Items.Contains(row))
        {
            SearchBox.Text = "";
            Filter.SelectedIndex = 0;
            ApplyFilter();
        }
        RunList.SelectedItem = row;
        RunList.ScrollIntoView(row);
        ShowDetail(row);
    }

    /// <summary>The run selected in the list (tests).</summary>
    public RunGroupRow? SelectedRun => RunList.SelectedItem as RunGroupRow;

    public HistoryPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Filter.SelectionChanged += (_, _) => ApplyFilter();
        ViewPicker.SelectionChanged += (_, _) => ShowView();
        // Narrow windows: a slimmer details card leaves the run titles room next to their buttons.
        SizeChanged += (_, e) => DetailColumn.Width = new GridLength(e.NewSize.Width < 900 ? 256 : 300);
        Loaded += (_, _) => RefreshAsync().Forget(Host, "Couldn't read History");
    }

    private FlowHost Host => (FlowHost)_services.Get<IShellFlowHost>();
    private MainWindow? Window => System.Windows.Window.GetWindow(this) as MainWindow;

    // ---------------- Restore points view (layout spec §4.5) ----------------

    /// <summary>Runs (0) or Restore points (1).</summary>
    public bool ShowingRestorePoints => ViewPicker.SelectedIndex == 1;

    /// <summary>Switches to the Restore points view (screenshots, the tour) and loads it.</summary>
    public Task ShowRestorePointsAsync()
    {
        ViewPicker.SelectedIndex = 1;
        return LoadRestorePointsAsync();
    }

    private void ShowView()
    {
        bool points = ShowingRestorePoints;
        RestorePointsView.Visibility = points ? Visibility.Visible : Visibility.Collapsed;
        RunsView.Visibility = points ? Visibility.Collapsed : Visibility.Visible;
        UnfinishedPanel.Visibility = !points && UnfinishedPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Filter.Visibility = points || _all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = points || _all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (points) LoadRestorePointsAsync().Forget(Host, "Couldn't read the restore points");
    }

    /// <summary>Demo: invented points. Practice PC: none (it isn't this PC). Otherwise Windows' list (read-only WMI).</summary>
    private IRestorePointReader? Reader =>
        _services.GetService(typeof(IRestorePointReader)) as IRestorePointReader
        ?? (_services.IsDemo ? new DemoRestorePointReader() : _services.Args.SandboxRoot != null ? null : new WmiRestorePointReader());

    private IReadOnlyList<RestorePointRow> _points = Array.Empty<RestorePointRow>();

    /// <summary>The cards shown (tests and screenshots).</summary>
    public IReadOnlyList<RestorePointRow> RestorePointRows => _points;

    private int _pointsLoad;

    private async Task LoadRestorePointsAsync()
    {
        RestoreCards.Children.Clear();
        RestoreProblem.IsOpen = false;
        RestoreEmpty.Visibility = Visibility.Collapsed;
        if (Reader is not { } reader)
        {
            ShowRestoreProblem("Practice mode doesn't read this PC's restore points.");
            return;
        }
        int load = ++_pointsLoad;
        var list = await reader.ReadAsync(CancellationToken.None);
        if (load != _pointsLoad) return; // a newer load is on its way
        _points = RestorePointsModel.Build(list.Points, ShellState.Get(_services.Get<AppSettings>()).RestorePointTags);
        if (list.Problem is { } problem) ShowRestoreProblem(problem);
        RestoreEmpty.Visibility = _points.Count == 0 && list.Problem is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _points) RestoreCards.Children.Add(RestoreCard(row));
    }

    private void ShowRestoreProblem(string message)
    {
        RestoreProblem.Message = message;
        RestoreProblem.IsOpen = true;
        // Without a list, Windows' own window still works.
        RestoreCards.Children.Clear();
        var open = new Wpf.Ui.Controls.Button { Content = "Open System Restore…", Icon = new SymbolIcon(SymbolRegular.History24), Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(open, "Open Windows' System Restore window");
        open.Click += (_, _) => OpenSystemRestore();
        RestoreCards.Children.Add(open);
    }

    private FrameworkElement RestoreCard(RestorePointRow row)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "TidyCard");
        AutomationProperties.SetName(card, row.AutomationName);
        var stack = new StackPanel();
        var head = new DockPanel();
        var tag = new BadgePill { Text = row.Tag ?? "", Tone = RestorePointsModel.ToneOf(row.Tag), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(tag, Dock.Right);
        head.Children.Add(tag);
        if (row.IsNewest)
        {
            var newest = new BadgePill { Text = "Newest · always kept", Tone = PillTone.Accent, ShowDot = false, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(newest, Dock.Right);
            head.Children.Add(newest);
        }
        var name = Text(row.Name, "CardTitle");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(name);
        stack.Children.Add(head);
        stack.Children.Add(Wrap(Text(row.Description, "Secondary")));
        stack.Children.Add(Text(row.When, "Caption"));

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var restore = new Wpf.Ui.Controls.Button { Content = "Restore…", Icon = new SymbolIcon(SymbolRegular.History24), Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(restore, $"Restore: opens Windows' System Restore window ({row.Name})");
        restore.ToolTip = "Opens Windows' own System Restore window. You pick the point and confirm there.";
        restore.Click += (_, _) => OpenSystemRestore();
        actions.Children.Add(restore);
        if (row.CanDelete)
        {
            var delete = new Wpf.Ui.Controls.Button { Content = "Delete", Icon = new SymbolIcon(SymbolRegular.Delete24), Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetName(delete, $"Delete the restore point {row.Name}, {row.When}");
            RunGate.SetDisableWhileBusy(delete, true);
            delete.Click += (_, _) => DeleteAsync(row).Forget(Host, "Couldn't delete that restore point");
            actions.Children.Add(delete);
        }
        stack.Children.Add(actions);

        // The tag picker sits on its own row on every card (the same place whether or not the card has Delete).
        var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var tagLabel = Text("Tag", "Secondary");
        tagLabel.VerticalAlignment = VerticalAlignment.Center;
        tagLabel.Margin = new Thickness(0, 0, 8, 0);
        tagRow.Children.Add(tagLabel);
        var picker = new ComboBox { MinWidth = 120, ItemsSource = new[] { NoTag }.Concat(RestorePointsModel.Tags).ToList() };
        picker.SelectedItem = row.Tag ?? NoTag;
        AutomationProperties.SetName(picker, $"Colour tag for {row.Name}");
        picker.ToolTip = "A colour tag to find this point again. Kept on this PC only.";
        // Picking a tag updates this card in place: no reload, so keyboard focus stays on the picker.
        picker.SelectionChanged += (_, _) =>
        {
            var chosen = SetTag(row, picker.SelectedItem as string);
            tag.Text = chosen ?? "";
            tag.Tone = RestorePointsModel.ToneOf(chosen);
            AutomationProperties.SetName(card, $"{row.Name}, {row.When}, {row.Description}" + (chosen is null ? "" : $", tagged {chosen}")
                                               + (row.IsNewest ? ", newest, always kept" : ""));
        };
        tagRow.Children.Add(picker);
        stack.Children.Add(tagRow);
        card.Child = stack;
        return card;
    }

    private const string NoTag = "No tag";

    private static TextBlock Wrap(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    /// <summary>Remembers the card's tag (this PC only) and returns it (null = no tag). The cards aren't rebuilt.</summary>
    private string? SetTag(RestorePointRow row, string? tag)
    {
        var chosen = tag is null || tag == NoTag || !RestorePointsModel.Tags.Contains(tag) ? null : tag;
        var settings = _services.Get<AppSettings>();
        ShellState.Update(settings, s => s with { RestorePointTags = RestorePointsModel.WithTag(s.RestorePointTags, row.TagKey, chosen) });
        Window?.SaveSettingsQuietly();
        return chosen;
    }

    /// <summary>"Restore…": Windows' own System Restore window. PC Tidy never restores by itself.</summary>
    private void OpenSystemRestore()
    {
        if (_services.IsDemo || _services.Args.SandboxRoot != null)
        {
            Host.ShowMessage("Not in this mode", "System Restore works on this real PC, so it isn't opened from demo or practice mode.");
            return;
        }
        try { PcTidy.App.Updates.OwnProcesses.OpenSystemRestore(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Host.ShowMessage("Couldn't open System Restore", ex.Message, isError: true); }
    }

    /// <summary>"Delete": the normal confirm flow with PC care's RestorePointDelete item (never the newest point).</summary>
    private async Task DeleteAsync(RestorePointRow row)
    {
        if (RestorePointsModel.DeletePlan(row) is not { } plan) return;
        var outcome = await Host.ReviewAndRunAsync(new[] { plan }, new FlowOptions { Title = "Delete this restore point?", ShowResult = true }, CancellationToken.None);
        if (outcome.Kind == FlowOutcomeKind.Ran) await LoadRestorePointsAsync();
    }

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
        SelectPendingRun();
    }

    /// <summary>One banner per unfinished run, with Resume / Roll back right under it (InfoBar can't host buttons).</summary>
    private void ShowUnfinished(IReadOnlyList<RunRecord> unfinished)
    {
        UnfinishedPanel.Children.Clear();
        UnfinishedPanel.Visibility = unfinished.Count > 0 && !ShowingRestorePoints ? Visibility.Visible : Visibility.Collapsed;
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
        Filter.Visibility = noRuns || ShowingRestorePoints ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = noRuns || ShowingRestorePoints ? Visibility.Collapsed : Visibility.Visible;
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
        DetailHeadline.Text = s.IsUndone ? (s.PermanentDone > 0 ? "Put back all that could be" : "Put back") : s.FullHeadline;
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
                facts.Add((r.Title, new RunSummary(new[] { r }).FullHeadline));
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
