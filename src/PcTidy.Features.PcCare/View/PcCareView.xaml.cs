using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Catalog.PcCare;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Planning;
using PcTidy.Features.PcCare.Scanning;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.PcCare.View;

/// <summary>
/// The PC care page: ten tabs over one read-only scan. Each tab keeps its own ticks; the bottom bar acts on the visible tab
/// only and always goes through the standard confirm (IFlowHost). Nothing is ever ticked for you.
/// </summary>
public partial class PcCareView : UserControl, IFixedHeightPage
{
    private enum PageState { Ready, Scanning, Results }

    private readonly IServiceProvider _services;
    private readonly PcCareScanner _scanner;
    private readonly Dictionary<string, SelectionModel> _models = new(StringComparer.Ordinal);
    private readonly List<string> _shredPicks = new();
    private PcCareScanReport? _report;
    private CancellationTokenSource? _scanCts;
    private bool _running;
    private bool _switchingGroup;

    public PcCareView(IServiceProvider services, PcCareScanner scanner)
    {
        InitializeComponent();
        _services = services;
        _scanner = scanner;
        foreach (var (id, _) in PcCareTabs.All)
        {
            var model = new SelectionModel(showSectionHeaders: false);
            var tab = id;
            model.Changed += () => OnTicksChanged(tab);
            _models[id] = model;
        }
        TabGroups.Items = string.Join('|', PcCareTabs.Groups.Select(g => g.Title));
        Tabs.Items = string.Join('|', CurrentGroup.TabIds.Select(PcCareTabs.TitleOf));
        ShredAckBox.Content = ShredderPlanner.PageAcknowledgement;
        Results.OpenInExplorerRequested += p => Host?.OpenInExplorer(p);
        Subtitle.Text = "App updates, Windows repair, network, security, browsers, crashes, restore points and a health report.";
        SetState(PageState.Ready);
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;
    private (string Title, IReadOnlyList<string> TabIds) CurrentGroup => PcCareTabs.Groups[Math.Clamp(TabGroups.SelectedIndex, 0, PcCareTabs.Groups.Count - 1)];
    private string CurrentTab => CurrentGroup.TabIds[Math.Clamp(Tabs.SelectedIndex, 0, CurrentGroup.TabIds.Count - 1)];

    // ------------------------------------------------------------------ scan

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = StartScanAsync();

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>Reads everything (public for screenshots and Home quick actions).</summary>
    public Task StartScanAsync() => RunScanAsync("Checking your PC…", (ctx, progress, token) => _scanner.ScanReportAsync(ctx, progress, token));

    private void OnDnsTest(object sender, RoutedEventArgs e)
    {
        if (_report is not { } earlier) return;
        _ = RunScanAsync("Timing DNS lookups…", (ctx, _, token) => _scanner.RunDnsTestAsync(earlier, ctx, token));
    }

    private async Task RunScanAsync(string title, Func<ScanContext, IProgress<ScanProgress>, CancellationToken, Task<PcCareScanReport>> scan)
    {
        if (_scanCts is not null || _running) return;
        _scanCts = new CancellationTokenSource();
        int tab = Tabs.SelectedIndex;
        ScanTitle.Text = title;
        ScanMessage.Text = "Starting…";
        SetState(PageState.Scanning);
        try
        {
            var runs = await RecentRunsAsync().ConfigureAwait(true);
            var ctx = CreateContext(new PcCareScanOptions());
            var progress = new Progress<ScanProgress>(p => ScanMessage.Text = p.Message);
            var token = _scanCts.Token;
            var report = await Task.Run(() => scan(ctx, progress, token), token).ConfigureAwait(true);
            if (runs.Count > 0) report = _scanner.Rebuild(report, ctx, runs);
            Load(report);
            Tabs.SelectedIndex = tab;
        }
        catch (OperationCanceledException)
        {
            SetState(_report is null ? PageState.Ready : PageState.Results);
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("Scan didn't finish", "Something went wrong while checking your PC: " + ex.Message + " Nothing was changed.", isError: true);
            SetState(_report is null ? PageState.Ready : PageState.Results);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
        }
    }

    private ScanContext CreateContext(PcCareScanOptions options)
    {
        var b = ScanRunner.CreateContext(_services, ScanDepth.Quick);
        return new ScanContext
        {
            Locations = b.Locations, Safety = b.Safety, InUse = b.InUse, Settings = b.Settings, Walker = b.Walker,
            Depth = ScanDepth.Quick, RunningProcesses = b.RunningProcesses, System = b.System,
            Options = new Dictionary<string, object> { [PcCareScanOptions.Key] = options },
        };
    }

    /// <summary>The last finished runs from History, for the report (none when the journal isn't available).</summary>
    private async Task<IReadOnlyList<RecentRun>> RecentRunsAsync()
    {
        if (_services.GetService(typeof(IRunJournal)) is not IRunJournal journal) return Array.Empty<RecentRun>();
        try
        {
            var runs = await journal.ListAsync(CancellationToken.None).ConfigureAwait(true);
            return runs.Where(r => r.Mode == ExecutionMode.Real).OrderByDescending(r => r.CreatedUtc).Take(10)
                .Select(r => new RecentRun(r.CreatedUtc, r.Title, r.Status.ToString())).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Array.Empty<RecentRun>();
        }
    }

    /// <summary>Shows a finished scan (internal so UI tests can load a fixed report).</summary>
    internal void Load(PcCareScanReport report)
    {
        _report = report;
        foreach (var (id, _) in PcCareTabs.All) LoadTab(id);
        Subtitle.Text = $"Checked at {DateTime.Now:t}" + (report.Snapshot.IsAdmin ? " · running as admin" : "");
        LoadFacts(report.Facts);
        UpdateTabLabels();
        SetState(PageState.Results);
    }

    private void LoadTab(string id)
    {
        if (_report is null) return;
        var groups = _report.Result.Groups.Where(g => PcCareTabs.Of(g.Id) == id).ToList();
        if (id == PcCareTabs.Shredder && _shredPicks.Count > 0)
        {
            var guard = _services.GetService(typeof(ISafetyGuard)) as ISafetyGuard;
            var drives = (_services.GetService(typeof(ISystemInfo)) as ISystemInfo)?.Drives ?? Array.Empty<DriveDetails>();
            if (guard is not null) groups.Insert(0, ShredderPlanner.ForFiles(_shredPicks, guard, drives));
        }
        var title = PcCareTabs.All.First(t => t.Id == id).Title;
        var section = new SectionModel(PcCareActions.FeatureId, title, SymbolRegular.HeartPulse24, _report.Result with { Groups = groups.Where(g => g.Items.Count > 0).ToList() });
        // Nothing in PC care is ever pre-ticked; small groups open so a tab isn't just headers.
        _models[id].Load(new[] { section }, (_, _, _) => false, (_, g) => g.Items.Count <= 40);
    }

    /// <summary>The health report's facts as plain "label / value" pairs: read-only, so no tick box and no badge.</summary>
    private void LoadFacts(IReadOnlyList<Report.ReportSection> sections)
    {
        FactsGrid.Children.Clear();
        foreach (var line in sections.SelectMany(s => s.Lines))
        {
            var label = new System.Windows.Controls.TextBlock { Text = line.Label, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = line.Label };
            label.SetResourceReference(StyleProperty, "Caption");
            label.TextWrapping = TextWrapping.NoWrap;
            var value = new System.Windows.Controls.TextBlock { Text = line.Value };
            value.SetResourceReference(StyleProperty, "Body");
            var pair = new StackPanel();
            pair.Children.Add(label);
            pair.Children.Add(value);
            AutomationProperties.SetName(pair, $"{line.Label}: {line.Value}");
            FactsGrid.Children.Add(pair);
        }
    }

    /// <summary>"App updates (12)": counts are the rows you could act on (a group shows its tabs' total).</summary>
    private void UpdateTabLabels()
    {
        if (_report is null) return;
        int keepGroup = TabGroups.SelectedIndex, keepTab = Tabs.SelectedIndex;
        _switchingGroup = true;
        try
        {
            TabGroups.Items = string.Join('|', PcCareTabs.Groups.Select(g => WithCount(g.Title, g.TabIds.Sum(ActionableCount))));
            TabGroups.SelectedIndex = keepGroup;
            Tabs.Items = string.Join('|', CurrentGroup.TabIds.Select(id => WithCount(PcCareTabs.TitleOf(id), ActionableCount(id))));
            Tabs.SelectedIndex = keepTab;
        }
        finally { _switchingGroup = false; }
    }

    private int ActionableCount(string tab) => _report is not null && tab is PcCareTabs.Updates or PcCareTabs.Browsers or PcCareTabs.Leftovers
        ? _report.Result.Groups.Where(g => PcCareTabs.Of(g.Id) == tab).SelectMany(g => g.Items).Count(i => i.DisabledReason is null && i.Actions.Count > 0)
        : 0;

    private static string WithCount(string title, int n) => n > 0 ? $"{title} ({n.ToString("N0", CultureInfo.CurrentCulture)})" : title;

    // ------------------------------------------------------------------ tabs

    private void OnGroupChanged(object? sender, EventArgs e)
    {
        if (_switchingGroup) return;
        // A new group starts on its first tab; the labels (with counts) are rebuilt for that group.
        _switchingGroup = true;
        try
        {
            Tabs.Items = string.Join('|', CurrentGroup.TabIds.Select(id => WithCount(PcCareTabs.TitleOf(id), ActionableCount(id))));
            Tabs.SelectedIndex = 0;
        }
        finally { _switchingGroup = false; }
        if (_report is not null) ShowTab();
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (!_switchingGroup && _report is not null) ShowTab();
    }

    private void ShowTab()
    {
        var tab = CurrentTab;
        var model = _models[tab];
        Results.Model = model;
        bool empty = model.TotalItems == 0;
        Results.Visibility = Vis(!empty);
        TabEmpty.Visibility = Vis(empty);

        string? note = _report!.TabNotes.TryGetValue(tab, out var n) ? n : null;
        // Said once: an empty tab carries its note in the empty state, so the bar above stays closed.
        TabNote.Message = note ?? "";
        TabNote.IsOpen = note is not null && !empty;
        FactsCard.Visibility = Vis(tab == PcCareTabs.Report && FactsGrid.Children.Count > 0);
        TabNote.Severity = tab == PcCareTabs.Security && _report.Antivirus?.Situation is Analysis.AvSituation.DefenderOffByPolicy or Analysis.AvSituation.NoActiveAntivirus
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;
        TabEmpty.Icon = tab == PcCareTabs.Shredder ? SymbolRegular.DocumentAdd24 : SymbolRegular.Info24;
        TabEmpty.Title = tab == PcCareTabs.Shredder ? "Pick files to shred" : "Nothing to show";
        TabEmpty.Message = note ?? "PC Tidy looked and found nothing for this tab.";

        ShowTools(tab);
        StickyBar.Visibility = Vis(model.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).Any(i => i.IsSelectable));
        UpdateSelectionBar();
    }

    /// <summary>The tab's own controls: agreements box, DNS test, links to Tweaks/Drivers, the shredder's picker and "I understand".</summary>
    private void ShowTools(string tab)
    {
        bool updates = tab == PcCareTabs.Updates && _report!.Result.Groups.Any(g => g.Id == PcCareTabs.GroupId(PcCareTabs.Updates, "apps") && g.Items.Count > 0);
        AgreementsBox.Visibility = Vis(updates);
        AgreementsNote.Visibility = Vis(updates);
        DnsButton.Visibility = Vis(tab == PcCareTabs.Network && _report!.Snapshot.Network is not null);
        TweaksButton.Visibility = Vis(tab == PcCareTabs.Security && _report!.Antivirus?.PolicyTurnsDefenderOff == true && Shell is not null);
        DriversButton.Visibility = Vis(tab == PcCareTabs.Crashes && Shell is not null
                                       && (_report!.CrashFixAreas.Contains(CrashFixArea.Drivers) || _report.CrashFixAreas.Contains(CrashFixArea.GraphicsDriver)));
        PickFilesButton.Visibility = Vis(tab == PcCareTabs.Shredder);
        ShredAckBox.Visibility = Vis(tab == PcCareTabs.Shredder);
        TabTools.Visibility = Vis(TabTools.Children.OfType<FrameworkElement>().Any(c => c.Visibility == Visibility.Visible));
    }

    private void OnOpenTweaks(object sender, RoutedEventArgs e) => Shell?.NavigateTo("tweaks");

    private void OnOpenDrivers(object sender, RoutedEventArgs e) => Shell?.NavigateTo("drivers");

    private void OnPageChoiceChanged(object sender, RoutedEventArgs e) => UpdateSelectionBar();

    /// <summary>Opens Windows' file picker (read-only: it only returns paths) and lists the files on the Shredder tab.</summary>
    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick files to shred",
            Multiselect = true,
            CheckFileExists = true,
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var f in dialog.FileNames)
            if (!_shredPicks.Contains(f, StringComparer.OrdinalIgnoreCase)) _shredPicks.Add(f);
        LoadTab(PcCareTabs.Shredder);
        ShowTab();
    }

    // ------------------------------------------------------------------ ticks and the bottom bar

    private void OnTicksChanged(string tab)
    {
        if (_report is not null && tab == CurrentTab) UpdateSelectionBar();
    }

    private IReadOnlyList<ScanItem> PickedItems() => _models[CurrentTab].CheckedItems().Where(r => r.IsSelectable).Select(r => r.Item).ToList();

    private PcCarePageChoices Choices => new() { AcceptAgreements = AgreementsBox.IsChecked == true, ShredAcknowledged = ShredAckBox.IsChecked == true };

    private void UpdateSelectionBar()
    {
        var picked = PickedItems();
        var built = picked.Count == 0 ? null : PcCarePlanBuilder.Build(TitleFor(CurrentTab), picked, Choices);
        var items = built?.Plan?.Items ?? Array.Empty<PlanItem>();
        long bytes = picked.Sum(i => i.SizeBytes);
        SelectionIcon.HasSelection = picked.Count > 0; // shared glyph: empty grey box when nothing is ticked
        SelectedText.Text = picked.Count == 0
            ? "Nothing selected"
            : $"Selected {picked.Count:N0} item{PlanFacts.S(picked.Count)}" + (bytes > 0 ? " · " + Sizes.Format(bytes) : "");

        var extras = new List<string>();
        if (built?.Problem is { } problem) extras.Add(problem);
        if (items.Any(i => i.NeedsAdmin)) extras.Add("needs admin");
        if (items.Any(i => i.IsSystemChange)) extras.Add("restore point first");
        if (picked.Any(i => i.NeedsRestart)) extras.Add("restart needed");
        if (items.Any(i => !i.Reversible)) extras.Add("can't be undone from History");
        if (CurrentTab == PcCareTabs.Updates && picked.Count > 0 && AgreementsBox.IsChecked != true)
            extras.Add("tick the agreements box, or updates that need it will stop");
        SelectedCaption.Text = extras.Count > 0 ? string.Join(" · ", extras) : "Tick what you want, then confirm";

        PrimaryButton.Content = PcCarePlanBuilder.VerbFor(items);
        PrimaryButton.IsEnabled = items.Count > 0 && !_running;
        SimulateButton.IsEnabled = items.Count > 0 && !_running;
        AutomationProperties.SetName(PrimaryButton, (string)PrimaryButton.Content);
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: false);

    private void OnSimulate(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: true);

    /// <summary>Standard confirm → run (or simulate). A real run is followed by a fresh scan (public for screenshots).</summary>
    public async Task RunAsync(bool simulate)
    {
        if (_running) return;
        var built = PcCarePlanBuilder.Build(TitleFor(CurrentTab), PickedItems(), Choices);
        if (built.Plan is not { } plan)
        {
            if (built.Problem is not null) Host?.ShowMessage("One more step", built.Problem);
            return;
        }

        _running = true;
        UpdateSelectionBar();
        Steps.Current = 3;
        bool ran = false;
        try
        {
            var restart = _models[CurrentTab].RestartIds().Concat(plan.Items.Where(i => PlanFacts.NeedsRestart(i, null)).Select(i => i.Id)).ToHashSet();
            var options = new FlowOptions { Title = plan.Title, ShowResult = true, RestartItemIds = restart, PrimaryVerb = PcCarePlanBuilder.VerbFor(plan.Items) };
            if (Shell is { } shell)
            {
                if (simulate) await shell.SimulateAsync(new[] { plan }, options, CancellationToken.None);
                else ran = (await shell.ReviewAndRunAsync(new[] { plan }, options, CancellationToken.None)).Kind == FlowOutcomeKind.Ran;
            }
            else if (Host is { } host)
            {
                if (simulate) await host.SimulateAsync(plan, CancellationToken.None);
                else ran = await host.ReviewAndRunAsync(plan, CancellationToken.None) is not null;
            }
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History.", isError: true);
        }
        finally
        {
            _running = false;
            UpdateSelectionBar();
            if (!ran) Steps.Current = _report is null ? 1 : 2;
        }

        if (!ran) return;
        if (plan.Items.Any(i => i.Kind == ActionKind.ShredFile))
        {
            _shredPicks.RemoveAll(p => !File.Exists(p));
            ShredAckBox.IsChecked = false;
        }
        // Only links were opened: nothing changed, so there's nothing to read again.
        if (plan.Items.All(PcCareActions.IsOpen)) return;
        await StartScanAsync();
    }

    private static string TitleFor(string tab) => tab switch
    {
        PcCareTabs.Updates => "App updates",
        PcCareTabs.Repair => "Windows repair",
        PcCareTabs.Network => "Network fixes",
        PcCareTabs.Security => "Security",
        PcCareTabs.Browsers => "Browser check",
        PcCareTabs.Restore => "Restore points",
        PcCareTabs.Leftovers => "Leftovers",
        PcCareTabs.Shredder => "File shredder",
        PcCareTabs.Report => "Health report",
        _ => "PC care",
    };

    // ------------------------------------------------------------------ states

    private void SetState(PageState state)
    {
        Steps.Set(state == PageState.Results ? 2 : 1,
            state == PageState.Scanning ? "Scanning…" : _report is null ? "Looks only" : FoundCaption(),
            "Pick what changes", "Nothing changed yet", "");
        ReadyState.Visibility = Vis(state == PageState.Ready);
        ScanningState.Visibility = Vis(state == PageState.Scanning);
        Tabs.IsEnabled = state != PageState.Scanning;
        ScanButton.IsEnabled = state != PageState.Scanning;
        ScanButton.Content = _report is null ? "Scan" : "Scan again";
        AutomationProperties.SetName(ScanButton, (string)ScanButton.Content);
        if (state == PageState.Results) ShowTab();
        else
        {
            Results.Visibility = Visibility.Collapsed;
            TabEmpty.Visibility = Visibility.Collapsed;
            StickyBar.Visibility = Visibility.Collapsed;
            TabNote.IsOpen = false;
            TabTools.Visibility = Visibility.Collapsed;
            FactsCard.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>What the step bar says under "Scan" once there's a result.</summary>
    private string FoundCaption()
    {
        int n = _report!.Result.Groups.SelectMany(g => g.Items).Count(i => i.DisabledReason is null && i.Actions.Count > 0);
        return n == 0 ? "Checked" : $"{n:N0} to look at";
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
