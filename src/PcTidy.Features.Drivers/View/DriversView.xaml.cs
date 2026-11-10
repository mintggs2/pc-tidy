using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Drivers.Analysis;
using PcTidy.Features.Drivers.Planning;
using PcTidy.Features.Drivers.Scanning;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Drivers.View;

/// <summary>
/// The Drivers page: five tabs over one read-only scan. Each tab keeps its own ticks; the bottom bar acts on the
/// visible tab only and always goes through the standard confirm (IFlowHost). Nothing is ever ticked for you.
/// </summary>
public partial class DriversView : UserControl, IFixedHeightPage
{
    private enum PageState { Ready, Scanning, Results }

    private readonly IServiceProvider _services;
    private readonly DriverScanner _scanner;
    private readonly Dictionary<string, SelectionModel> _models = new(StringComparer.Ordinal);
    private readonly HashSet<ItemRow> _ghostTicks = new();
    private DriverScanReport? _report;
    private CancellationTokenSource? _scanCts;
    private bool _running;
    private bool _enforcingSingleGhost;
    private bool _loadingToggle;

    public DriversView(IServiceProvider services, DriverScanner scanner)
    {
        InitializeComponent();
        _services = services;
        _scanner = scanner;
        foreach (var (id, _) in DriverTabs.All)
        {
            var model = new SelectionModel(showSectionHeaders: false);
            var tab = id;
            model.Changed += () => OnTicksChanged(tab);
            _models[id] = model;
        }
        Results.OpenInExplorerRequested += p => Host?.OpenInExplorer(p);
        Subtitle.Text = "Every device and its driver, problem devices, official updates, and old drivers and devices to tidy.";
        SetState(PageState.Ready);
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;
    private string DataRoot => (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.DataRoot
                               ?? (_services.GetService(typeof(AppSettings)) as AppSettings)?.DataRoot
                               ?? AppSettings.DefaultDataRoot;

    private string CurrentTab => DriverTabs.All[Math.Clamp(Tabs.SelectedIndex, 0, DriverTabs.All.Count - 1)].Id;

    // ------------------------------------------------------------------ scan

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = StartScanAsync();

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>Reads everything (public for screenshots and Home quick actions).</summary>
    public async Task StartScanAsync()
    {
        if (_scanCts is not null || _running) return;
        _scanCts = new CancellationTokenSource();
        SetState(PageState.Scanning);
        ScanMessage.Text = "Starting…";
        try
        {
            var ctx = CreateContext(new DriverScanOptions { IncludeImportantClasses = ImportantToggle.IsChecked == true });
            var progress = new Progress<ScanProgress>(p => ScanMessage.Text = p.Message);
            var token = _scanCts.Token;
            var report = await Task.Run(() => _scanner.ScanReportAsync(ctx, progress, token), token).ConfigureAwait(true);
            Load(report);
        }
        catch (OperationCanceledException)
        {
            SetState(_report is null ? PageState.Ready : PageState.Results);
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("Scan didn't finish", "Something went wrong while reading your drivers: " + ex.Message + " Nothing was changed.", isError: true);
            SetState(_report is null ? PageState.Ready : PageState.Results);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
        }
    }

    private ScanContext CreateContext(DriverScanOptions options)
    {
        var baseCtx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
        return new ScanContext
        {
            Locations = baseCtx.Locations,
            Safety = baseCtx.Safety,
            InUse = baseCtx.InUse,
            Settings = baseCtx.Settings,
            Walker = baseCtx.Walker,
            Depth = ScanDepth.Quick,
            RunningProcesses = baseCtx.RunningProcesses,
            System = baseCtx.System,
            Options = new Dictionary<string, object> { [DriverScanOptions.Key] = options },
        };
    }

    private void Load(DriverScanReport report)
    {
        _report = report;
        _ghostTicks.Clear();
        foreach (var (id, title) in DriverTabs.All)
        {
            var groups = report.Result.Groups.Where(g => DriverTabs.Of(g.Id) == id).ToList();
            var section = new SectionModel(DriverActions.FeatureId, title, SymbolRegular.DeveloperBoard24,
                report.Result with { Groups = groups });
            // Nothing in Drivers is ever pre-ticked; the first small group opens so the tab isn't just headers.
            _models[id].Load(new[] { section }, (_, _, _) => false, (s, g) => g == s.Groups.FirstOrDefault(x => x.Items.Count <= 60));
        }

        var inv = report.Inventory;
        int presentDevices = inv.HasPnpUtilXml ? inv.Devices.Count(d => d.IsPresent) : inv.WmiDrivers.Count;
        var inUse = StoreCleanupRules.InUseInfs(inv.Packages, inv.AllDevices);
        var parts = new List<string> { $"Scanned at {DateTime.Now:t}", $"{presentDevices:N0} device{PlanFacts.S(presentDevices)}" };
        if (inv.Packages.Count > 0)
            parts.Add($"{inv.Packages.Count} driver packages, {inv.Packages.Count(p => !inUse.Contains(p.PublishedName))} not in use");
        Subtitle.Text = string.Join(" · ", parts);

        UpdateTabLabels();
        SetState(PageState.Results);
    }

    /// <summary>"Problems (2)": counts are the rows you could act on (or the problems found).</summary>
    private void UpdateTabLabels()
    {
        if (_report is null) return;
        var labels = DriverTabs.All.Select(t =>
        {
            var groups = _report.Result.Groups.Where(g => DriverTabs.Of(g.Id) == t.Id).ToList();
            int n = t.Id switch
            {
                DriverTabs.Problems => groups.Where(g => !g.Id.EndsWith(".info", StringComparison.Ordinal)).Sum(g => g.Items.Count),
                DriverTabs.Devices => 0,
                _ => groups.SelectMany(g => g.Items).Count(i => i.DisabledReason is null && i.Actions.Any(a => a.Kind != ActionKind.OpenExternal)),
            };
            return n > 0 ? $"{t.Title} ({n.ToString("N0", CultureInfo.CurrentCulture)})" : t.Title;
        });
        int keep = Tabs.SelectedIndex;
        Tabs.Items = string.Join('|', labels);
        Tabs.SelectedIndex = keep;
    }

    // ------------------------------------------------------------------ tabs

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (_report is not null) ShowTab();
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
        if (tab == DriverTabs.Ghosts && !empty) note = (note is null ? "" : note + " ") + "Old devices are removed one at a time.";
        // Said once: an empty tab carries its note in the empty state, so the bar above stays closed.
        TabNote.Message = note ?? "";
        TabNote.IsOpen = note is not null && !empty;

        bool hasImportant = _report.Inventory.HasPnpUtilXml
            && _report.Result.Groups.Any(g => g.Id == DriverTabs.GroupId(DriverTabs.Store, "important"));
        ImportantPanel.Visibility = Vis(tab == DriverTabs.Store && hasImportant);

        // Nothing was checked when Windows didn't share the list: say so plainly, never a green "all good".
        bool unavailable = _report.UnavailableTabs.Contains(tab);
        TabEmpty.Icon = unavailable ? SymbolRegular.Info24 : SymbolRegular.CheckmarkCircle24;
        TabEmpty.Title = unavailable ? "Not available on this PC" : tab switch
        {
            DriverTabs.Problems => "No problem devices",
            DriverTabs.Updates => "No updates found",
            DriverTabs.Store => "Nothing to clean up",
            DriverTabs.Ghosts => "No old devices to tidy",
            _ => "Nothing to show",
        };
        TabEmpty.Message = note ?? "";

        StickyBar.Visibility = Vis(model.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).Any(i => i.IsSelectable));
        UpdateSelectionBar();
    }

    private void OnImportantToggled(object sender, RoutedEventArgs e)
    {
        if (_report is null || _loadingToggle) return;
        _loadingToggle = true;
        try
        {
            int tab = Tabs.SelectedIndex;
            Load(_scanner.Rebuild(_report.Inventory, DataRoot, new DriverScanOptions { IncludeImportantClasses = ImportantToggle.IsChecked == true }));
            Tabs.SelectedIndex = tab;
            ShowTab();
        }
        finally { _loadingToggle = false; }
    }

    // ------------------------------------------------------------------ ticks

    private void OnTicksChanged(string tab)
    {
        if (tab == DriverTabs.Ghosts) KeepOneGhost();
        if (_report is not null && tab == CurrentTab) UpdateSelectionBar();
    }

    /// <summary>Old devices go one at a time: ticking a second one unticks the first.</summary>
    private void KeepOneGhost()
    {
        if (_enforcingSingleGhost) return;
        var ticked = _models[DriverTabs.Ghosts].CheckedItems();
        var fresh = ticked.FirstOrDefault(r => !_ghostTicks.Contains(r)) ?? ticked.FirstOrDefault();
        _enforcingSingleGhost = true;
        try
        {
            foreach (var r in ticked.Where(r => r != fresh)) r.IsChecked = false;
        }
        finally { _enforcingSingleGhost = false; }
        _ghostTicks.Clear();
        if (fresh is not null && fresh.IsChecked) _ghostTicks.Add(fresh);
    }

    private IReadOnlyList<ScanItem> PickedItems() => _models[CurrentTab].CheckedItems().Where(r => r.IsSelectable).Select(r => r.Item).ToList();

    private void UpdateSelectionBar()
    {
        var picked = PickedItems();
        var plan = picked.Count == 0 ? null : DriverPlanBuilder.Build("Drivers", picked, BackupFolders.ForDate(DataRoot, DateTime.Now)).Plan;
        var items = plan?.Items ?? Array.Empty<PlanItem>();
        long bytes = picked.Sum(i => i.SizeBytes);
        SelectionIcon.HasSelection = picked.Count > 0;
        SelectedText.Text = picked.Count == 0
            ? "Nothing selected"
            : $"Selected {picked.Count:N0} item{PlanFacts.S(picked.Count)}" + (bytes > 0 ? " · " + Sizes.Format(bytes) : "");

        var extras = new List<string>();
        if (items.Any(DriverActions.IsExport))
            extras.Add(items.Any(i => i.DependsOn is { Count: > 0 }) ? "drivers backed up first (nothing changes if that fails)" : "drivers backed up first");
        if (items.Any(i => i.NeedsAdmin)) extras.Add("needs admin");
        if (items.Any(i => i.IsSystemChange)) extras.Add("restore point first");
        if (items.Any(i => !i.Reversible)) extras.Add("can't be undone from History");
        SelectedCaption.Text = extras.Count > 0 ? string.Join(" · ", extras) : picked.Count > 0 ? "Opens official pages only" : "Tick what you want, then confirm";

        PrimaryButton.Content = VerbFor(items);
        PrimaryButton.IsEnabled = items.Count > 0 && !_running;
        SimulateButton.IsEnabled = items.Count > 0 && !_running;
        AutomationProperties.SetName(PrimaryButton, (string)PrimaryButton.Content);
    }

    /// <summary>Button text that says exactly what the picked rows do.</summary>
    internal static string VerbFor(IReadOnlyList<PlanItem> items)
    {
        int deletes = items.Count(i => i.ToolId == ToolIds.PnpDeleteDriver);
        int removals = items.Count(DriverActions.IsDeviceRemoval);
        int updates = items.Where(DriverActions.IsWindowsUpdateInstall)
            .Sum(i => (i.Args?.GetValueOrDefault("updateIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
        int rollbacks = items.Count(i => i.ToolId == ToolIds.DriverRollback);
        int restores = items.Count(i => i.ToolId == ToolIds.PnpAddDrivers);
        int opens = items.Count(i => i.Kind == ActionKind.OpenExternal);
        int kinds = new[] { deletes, removals, updates, rollbacks, restores, opens }.Count(n => n > 0);

        if (items.Count == 0) return "Nothing selected";
        if (kinds > 1) return "Apply these changes";
        if (deletes > 0) return $"Remove {deletes} old package{PlanFacts.S(deletes)}";
        if (removals > 0) return "Remove this device";
        if (updates > 0) return $"Install {updates} update{PlanFacts.S(updates)}";
        if (rollbacks > 0) return $"Roll back {rollbacks} device{PlanFacts.S(rollbacks)}";
        if (restores > 0) return "Put drivers back";
        if (opens > 0) return opens == 1 ? "Open page" : $"Open {opens} pages";
        return "Back up drivers";
    }

    // ------------------------------------------------------------------ confirm / run / simulate

    private void OnPrimary(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: false);

    private void OnSimulate(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: true);

    /// <summary>Standard confirm → run (or simulate). A real run is followed by a fresh scan (public for screenshots).</summary>
    public async Task RunAsync(bool simulate)
    {
        if (_running) return;
        var built = DriverPlanBuilder.Build(TitleFor(CurrentTab), PickedItems(), BackupFolders.ForDate(DataRoot, DateTime.Now));
        if (built.Plan is not { } plan)
        {
            if (built.Problem is not null) Host?.ShowMessage("Pick again", built.Problem);
            return;
        }

        _running = true;
        UpdateSelectionBar();
        Steps.Current = 3;
        bool ran = false;
        try
        {
            var restart = plan.Items.Where(i => i.ToolId is ToolIds.WindowsUpdateInstallDrivers or ToolIds.DriverRollback or ToolIds.PnpAddDrivers)
                .Select(i => i.Id).ToHashSet();
            var options = new FlowOptions { Title = plan.Title, ShowResult = true, RestartItemIds = restart, PrimaryVerb = VerbFor(plan.Items) };
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
        // Drivers and devices changed: read them again so the tabs show the truth.
        if (ran) await StartScanAsync();
    }

    private static string TitleFor(string tab) => tab switch
    {
        DriverTabs.Updates => "Driver updates",
        DriverTabs.Store => "Driver store cleanup",
        DriverTabs.Ghosts => "Remove an old device",
        _ => "Drivers",
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
            ImportantPanel.Visibility = Visibility.Collapsed;
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
