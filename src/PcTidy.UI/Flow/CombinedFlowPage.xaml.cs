using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow.Resume;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.UI.Flow;

/// <summary>One section taking part in "Optimize &amp; Organize everything".</summary>
public sealed record OptimizeSection(IFeatureScanner Scanner, string Title, SymbolRegular Icon);

/// <summary>
/// "Optimize &amp; Organize everything": runs every IncludedInOptimizeAll scanner, shows ONE combined preview grouped
/// by section with a totals line, presets (Safe / Recommended / Gamer / Privacy) that set the ticks, ONE confirm,
/// then runs the section plans in a safe order sharing one ParentRunId, with one "Undo this whole run".
/// </summary>
public partial class CombinedFlowPage : UserControl, IFixedHeightPage
{
    private readonly IServiceProvider _services;
    private readonly Func<IReadOnlyList<OptimizeSection>> _sections;
    private readonly SelectionModel _model = new(showSectionHeaders: true);
    private readonly DoneCard _done;
    private CancellationTokenSource? _cts;
    private bool _scanning;
    private int _ranSections;

    /// <param name="sections">Evaluated at scan time, so modules registered later are included.</param>
    public CombinedFlowPage(IServiceProvider services, Func<IReadOnlyList<OptimizeSection>> sections)
    {
        InitializeComponent();
        _services = services;
        _sections = sections;
        _model.Changed += UpdateTotals;
        Results.Model = _model;
        Results.OpenInExplorerRequested += p => Shell?.OpenInExplorer(p);
        var settings = services.GetService(typeof(AppSettings)) as AppSettings;
        DepthPicker.SelectedIndex = settings?.DefaultDepth == ScanDepth.Deep ? 1 : 0;
        PresetPicker.SelectionChanged += (_, _) => OnPresetChanged();
        _done = new DoneCard(settings?.QuarantineDays ?? 7);
        _done.UndoRequested += runs => UndoAsync(runs).Forget(Shell, "Undo didn't finish");
        _done.HistoryRequested += () => Shell?.NavigateTo("history");
        _done.RunCheckLauncher = services.GetService(typeof(IRunCheckLauncher)) as IRunCheckLauncher;
        DoneState.Content = _done;
        SimulationState.BackRequested += () => SetState(FlowState.Preview);
        SimulationState.RunForRealRequested += () => ConfirmAsync().Forget(Shell);
        // One long task at a time: Confirm and Simulate wait (and say so) while another run is going.
        Gate = services.GetService(typeof(RunGate)) as RunGate ?? RunGate.Shared;
        Loaded += (_, _) =>
        {
            Gate.Changed -= OnGateChanged;
            Gate.Changed += OnGateChanged;
            if (State == FlowState.Ready) RefreshReady();
            else if (State == FlowState.Preview) UpdateTotals();
        };
        Unloaded += (_, _) => Gate.Changed -= OnGateChanged;
        SetState(FlowState.Ready);
    }

    /// <summary>The one-long-task-at-a-time gate.</summary>
    public RunGate Gate { get; }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdateTotals();
        else Dispatcher.BeginInvoke(UpdateTotals);
    }

    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? _services.GetService(typeof(IFlowHost)) as IShellFlowHost;

    public FlowState State { get; private set; }
    public ResultsPanel ResultsList => Results;

    public string Preset
    {
        get => OptimizePresets.All[Math.Clamp(PresetPicker.SelectedIndex, 0, OptimizePresets.All.Count - 1)];
        set => PresetPicker.SelectedIndex = OptimizePresets.IndexOf(value);
    }

    private void SetState(FlowState s)
    {
        State = s;
        ReadyState.Visibility = s == FlowState.Ready ? Visibility.Visible : Visibility.Collapsed;
        ScanningState.Visibility = s == FlowState.Scanning ? Visibility.Visible : Visibility.Collapsed;
        PreviewState.Visibility = s == FlowState.Preview ? Visibility.Visible : Visibility.Collapsed;
        SimulationState.Visibility = s == FlowState.Simulation ? Visibility.Visible : Visibility.Collapsed;
        DoneState.Visibility = s == FlowState.Done ? Visibility.Visible : Visibility.Collapsed;
        DepthPicker.IsEnabled = s != FlowState.Scanning;
        if (s is not (FlowState.Ready or FlowState.Scanning)) { FlowNotes.Children.Clear(); FlowNotes.Visibility = Visibility.Collapsed; }
        int n = _model.Sections.Count;
        switch (s)
        {
            case FlowState.Ready: Steps.Set(1, "Every section, read-only", "One combined list", "One confirm", ""); break;
            case FlowState.Scanning: Steps.Set(1, "Scanning…", "One combined list", "One confirm", ""); break;
            case FlowState.Preview: Steps.Set(2, $"{n} section{PlanFacts.S(n)} scanned", "One combined list", "One confirm", ""); break;
            case FlowState.Simulation: Steps.Set(2, $"{n} section{PlanFacts.S(n)} scanned", "Simulated", "One confirm", ""); break;
            case FlowState.Done: Steps.Set(4, $"{n} section{PlanFacts.S(n)} scanned", $"{_ranSections} section{PlanFacts.S(_ranSections)} run", "Confirmed once", "Undo the whole run"); break;
        }
    }

    private void RefreshReady()
    {
        PresetTitle.Text = $"{Preset} preset";
        PresetDescription.Text = OptimizePresets.Describe(Preset) + " You can change any tick before confirming.";
        var secs = _sections();
        SectionList.Text = secs.Count == 0 ? "No sections are available in this build yet." : string.Join(" · ", secs.Select(x => x.Title));
        ScanButton.IsEnabled = secs.Count > 0;
        ShowFlowNotes(offerResume: true);
    }

    // ---------------- resume a scan saved part way; the restart note ----------------

    private ScanResume? Resume => ScanResume.From(_services);

    /// <summary>The saved part-way scan this page would offer to resume (tests and screenshots).</summary>
    public ScanCheckpoint? PendingCheckpoint => Resume?.Pending(ScanResume.CombinedFlow);

    /// <summary>True when the "Windows Update is waiting to restart" note is on the page.</summary>
    public bool ShowsRestartWarning => FlowNotes.Children.OfType<FrameworkElement>().Any(e => Equals(e.Tag, "restart-warning"));

    /// <summary>True when the page offers "Resume / Start over".</summary>
    public bool ShowsResumeOffer => FlowNotes.Children.OfType<FrameworkElement>().Any(e => Equals(e.Tag, "resume-offer"));

    /// <summary>Quick or Deep (the depth switch).</summary>
    public ScanDepth Depth
    {
        get => DepthPicker.SelectedIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick;
        set => DepthPicker.SelectedIndex = value == ScanDepth.Deep ? 1 : 0;
    }

    private int _notesVersion;

    /// <summary>The latest refresh of the resume offer and restart note (tests and screenshots wait on it).</summary>
    public Task FlowNotesTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The resume offer and the restart note. The checkpoint header and the registry are read off the UI thread
    /// (3.1.1 review M2); only the newest refresh fills the panel, and the offer only while the page is still Ready.
    /// </summary>
    private void ShowFlowNotes(bool offerResume) => FlowNotesTask = ShowFlowNotesAsync(offerResume);

    private async Task ShowFlowNotesAsync(bool offerResume)
    {
        int version = ++_notesVersion;
        FlowNotes.Children.Clear();
        FlowNotes.Visibility = Visibility.Collapsed;
        if (Resume is not { } r) return;
        bool wantOffer = offerResume && State == FlowState.Ready;
        (ScanCheckpoint? Cp, bool Restart) facts;
        try { facts = await Task.Run(() => (wantOffer ? r.Pending(ScanResume.CombinedFlow) : null, r.RestartPending())); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { facts = (null, false); }
        if (version != _notesVersion) return; // a newer refresh (or a scan) replaced this one
        if (facts.Cp is { } cp && State == FlowState.Ready)
            FlowNotes.Children.Add(ResumeNotes.ResumeOffer(cp, () => ResumeAsync().Forget(Shell, "Scan didn't finish"), () => StartOverAsync().Forget(Shell, "Scan didn't finish")));
        // Scan everything is always a long scan: say so when Windows Update may restart the PC part way.
        if (facts.Restart)
            FlowNotes.Children.Add(ResumeNotes.RestartWarning(Shell is { } host ? () => ResumeNotes.OpenWindowsUpdateAsync(host).Forget(host, "Couldn't open Settings") : null));
        FlowNotes.Visibility = FlowNotes.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Start over": the saved part-way scan is thrown away and everything is scanned from the start.</summary>
    public async Task StartOverAsync()
    {
        if (Resume is { } r) await Task.Run(() => r.Discard(ScanResume.CombinedFlow));
        await StartScanAsync();
    }

    /// <summary>
    /// "Resume": the saved part-way scan's finished sections are reused (with its depth and preset) and only the rest
    /// are scanned. If the staleness rules say it can't be used, everything is scanned again and the preview says why.
    /// </summary>
    public async Task ResumeAsync()
    {
        if (_scanning) return;
        var cp = Resume is { } r ? await Task.Run(() => r.Pending(ScanResume.CombinedFlow)) : null;
        if (_scanning) return;
        if (cp is null) { await StartScanAsync(); return; }
        DepthPicker.SelectedIndex = cp.Key.Depth == ScanDepth.Deep ? 1 : 0;
        if (cp.Key.Preset is { } preset && OptimizePresets.All.Contains(preset)) Preset = preset;
        await StartScanAsync(cp);
    }

    /// <summary>Below ~900 px the pickers would squeeze the title to "Optimize & O…": put them on their own line.</summary>
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 900;
        Grid.SetRow(Pickers, narrow ? 1 : 0);
        Grid.SetColumn(Pickers, narrow ? 0 : 1);
        Pickers.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 8, 0, 0);
        Pickers.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private void OnPresetChanged()
    {
        if (State == FlowState.Ready) RefreshReady();
        else if (State == FlowState.Preview) _model.Retick(ShouldTick);
    }

    private IReadOnlySet<GroupRow> _learnedOff = new HashSet<GroupRow>();

    private bool ShouldTick(SectionModel s, GroupRow g, ItemRow i)
        => OptimizePresets.ShouldTick(Preset, s.FeatureId, g.Group, i.Item) && !_learnedOff.Contains(g);

    // ---------------- scan ----------------

    private void OnScan(object sender, RoutedEventArgs e) => StartScanAsync().Forget(Shell, "Scan didn't finish");
    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>A fresh scan of every section (a part-way scan saved earlier is replaced).</summary>
    public Task StartScanAsync() => StartScanAsync(resumeFrom: null);

    private async Task StartScanAsync(ScanCheckpoint? resumeFrom)
    {
        if (_scanning) return;
        var sections = _sections();
        if (sections.Count == 0) return;
        _scanning = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try { await ScanAsync(sections, cts.Token, resumeFrom); }
        finally { _cts = null; _scanning = false; }
    }

    private async Task ScanAsync(IReadOnlyList<OptimizeSection> sections, CancellationToken ct, ScanCheckpoint? resumeFrom)
    {
        var depth = DepthPicker.SelectedIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick;
        SetState(FlowState.Scanning);
        ShowFlowNotes(offerResume: false);
        ScanLines.Children.Clear();
        var lines = sections.Select(AddScanLine).ToList();
        ScanBar.Value = 0;
        try
        {
            var ctx = ScanRunner.CreateContext(_services, depth, Preset);
            var scanners = sections.Select(s => s.Scanner).ToList();
            // Reading the saved results and looking at sampled files again happens off the UI thread (review M2/L2).
            var service = Resume;
            var resume = await Task.Run(() => ResumeFlow.Start(service, ScanResume.CombinedFlow, "Scan everything", "optimize", scanners, ctx, resumeFrom));
            var found = resume.FoundTexts();
            var outcomes = await ScanRunner.RunAsync(scanners, ctx, (i, _, p) =>
            {
                lines[i].Status.Text = p.Fraction >= 1 ? found.GetValueOrDefault(i, "Done") : p.Message;
                lines[i].Ring.Visibility = p.Fraction >= 1 ? Visibility.Collapsed : Visibility.Visible;
                lines[i].Check.Visibility = p.Fraction >= 1 ? Visibility.Visible : Visibility.Collapsed;
                ScanBar.Value = (i + Math.Clamp(p.Fraction ?? 0, 0, 1)) / sections.Count * 100;
            }, ct, resume.Session);
            ShowPreview(sections, outcomes, resume);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Shell?.ShowMessage("Scan didn't finish", ex.Message + " Nothing was changed.", isError: true);
            SetState(FlowState.Ready);
        }
    }

    private (TextBlock Status, ProgressRing Ring, SymbolIcon Check) AddScanLine(OptimizeSection s)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new ProgressRing { Width = 16, Height = 16, IsIndeterminate = true, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var check = new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24, Filled = true, FontSize = 18, Visibility = Visibility.Collapsed };
        check.SetResourceReference(ForegroundProperty, "TidyAccentBrush");
        grid.Children.Add(ring);
        grid.Children.Add(check);
        var title = new TextBlock { Text = s.Title };
        title.SetResourceReference(StyleProperty, "CardTitle");
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var status = new TextBlock { Text = "Waiting" };
        status.SetResourceReference(StyleProperty, "SecondaryTrim");
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        ScanLines.Children.Add(grid);
        return (status, ring, check);
    }

    private void ShowPreview(IReadOnlyList<OptimizeSection> sections, IReadOnlyList<ScannerOutcome> outcomes, ResumeStart? resume = null)
    {
        var models = new List<SectionModel>();
        foreach (var o in outcomes.Where(o => o.Result != null))
        {
            var sec = sections.First(s => s.Scanner == o.Scanner);
            var model = new SectionModel(o.Scanner.FeatureId, sec.Title, sec.Icon, o.Result!);
            // A resumed section says when it was found.
            if (o.FoundAtUtc is { } at) model.Note = ResumeText.FoundAt(at, resume?.StoppedByUser == true);
            models.Add(model);
        }
        models = models.OrderBy(m => SafeOrder.Rank(m.FeatureId)).ToList();
        // Groups the user skipped the last few times start unticked; presets never tick them back on.
        var learning = _services.GetService(typeof(IFlowLearning)) as IFlowLearning;
        _learnedOff = LearnedDefaults.Unticked(learning, models);
        _model.Load(models, ShouldTick);

        NotesPanel.Children.Clear();
        if (LearnedDefaults.NoteBar(learning, _learnedOff.ToList(), g => g.Items.Any(i => OptimizePresets.ShouldTick(Preset, g.FeatureId, g.Group, i.Item)),
                again =>
                {
                    _learnedOff = _learnedOff.Except(again).ToHashSet();
                    _model.Retick((s, g, i) => again.Contains(g) ? ShouldTick(s, g, i) : i.IsChecked);
                }) is { } learnedBar)
            NotesPanel.Children.Add(learnedBar);
        foreach (var o in outcomes)
        {
            if (o.Problem != null && !o.Cancelled)
                NotesPanel.Children.Add(new InfoBar { Title = "Part of the scan didn't finish.", Message = o.Problem, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Error, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var note in o.Result?.Notes ?? Array.Empty<string>())
                NotesPanel.Children.Add(new InfoBar { Message = $"{o.Scanner.Title}: {note}", IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 8) });
        }
        if (outcomes.Any(o => o.Cancelled))
            NotesPanel.Children.Add(new InfoBar
            {
                Message = "You stopped the scan, so some sections are missing from this list." + (resume?.Session?.Current.Sections.Count > 0 ? " The finished sections were kept, so you can resume it later." : ""),
                IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8),
            });
        foreach (var bar in ResumeFlow.PreviewBars(outcomes, resume)) NotesPanel.Children.Add(bar);

        // A resumed section keeps its original scan time: the preview says the earliest one (review L4).
        Subtitle.Text = $"{ResumeText.ScannedAt(outcomes.Where(o => o.Result != null).Select(o => o.FoundAtUtc))} · you confirm once";
        SetState(FlowState.Preview);
        UpdateTotals();
    }

    // ---------------- totals ----------------

    private void UpdateTotals()
    {
        var actions = _model.CheckedActions();
        var c = PlanCounts.Of(actions);
        TotalsLine.Text = PlanFacts.TotalsLine(c);
        SelectionIcon.HasSelection = c.HasWork;
        var caption = new List<string>();
        if (c.SystemChange) caption.Add("restore point first");
        if (c.Admin > 0) caption.Add($"admin asked once for {c.Admin} step{PlanFacts.S(c.Admin)}");
        caption.Add("undo everything from History in one click");
        TotalsCaption.Text = char.ToUpperInvariant(caption[0][0]) + string.Join(" · ", caption)[1..];
        // Same section count as the step bar ("5 sections scanned"): say how many of them will run.
        int scanned = _model.Sections.Count;
        int picked = _model.Sections.Count(s => s.CheckedItems.Any(i => i.IsSelectable));
        ConfirmButton.Content = picked <= 1 ? "Review and confirm"
            : picked == scanned ? $"Confirm all {picked} sections" : $"Confirm {picked} of {scanned} sections";
        // One long task at a time: the reason replaces the caption while another run is going.
        bool busy = Gate?.IsBusy == true;
        string busyReason = Gate?.Reason ?? RunGate.BusyReason; // "Available when Clean finishes"
        if (busy && c.HasWork) TotalsCaption.Text = busyReason;
        ConfirmButton.IsEnabled = c.HasWork && !busy;
        SimulateButton.IsEnabled = c.HasWork && !busy;
        AutomationProperties.SetHelpText(ConfirmButton, busy ? busyReason : "");
        AutomationProperties.SetHelpText(SimulateButton, busy ? busyReason : "");
        AutomationProperties.SetName(ConfirmButton, (string)ConfirmButton.Content);

        // One tile per kind of change that is actually ticked, counted like the confirm dialog.
        Totals.Children.Clear();
        if (c.Quarantine > 0)
            Totals.Children.Add(Tile(SymbolRegular.Broom24, c.QuarantineBytes > 0 ? "Clean " + Sizes.Format(c.QuarantineBytes) : $"Clean {c.Quarantine:N0}",
                $"{PlanFacts.Items(c.Quarantine)} → quarantine"));
        if (c.Moves > 0) Totals.Children.Add(Tile(SymbolRegular.FolderArrowRight24, $"Sort {c.Moves:N0} file{PlanFacts.S(c.Moves)}", "into tidy folders"));
        if (c.Startup > 0) Totals.Children.Add(Tile(SymbolRegular.Rocket24, $"{c.Startup} startup item{PlanFacts.S(c.Startup)}", "apps stay installed"));
        if (c.Settings > 0) Totals.Children.Add(Tile(SymbolRegular.WrenchScrewdriver24, $"{c.Settings} setting{PlanFacts.S(c.Settings)}", "each one undoable"));
        if (c.Permanent > 0) Totals.Children.Add(Tile(SymbolRegular.Warning24, $"{c.Permanent} permanent", "asks separately"));
        if (Totals.Children.Count == 0) Totals.Children.Add(Tile(SymbolRegular.CheckboxUnchecked24, "Nothing ticked", "tick what you want below"));
        for (int i = 0; i < Totals.Children.Count; i++)
            ((FrameworkElement)Totals.Children[i]).Margin = new Thickness(i == 0 ? 0 : 5, 0, i == Totals.Children.Count - 1 ? 0 : 5, 0);
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
        AutomationProperties.SetName(b, number + " " + caption);
        return b;
    }

    // ---------------- confirm / simulate ----------------

    private void OnConfirm(object sender, RoutedEventArgs e) => ConfirmAsync().Forget(Shell);
    private void OnSimulate(object sender, RoutedEventArgs e) => SimulateAsync().Forget(Shell, "Simulation didn't finish");

    private async Task UndoAsync(IReadOnlyList<RunRecord> runs)
    {
        if (Shell != null && await Shell.UndoRunsAsync(runs, CancellationToken.None)) _done.MarkUndone();
    }

    public async Task ConfirmAsync()
    {
        if (Shell is not { } shell || Gate.IsBusy) return; // one long task at a time
        var parent = ActionPlan.NewRunId();
        var plans = _model.BuildPlans(parent);
        if (plans.Count == 0) return;
        int n = _model.Sections.Count;
        Steps.Set(3, $"{n} section{PlanFacts.S(n)} scanned", $"{plans.Count} section{PlanFacts.S(plans.Count)} picked", "One confirm", "");
        try
        {
            var outcome = await shell.ReviewAndRunAsync(plans, new FlowOptions
            {
                Title = plans.Count > 1 ? $"Optimize & Organize: {plans.Count} sections" : $"Ready? {plans[0].Title}",
                ShowResult = false,
                ParentRunId = parent,
                RestartItemIds = _model.RestartIds(),
                OfferedGroups = _model.Decisions(),
            }, CancellationToken.None);
            if (outcome.Kind == FlowOutcomeKind.Ran)
            {
                Resume?.Discard(ScanResume.CombinedFlow); // what was found before this run is out of date now
                _ranSections = outcome.Records.Count;
                _done.Runs = outcome.Records;
                if (outcome.BeforeAfter.Count > 0) _done.BeforeAfter = outcome.BeforeAfter;
                Subtitle.Text = $"Finished at {DateTime.Now:t} · one Undo for all of it";
                SetState(FlowState.Done);
                return;
            }
            if (outcome.Kind == FlowOutcomeKind.Simulated)
            {
                SimulationState.Show(outcome.Records);
                SetState(FlowState.Simulation);
                return;
            }
        }
        catch (Exception ex)
        {
            shell.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
        SetState(FlowState.Preview);
    }

    public async Task SimulateAsync()
    {
        if (Shell is not { } shell || Gate.IsBusy) return; // one long task at a time
        var plans = _model.BuildPlans(ActionPlan.NewRunId());
        if (plans.Count == 0) return;
        var records = await shell.SimulateAsync(plans, new FlowOptions { Title = "Optimize & Organize everything", ShowResult = false }, CancellationToken.None);
        if (records.Count == 0) return;
        SimulationState.Show(records);
        SetState(FlowState.Simulation);
    }
}
