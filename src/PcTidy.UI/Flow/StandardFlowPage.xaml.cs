using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using Wpf.Ui.Controls;

namespace PcTidy.UI.Flow;

public enum FlowState { Ready, Scanning, Preview, Simulation, Done }

/// <summary>
/// The shared Scan → Preview → Confirm → Done page used by every feature module that doesn't bring its own page.
/// Read-only until the user confirms: scanning only reads, the confirm/run/undo all go through the shell's flow host.
/// Every count on this page is a count of plan items (<see cref="PlanCounts"/>), the same numbers the confirm
/// dialog, the simulation report and the Done card show.
/// </summary>
public partial class StandardFlowPage : UserControl, IFixedHeightPage, IPreselectable, IFolderTarget, IGroupFocus
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IFeatureScanner> _scanners;
    private readonly string _featureTitle;
    private readonly SymbolRegular _icon;
    private readonly ModuleRisk? _risk;
    private readonly string? _previewNote;
    private readonly SelectionModel _model;
    private readonly DoneCard _done;
    private CancellationTokenSource? _scanCts;
    private DateTime _scannedAt;
    private int _pickedForRun;

    /// <param name="previewNote">The module's own words for the preview banner (<see cref="IModuleFlowText"/>); null = worked out from the scan.</param>
    public StandardFlowPage(IServiceProvider services, string title, string summary, SymbolRegular icon, IReadOnlyList<IFeatureScanner> scanners,
        ModuleRisk? risk = null, string? previewNote = null)
    {
        InitializeComponent();
        _services = services;
        _scanners = scanners;
        _featureTitle = title;
        _icon = icon;
        _risk = risk;
        _previewNote = previewNote;
        _model = new SelectionModel(showSectionHeaders: scanners.Count > 1);
        _model.Changed += UpdateSelectionBar;
        Results.Model = _model;
        Results.OpenInExplorerRequested += p => Host?.OpenInExplorer(p);

        TitleText.Text = title;
        ShowDeclaredPill();
        Subtitle.Text = summary;
        AutomationProperties.SetName(this, title);
        var settings = services.GetService(typeof(AppSettings)) as AppSettings;
        DepthPicker.SelectedIndex = settings?.DefaultDepth == ScanDepth.Deep ? 1 : 0;

        ReadyEmpty.Icon = icon;
        ReadyEmpty.ActionIcon = SymbolRegular.ScanDash24;
        ReadyEmpty.Title = scanners.Count == 0 ? $"{title} isn't available on this PC" : $"Scan to see what {title} can do";
        ReadyEmpty.Message = scanners.Count == 0
            ? "There's nothing for this section to look at on this PC. Nothing is wrong."
            : "Scanning only looks. You get a plain-English list with sizes and reasons, and nothing changes until you confirm.";
        if (scanners.Count == 0) { ReadyEmpty.ActionText = null; ScanButton.IsEnabled = false; DepthPicker.IsEnabled = false; }

        _done = new DoneCard(Shell?.QuarantineDays ?? settings?.QuarantineDays ?? 7);
        _done.UndoRequested += runs => UndoAsync(runs).Forget(Host, "Undo didn't finish");
        _done.HistoryRequested += () => Shell?.NavigateTo("history");
        _done.RunCheckLauncher = services.GetService(typeof(IRunCheckLauncher)) as IRunCheckLauncher;
        DoneHost.Content = _done;

        SimulationState.BackRequested += () => SetState(FlowState.Preview);
        SimulationState.RunForRealRequested += () => RunAsync().Forget(Host);

        // One long task at a time: Run and Simulate wait (and say so) while another run is going.
        Gate = services.GetService(typeof(RunGate)) as RunGate ?? RunGate.Shared;
        Loaded += (_, _) => { Gate.Changed -= OnGateChanged; Gate.Changed += OnGateChanged; UpdateSelectionBar(); };
        Unloaded += (_, _) => Gate.Changed -= OnGateChanged;

        SetState(FlowState.Ready);
    }

    /// <summary>The one-long-task-at-a-time gate (<see cref="RunGate.Shared"/> unless the services bring their own).</summary>
    public RunGate Gate { get; }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdateSelectionBar();
        else Dispatcher.BeginInvoke(UpdateSelectionBar);
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    /// <summary>
    /// A section's own strip (shortcuts to Windows Settings, the cookie keep-list…), shown under the title and
    /// subtitle and above the steps, so every page still starts with its title.
    /// </summary>
    public object? HeaderContent
    {
        get => HeaderSlot.Content;
        set
        {
            HeaderSlot.Content = value;
            HeaderSlot.Visibility = Vis(value is not null);
        }
    }

    /// <summary>False for sections whose scanner reads settings and doesn't use Quick/Deep (Notifications, Privacy sweep).</summary>
    public bool ShowDepthSwitch
    {
        get => DepthPicker.Visibility == Visibility.Visible;
        set => DepthPicker.Visibility = Vis(value);
    }

    /// <summary>The ready card's message, for sections where the generic "sizes and reasons" doesn't fit.</summary>
    public string ReadyMessage
    {
        get => ReadyEmpty.Message ?? "";
        set { if (_scanners.Count > 0) ReadyEmpty.Message = value; }
    }

    public FlowState State { get; private set; }
    public SelectionModel Selection => _model;
    public ResultsPanel ResultsList => Results;

    /// <summary>The module's own risk pill, or none when it declares none.</summary>
    private void ShowDeclaredPill()
    {
        if (_risk is not { } r) { RiskPill.Visibility = Visibility.Collapsed; return; }
        SetPill(r switch { ModuleRisk.Safe => "Safe section", ModuleRisk.Check => "Check each item", _ => "Expert" },
            r switch { ModuleRisk.Safe => Controls.PillTone.Success, ModuleRisk.Check => Controls.PillTone.Caution, _ => Controls.PillTone.Critical });
    }

    private void SetPill(string text, Controls.PillTone tone)
    {
        RiskPill.Text = text;
        RiskPill.Tone = tone;
        RiskPill.Visibility = Visibility.Visible;
        AutomationProperties.SetName(RiskPill, text);
    }

    // ---------------- states ----------------

    private void SetState(FlowState state)
    {
        State = state;
        ReadyState.Visibility = Vis(state == FlowState.Ready);
        ScanningState.Visibility = Vis(state == FlowState.Scanning);
        PreviewState.Visibility = Vis(state == FlowState.Preview);
        SimulationState.Visibility = Vis(state == FlowState.Simulation);
        DoneState.Visibility = Vis(state == FlowState.Done);
        // Before the first scan the big "Scan now" in the card is the only scan button (no duplicate in the header).
        ScanButton.Visibility = Vis(state is FlowState.Preview or FlowState.Simulation or FlowState.Done);
        ScanButton.IsEnabled = state != FlowState.Scanning && _scanners.Count > 0;
        DepthPicker.IsEnabled = state != FlowState.Scanning && _scanners.Count > 0;
        ScanButton.Content = "Scan again";
        AutomationProperties.SetName(ScanButton, "Scan again");

        int found = _model.TotalItems;
        switch (state)
        {
            case FlowState.Ready: Steps.Set(1, "Looks, changes nothing", "Pick what goes", "One last check", ""); break;
            case FlowState.Scanning: Steps.Set(1, "Scanning…", "Pick what goes", "One last check", ""); break;
            case FlowState.Preview: Steps.Set(2, $"{found:N0} item{PlanFacts.S(found)} found", "Pick what goes", "One last check", ""); break;
            case FlowState.Simulation: Steps.Set(2, $"{found:N0} item{PlanFacts.S(found)} found", "Simulated", "One last check", ""); break;
            case FlowState.Done:
                var s = new RunSummary(_done.Runs);
                Steps.Set(4, $"{found:N0} found", $"{_pickedForRun:N0} picked",
                    s.RestorePoint?.Created == true ? "Restore point made" : "Confirmed", $"Undo for {Shell?.QuarantineDays ?? 7} days");
                break;
        }
    }

    /// <summary>A section's own icon from its title ("Windows tips" → bulb), else the page's icon, so sections don't all look alike.</summary>
    private SymbolRegular SectionIcon(IFeatureScanner scanner)
    {
        if (_scanners.Count < 2) return _icon;
        var icon = GroupIcons.For(scanner.FeatureId + " " + scanner.Title);
        return icon == SymbolRegular.Box24 ? _icon : icon;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsMoreCautious(Controls.PillTone result, ModuleRisk declared)
        => declared == ModuleRisk.Safe ? result != Controls.PillTone.Success : declared == ModuleRisk.Check && result == Controls.PillTone.Critical;

    /// <summary>"All Safe" → "all Safe", "Check each item" → "check each item" (it follows a "·" in the subtitle).</summary>
    private static string MixText(string pill) => pill.Length > 1 && char.IsUpper(pill[0]) && !char.IsUpper(pill[1]) ? char.ToLowerInvariant(pill[0]) + pill[1..] : pill;

    // ---------------- scan ----------------

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        _folder = null; // a scan the user starts here always looks in the section's usual places
        StartScanAsync().Forget(Host, "Scan didn't finish");
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>Starts a scan with the selected depth (public for Home quick actions and screenshots).</summary>
    public async Task StartScanAsync()
    {
        if (State == FlowState.Scanning || _scanners.Count == 0) return;
        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        try { await ScanAsync(cts.Token); }
        finally { _scanCts = null; }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var depth = DepthPicker.SelectedIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick;
        SetState(FlowState.Scanning);
        ScanTitle.Text = depth == ScanDepth.Deep ? "Deep scan: looking everywhere…" : "Quick scan: looking in the usual places…";
        ScanMessage.Text = "Starting…";
        ScanStats.Text = depth == ScanDepth.Deep ? "Deep scans take longer on purpose and run at low priority." : "";
        ScanBar.IsIndeterminate = true;

        IReadOnlyList<ScannerOutcome> outcomes;
        try
        {
            var ctx = ScanRunner.CreateContext(_services, depth, preset: null, roots: _folder is null ? null : new[] { _folder });
            outcomes = await ScanRunner.RunAsync(_scanners, ctx, (i, sc, p) =>
            {
                ScanMessage.Text = _scanners.Count > 1 ? $"{sc.Title}: {p.Message}" : p.Message;
                if (p.Fraction is { } f)
                {
                    ScanBar.IsIndeterminate = false;
                    ScanBar.Value = (i + Math.Clamp(f, 0, 1)) / _scanners.Count * 100;
                }
                if (p.ItemsSeen > 0) ScanStats.Text = $"{p.ItemsSeen:N0} items looked at · {Sizes.Format(p.BytesSeen)}";
            }, ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Host?.ShowMessage("Scan didn't finish", "Something went wrong while looking: " + ex.Message + " Nothing was changed.", isError: true);
            SetState(_model.TotalItems > 0 ? FlowState.Preview : FlowState.Ready);
            return;
        }

        if (outcomes.Any(o => o.Cancelled) && outcomes.All(o => o.Result is null))
        {
            SetState(_model.TotalItems > 0 ? FlowState.Preview : FlowState.Ready);
            return;
        }
        ShowResults(outcomes, depth);
    }

    private void ShowResults(IReadOnlyList<ScannerOutcome> outcomes, ScanDepth depth)
    {
        _scannedAt = DateTime.Now;
        var sections = outcomes.Where(o => o.Result != null)
            .Select(o => new SectionModel(o.Scanner.FeatureId, o.Scanner.Title, SectionIcon(o.Scanner), o.Result!)).ToList();
        // Standard flow: honour the scanner's defaults, but never pre-tick Check/Permanent/irreversible (hard rule).
        // Groups the user skipped the last few times start unticked (learning only ever removes ticks).
        var learning = _services.GetService(typeof(IFlowLearning)) as IFlowLearning;
        var learnedOff = LearnedDefaults.Unticked(learning, sections);
        _model.Load(sections, (_, g, i) => OptimizePresets.IsTickableByDefault(i.Item) && !learnedOff.Contains(g), (s, g) => g == s.Groups.FirstOrDefault(x => x.Items.Count is > 1 and <= 8));

        NotesPanel.Children.Clear();
        if (_folder is not null)
            NotesPanel.Children.Add(new InfoBar { Message = $"Looking only in {_folder}.", IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) });
        bool WouldTick(GroupRow g) => g.Items.Any(i => i.IsSelectable && OptimizePresets.IsTickableByDefault(i.Item));
        if (LearnedDefaults.NoteBar(learning, learnedOff.ToList(), WouldTick,
                again => _model.Retick((_, g, i) => again.Contains(g) ? OptimizePresets.IsTickableByDefault(i.Item) : i.IsChecked)) is { } learnedBar)
            NotesPanel.Children.Add(learnedBar);
        foreach (var note in outcomes.Where(o => o.Result != null).SelectMany(o => o.Result!.Notes))
            NotesPanel.Children.Add(new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var problem in outcomes.Where(o => o.Problem != null && !o.Cancelled).Select(o => o.Problem!))
            NotesPanel.Children.Add(new InfoBar { Title = "Part of the scan didn't finish.", Message = problem, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Error, Margin = new Thickness(0, 0, 0, 8) });
        if (outcomes.Any(o => o.Cancelled))
            NotesPanel.Children.Add(new InfoBar { Message = "You stopped the scan, so this list may be incomplete.", IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) });

        var allItems = sections.SelectMany(s => s.Result.AllItems).ToList();
        int days = Shell?.QuarantineDays ?? 7;
        NothingYetBar.Message = _previewNote ?? (outcomes.Select(o => (o.Scanner as IModuleFlowText)?.PreviewNote).FirstOrDefault(n => n != null))
                                ?? FlowCopy.PreviewNote(allItems, days);
        // The section's own pill stays unless the rows are more cautious than it (a Check or Permanent item in a
        // "Safe section"), so the risk never seems to drop by itself after a scan. The mix goes in the subtitle.
        var mix = FlowCopy.ResultPill(allItems, _risk);
        if (mix is { } pill && (_risk is null || IsMoreCautious(pill.Tone, _risk.Value))) SetPill(pill.Text, pill.Tone);
        else ShowDeclaredPill();
        SelectSafeButton.IsEnabled = _model.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items)
            .Any(i => i.IsSelectable && i.Item.Badge == Badge.Safe && i.Item.Actions.All(a => a.Reversible));

        bool empty = _model.TotalItems == 0;
        Results.Visibility = Vis(!empty);
        NothingFound.Visibility = Vis(empty);
        StickyBar.Visibility = Vis(!empty);
        NothingFound.Message = $"{_featureTitle} found nothing worth changing right now.";
        // Sections that don't deal in files (settings, startup) never show a byte total.
        string found = _model.TotalBytes > 0 ? $"{Sizes.Format(_model.TotalBytes)} found" : $"{_model.TotalItems:N0} item{PlanFacts.S(_model.TotalItems)} found";
        Subtitle.Text = empty
            ? $"Scanned at {_scannedAt:t} · nothing to do"
            : $"Scanned at {_scannedAt:t}{(ShowDepthSwitch ? depth == ScanDepth.Deep ? " (Deep)" : " (Quick)" : "")} · {found}{(mix is { } m && _risk != ModuleRisk.Expert && m.Text != RiskPill.Text ? " · " + MixText(m.Text) : "")} · you pick what goes";
        SetState(FlowState.Preview);
        UpdateSelectionBar();
    }

    // ---------------- selection bar ----------------

    private void UpdateSelectionBar()
    {
        var actions = _model.CheckedActions();
        var c = PlanCounts.Of(actions);
        // Plan items, counted exactly like the confirm dialog ("Clean 6.3 GB (49 items) · 3 settings").
        SelectedText.Text = c.HasWork ? "Selected: " + PlanFacts.TotalsLine(c) : "Nothing selected";
        SelectionIcon.HasSelection = c.HasWork;

        var extras = new List<string>();
        if (c.Permanent > 0) extras.Add($"{c.Permanent} permanent (asks separately)");
        if (c.Admin > 0) extras.Add($"{c.Admin} step{PlanFacts.S(c.Admin)} need admin");
        if (c.SystemChange) extras.Add("restore point first");
        SelectedCaption.Text = extras.Count > 0 ? string.Join(" · ", extras)
            : c.OnlyOpens ? "PC Tidy only opens it for you. Nothing changes on your PC unless you go ahead there."
            : c.HasWork ? "Everything here can be undone from History" : "Tick what you want PC Tidy to do";

        PrimaryButton.Content = PlanFacts.PrimaryAction(actions);
        PrimaryButton.Icon = new SymbolIcon(PlanFacts.PrimaryIcon(c));
        // One long task at a time: while another run is going, the reason replaces the caption.
        bool busy = Gate?.IsBusy == true;
        string busyReason = Gate?.Reason ?? RunGate.BusyReason; // "Available when Clean finishes"
        if (busy && c.HasWork) SelectedCaption.Text = busyReason;
        PrimaryButton.IsEnabled = c.HasWork && !busy;
        SimulateButton.IsEnabled = c.HasWork && !busy;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
        AutomationProperties.SetHelpText(PrimaryButton, busy ? busyReason : "");
        AutomationProperties.SetHelpText(SimulateButton, busy ? busyReason : "");
    }

    /// <summary>Whether Run and Simulate can be pressed now (something ticked, and no other long task going).</summary>
    public bool CanRunNow => PrimaryButton.IsEnabled;

    /// <summary>The sticky bar's caption (the reason text while another task is going).</summary>
    public string SelectionCaption => SelectedCaption.Text;

    private void OnSelectSafe(object sender, RoutedEventArgs e) => _model.Retick((_, _, i) => i.Item.Badge == Badge.Safe && i.Item.Actions.All(a => a.Reversible));

    // ---------------- confirm / run / simulate ----------------

    private void OnPrimary(object sender, RoutedEventArgs e) => RunAsync().Forget(Host);

    private void OnSimulate(object sender, RoutedEventArgs e) => SimulateAsync().Forget(Host, "Simulation didn't finish");

    /// <summary>Confirm dialog → run → Done card (public for screenshots).</summary>
    public async Task RunAsync()
    {
        if (Gate.IsBusy) return; // one long task at a time (the buttons say so)
        var plans = _model.BuildPlans(parentRunId: null);
        if (plans.Count == 0) return;
        var picked = PlanCounts.Of(plans.SelectMany(p => p.Items));
        _pickedForRun = picked.Total + picked.Opens;
        var options = new FlowOptions { Title = $"Ready to {VerbFor(plans)}?", ShowResult = false, RestartItemIds = _model.RestartIds(), OfferedGroups = _model.Decisions() };
        Steps.Set(3, $"{_model.TotalItems:N0} found", $"{_pickedForRun:N0} picked", "One last check", "");
        try
        {
            if (Shell is { } shell)
            {
                var outcome = await shell.ReviewAndRunAsync(plans, options, CancellationToken.None);
                switch (outcome.Kind)
                {
                    case FlowOutcomeKind.Ran: ShowDone(outcome.Records, outcome.BeforeAfter); return;
                    case FlowOutcomeKind.Simulated: ShowSimulation(outcome.Records); return;
                }
            }
            else if (Host is { } host)
            {
                // Plain IFlowHost: it shows its own Done card; we just reflect the result.
                var records = new List<RunRecord>();
                foreach (var p in plans)
                    if (await host.ReviewAndRunAsync(p, CancellationToken.None) is { } r) records.Add(r);
                if (records.Count > 0) { ShowDone(records); return; }
            }
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
        SetState(FlowState.Preview);
    }

    /// <summary>"What would happen" with nothing changed (public for screenshots).</summary>
    public async Task SimulateAsync()
    {
        if (Gate.IsBusy) return;
        var plans = _model.BuildPlans(parentRunId: null);
        if (plans.Count == 0) return;
        try
        {
            if (Shell is { } shell)
                ShowSimulation(await shell.SimulateAsync(plans, new FlowOptions { Title = _featureTitle, ShowResult = false }, CancellationToken.None));
            else if (Host is { } host)
            {
                var list = new List<RunRecord>();
                foreach (var p in plans) if (await host.SimulateAsync(p, CancellationToken.None) is { } r) list.Add(r);
                ShowSimulation(list);
            }
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("Simulation didn't finish", ex.Message, isError: true);
        }
    }

    private async Task UndoAsync(IReadOnlyList<RunRecord> runs)
    {
        if (Shell != null && await Shell.UndoRunsAsync(runs, CancellationToken.None)) _done.MarkUndone();
    }

    private void ShowSimulation(IReadOnlyList<RunRecord> records)
    {
        if (records.Count == 0) return;
        SimulationState.Show(records);
        SetState(FlowState.Simulation);
    }

    private void ShowDone(IReadOnlyList<RunRecord> records, IReadOnlyList<string>? beforeAfter = null)
    {
        _done.Runs = records;
        if (beforeAfter is { Count: > 0 }) _done.BeforeAfter = beforeAfter;
        var s = new RunSummary(records);
        Subtitle.Text = $"Finished at {DateTime.Now:t} · " + (s.Quarantined > 0 ? "everything removed is in quarantine, nothing was deleted" : "you can undo this from History");
        SetState(FlowState.Done);
    }

    private static string VerbFor(IReadOnlyList<ActionPlan> plans)
    {
        var c = PlanCounts.Of(plans.SelectMany(p => p.Items));
        return PlanFacts.PrimaryIcon(c) switch
        {
            SymbolRegular.Broom24 => "clean",
            SymbolRegular.FolderArrowRight24 => "move these files",
            SymbolRegular.Cookies24 => "delete these cookies",
            _ => "apply these changes",
        };
    }

    // ---------------- opened from elsewhere (INavigationService, Explorer's right-click) ----------------

    private string? _folder;

    /// <summary>Ticks the items another section asked for, scanning first when there are no results yet. Never runs anything.</summary>
    public async Task PreselectAsync(IReadOnlyCollection<string> itemIds)
    {
        if (State == FlowState.Scanning) return;
        if (_model.TotalItems == 0 || State is FlowState.Ready or FlowState.Done) await StartScanAsync();
        if (_model.TotalItems == 0) return;
        if (State != FlowState.Preview) SetState(FlowState.Preview);
        var result = _model.Preselect(itemIds);
        NotesPanel.Children.Insert(0, new InfoBar
        {
            Message = result.Message, IsOpen = true, IsClosable = true, Margin = new Thickness(0, 0, 0, 8),
            Severity = result.Picked > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
        });
    }

    /// <summary>Opens and scrolls to one scan group (scanning first when there are no results yet).</summary>
    public async Task<bool> FocusGroupAsync(string groupId)
    {
        if (State == FlowState.Scanning) return false;
        if (_model.TotalItems == 0 || State is FlowState.Ready or FlowState.Done) await StartScanAsync();
        var group = _model.Sections.SelectMany(s => s.Groups).FirstOrDefault(g => string.Equals(g.Group.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null) return false;
        if (State != FlowState.Preview) SetState(FlowState.Preview);
        if (group.Items.Count <= 500) group.IsExpanded = true;
        await Dispatcher.InvokeAsync(() => Results.ShowRow(group), System.Windows.Threading.DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>
    /// Shows results that were already scanned (e.g. by Optimize everything) without scanning again, so items from
    /// that scan can be pre-selected. Results whose section isn't on this page are ignored. Returns false if none fit.
    /// </summary>
    public bool ShowExistingResults(IReadOnlyList<ScanResult> results)
    {
        if (State == FlowState.Scanning) return false;
        var outcomes = results
            .Select(r => (Result: r, Scanner: _scanners.FirstOrDefault(s => string.Equals(s.FeatureId, r.FeatureId, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Scanner is not null)
            .Select(x => new ScannerOutcome(x.Scanner!, x.Result, null, false))
            .ToList();
        if (outcomes.Count == 0) return false;
        _folder = null;
        ShowResults(outcomes, outcomes[0].Result!.Depth);
        return true;
    }

    /// <summary>Scans just this folder (sections whose scanner takes roots, e.g. Organize). Preview only.</summary>
    public async Task OpenFolderAsync(string folder)
    {
        if (State == FlowState.Scanning) return;
        _folder = folder;
        await StartScanAsync();
    }

    /// <summary>Back from the simulation report to the ticked list.</summary>
    public void BackToPreview()
    {
        if (_model.TotalItems > 0) SetState(FlowState.Preview);
    }

    private void OnHome(object sender, RoutedEventArgs e) => Shell?.NavigateTo("home");
    private void OnQuarantine(object sender, RoutedEventArgs e) => Shell?.NavigateTo("quarantine");
}
