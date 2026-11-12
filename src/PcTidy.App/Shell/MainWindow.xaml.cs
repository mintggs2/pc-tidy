using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PcTidy.App.Composition;
using PcTidy.App.Pages;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using PcTidy.UI.Theming;
using Wpf.Ui.Controls;

namespace PcTidy.App.Shell;

/// <summary>The PC Tidy window: Fluent + Mica, left rail built from the registered modules, overlays for onboarding and run progress.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppServices _services;
    private readonly PageProvider _pages = new();
    private readonly Dictionary<string, Type> _keys = new(StringComparer.OrdinalIgnoreCase);
    private OnboardingView? _onboarding;

    public MainWindow(AppServices services, bool screenshotMode)
    {
        _services = services;
        ScreenshotMode = screenshotMode;
        InitializeComponent();
        if (services.IsDemo)
        {
            DemoPill.Visibility = Visibility.Visible;
            DemoPill.ToolTip = services.DemoReason;
            AutomationProperties.SetName(DemoPill, services.DemoReason ?? "Demo");
        }
        if (screenshotMode)
        {
            // Mica can't be captured by RenderTargetBitmap: use the theme's solid background instead.
            WindowBackdropType = WindowBackdropType.None;
            RootGrid.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
            Left = 40; Top = 40;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        RefreshSummaryContext();
        FlowHost = new FlowHost(this, services);
        services.Add<IFlowHost>(FlowHost);
        services.Add<IShellFlowHost>(FlowHost);
        Navigation = new ShellNavigation(this, services);
        services.Add<INavigationService>(Navigation);
        services.Add<PcTidy.Features.Assistant.Engine.ISectionReviewHost>(Navigation);
        services.Add<IParameterNavigation>(Navigation);
        services.Add<IRunCheckLauncher>(Navigation);
        services.Add(RunGate.Shared);
        FlowHost.RunsChanged += () => RefreshBadgesAsync().Forget(FlowHost, "Couldn't update the rail");
        Updates = new UpdateFlow(new WindowUpdateUi(this, services), practice: services.IsDemo || services.Args.SandboxRoot is not null);

        RegisterBuiltInPages();
        BuildRail();
        RootNavigation.SetPageProviderService(_pages);
        RootNavigation.Navigated += (_, args) =>
        {
            OnPageShown(args.Page);
            LimitWidth(args.Page as FrameworkElement);
            Dispatcher.BeginInvoke(() => FixScrolling(args.Page as DependencyObject), DispatcherPriority.Loaded);
            // Opened from Home, a notification or another section: the rail shows where you are.
            Dispatcher.BeginInvoke(ShowSelectedRailItem, DispatcherPriority.Loaded);
            // WPF-UI's built-in ✕ buttons (InfoBar close, search box clear) have no screen-reader name of their own.
            Dispatcher.BeginInvoke(() => PcTidy.UI.Controls.AccessibleNames.NameWithin(args.Page as DependencyObject), DispatcherPriority.ContextIdle);
            SaveSettingsQuietly(); // pages keep options in AppSettings; leaving a page is a save point
        };
        Closing += OnClosing;
        StateChanged += (_, _) => NameTitleBarButtons();
        SizeChanged += (_, _) => ApplyRailDensity();
        Loaded += (_, _) =>
        {
            NameTitleBarButtons();
            ShowRailScrollBar();
            ApplyRailDensity();
            NavigateTo("home");
            if (!screenshotMode && !Settings.OnboardingDone) ShowOnboarding(true);
            RefreshBadgesAsync().Forget(FlowHost, "Couldn't update the rail");
        };
    }

    public bool ScreenshotMode { get; }

    /// <summary>The updater: the once-a-day check, "Check for updates" and the update notice.</summary>
    public UpdateFlow Updates { get; }

    /// <summary>True while the run progress overlay is up (a run, an undo, a download).</summary>
    public bool IsProgressShown => ProgressOverlay.Visibility == Visibility.Visible;

    /// <summary>
    /// True while a section or Optimize-everything is scanning. Scans only read, so they aren't on the one-task gate,
    /// but the updater waits for them: Setup closes PC Tidy and would cut a scan off.
    /// </summary>
    public bool IsScanning => _pages.Created.Any(p => (p is ScrollViewer { Content: { } inner } ? inner : p) is StandardFlowPage { State: FlowState.Scanning } or CombinedFlowPage { State: FlowState.Scanning });

    public ShellNavigation Navigation { get; }

    // ---------------- tray: hidden window, quit ----------------

    /// <summary>Set by the tray while its icon is shown: closing the window then hides it instead.</summary>
    public bool KeepInTray { get; set; }
    private bool _quitting;

    /// <summary>"Quit PC Tidy" from the tray: closes for real (still asks first when a run is in progress).</summary>
    public void Quit()
    {
        _quitting = true;
        ShowFromBackground(); // the "still working" question needs a visible window
        Close();
    }

    /// <summary>Shows the window if it's hidden in the tray or minimised, and brings it forward.</summary>
    public void ShowFromBackground()
    {
        if (!IsVisible) Show();
        Activate2();
    }

    /// <summary>
    /// Saves settings without bothering the user (a failure is written to the crash log). Skipped while the settings
    /// file couldn't be read and the user hasn't confirmed yet: saving then would replace their file with defaults.
    /// </summary>
    public void SaveSettingsQuietly()
    {
        if (Settings.LoadProblem is not null && !Settings.LoadProblemAcknowledged) return;
        try { _services.Get<ISettingsStore>().Save(Settings); }
        catch (Exception ex) { CrashHandler.Write(ex, "Saving settings", terminating: false); }
    }

    // ---------------- closing while PC Tidy is working ----------------

    private bool _closeAfterRun;

    /// <summary>
    /// Closing mid-run would leave a half-finished run (resumable, but confusing). Ask first; "Stop after this step"
    /// lets the current step finish, then closes. Nothing is ever cut off half-way through a step.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // With the tray icon on, the window's X only hides PC Tidy (Quit in the tray menu really closes it).
        if (KeepInTray && !_quitting && !_closeAfterRun)
        {
            e.Cancel = true;
            SaveSettingsQuietly();
            Hide();
            return;
        }
        if (_closeAfterRun || !FlowHost.IsWorking)
        {
            SaveSettingsQuietly();
            return;
        }
        e.Cancel = true;
        try
        {
            var ask = new ContentDialog(RootDialogHost)
            {
                Title = "PC Tidy is still working",
                Content = FlowHost.Body("Stop after the current step? Everything that already ran stays in History, where you can undo it. " +
                                        "Nothing is cut off half-way through a step."),
                PrimaryButtonText = "Stop after this step and close",
                CloseButtonText = "Keep working",
                DefaultButton = ContentDialogButton.Close,
            };
            // The progress overlay already shows a dialog-free state; show this one directly (not through the dialog gate).
            if (await ask.ShowAsync() != ContentDialogResult.Primary)
            {
                _quitting = false; // "Keep working": the next X hides to the tray again
                return;
            }
            FlowHost.StopAfterCurrentStep();
            SetProgressTitle("Stopping after this step, then closing…");
            await FlowHost.WhenIdleAsync();
            _closeAfterRun = true;
            Close();
        }
        catch (Exception ex)
        {
            CrashHandler.Write(ex, "Closing while a run was in progress", terminating: false);
        }
    }

    /// <summary>WPF-UI's title-bar buttons have no accessible names: screen readers would say just "button".</summary>
    private void NameTitleBarButtons()
    {
        foreach (var b in Descendants(AppTitleBar).OfType<TitleBarButton>())
        {
            var name = b.ButtonType switch
            {
                TitleBarButtonType.Minimize => "Minimize",
                TitleBarButtonType.Maximize or TitleBarButtonType.Restore => WindowState == WindowState.Maximized ? "Restore" : "Maximize",
                TitleBarButtonType.Close => "Close",
                TitleBarButtonType.Help => "Help",
                _ => null,
            };
            if (name is null) continue;
            AutomationProperties.SetName(b, name);
            b.ToolTip ??= name;
        }
    }

    /// <summary>
    /// The rail scrolls when its sections don't fit (small windows, more sections). WPF-UI's rail uses a scroll bar that
    /// only shows while scrolling, so a hidden last item gave no hint; the standard thin scroll bar stays visible.
    /// </summary>
    /// <summary>Scrolls the rail so the open section's item is in view (it can be below the fold on short windows).</summary>
    private void ShowSelectedRailItem()
    {
        if (RootNavigation.SelectedItem is FrameworkElement item && item.IsLoaded) item.BringIntoView();
    }

    /// <summary>Window heights below which the rail gets denser, so the whole "You" group fits at 1280×800.</summary>
    internal const double DenseRailBelowHeight = 900;

    private bool? _denseRail;

    /// <summary>
    /// v3 has 24 sections plus 3 footer items. Below 900 px the rows sit closer together (28 → 24 px apart) and group
    /// headers get tighter, which brings the whole "You" group into view at 800 px high; on smaller windows the rail
    /// scrolls (with a visible bar) and the open section is scrolled into view.
    /// GOTCHA (WPF-UI 4.3): a NavigationViewItem shorter than 28 px clips its text, so rows keep their height and
    /// overlap by 2 px top and bottom instead.
    /// </summary>
    private void ApplyRailDensity()
    {
        bool dense = ActualHeight > 0 && ActualHeight < DenseRailBelowHeight;
        if (_denseRail == dense) return;
        _denseRail = dense;
        foreach (var list in new[] { RootNavigation.MenuItems, RootNavigation.FooterMenuItems, (System.Collections.IList?)_moreItem?.MenuItems ?? Array.Empty<object>() })
            foreach (var o in list)
            {
                if (o is NavigationViewItem item) item.Margin = dense ? new Thickness(0, -2, 0, -2) : new Thickness(0);
                else if (o is NavigationViewItemHeader header)
                {
                    header.Margin = dense ? new Thickness(14, 4, 0, 0) : new Thickness(14, 6, 0, 1);
                    header.FontSize = dense ? 10.5 : 11;
                }
            }
        Dispatcher.BeginInvoke(ShowSelectedRailItem, DispatcherPriority.Loaded);
    }

    private void ShowRailScrollBar()
    {
        RootNavigation.ApplyTemplate();
        if (Descendants(RootNavigation).OfType<DynamicScrollViewer>().FirstOrDefault(s => s.Name == "PART_ScrollViewer") is { } rail)
            rail.SetResourceReference(StyleProperty, typeof(ScrollViewer));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            for (int i = VisualTreeHelper.GetChildrenCount(cur) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
    }

    /// <summary>On wide screens content stops at about 1280 px (plus page margins) so sizes don't sit far from their names.</summary>
    private static void LimitWidth(FrameworkElement? page)
    {
        if (page is null) return;
        page.MaxWidth = 1280 + 64;
        // Stretch (not Left): the page fills the pane up to MaxWidth, and is centred beyond it.
        page.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
    public FlowHost FlowHost { get; }

    /// <summary>Tells run summaries where quarantine lives (same drive = space freed only when emptied) and how long it keeps things.</summary>
    public void RefreshSummaryContext()
        => RunSummaryContext.Current = new RunSummaryContext(DataPaths.Quarantine(Settings.DataRoot), Settings.QuarantineDays);
    public PageProvider Pages => _pages;
    public ContentDialogHost DialogHost => RootDialogHost;
    private AppSettings Settings => _services.Get<AppSettings>();

    // ---------------- pages & rail ----------------

    private void RegisterBuiltInPages()
    {
        Key("home", typeof(HomePage), () => new HomePage(_services));
        Key("history", typeof(HistoryPage), () => new HistoryPage(_services));
        Key("quarantine", typeof(QuarantinePage), () => new QuarantinePage(_services));
        Key("settings", typeof(SettingsPage), () => new SettingsPage(_services, this));
        Key("optimize", typeof(CombinedFlowPage), () => new CombinedFlowPage(_services, OptimizeSections));
    }

    private void Key(string id, Type type, Func<object> factory)
    {
        _keys[id] = type;
        _pages.Register(type, factory);
    }

    /// <summary>Visible modules: current release, and Expert sections only with Expert mode on.</summary>
    public IReadOnlyList<IFeatureModule> Modules =>
        _services.VisibleModules.Where(m => m is not IModuleRiskInfo { Risk: ModuleRisk.Expert } || Settings.ExpertMode).ToList();

    /// <summary>Every scanner that takes part in Optimize-everything, from the visible modules.</summary>
    public IReadOnlyList<OptimizeSection> OptimizeSections() =>
        Modules.SelectMany(m => m.Scanners.Where(s => s.IncludedInOptimizeAll && s.Release <= _services.CurrentRelease)
                                         .Select(s => new OptimizeSection(Tracked(s), m.Scanners.Count > 1 ? s.Title : m.Title, IconOf(m))))
               .ToList();

    /// <summary>
    /// Scanners the flow pages run are wrapped so each finished scan is remembered for "Ask PC Tidy" (in memory only).
    /// The wrapper behaves exactly like the scanner (same id, title, release, preview wording).
    /// </summary>
    private IFeatureScanner Tracked(IFeatureScanner s)
        => _services.GetService(typeof(PcTidy.Features.Assistant.Engine.IScanResultsCache)) is PcTidy.Features.Assistant.Engine.ScanResultsCache cache ? cache.Track(s) : s;

    /// <summary>Feature pages with their own "go to another section" links get group-aware navigation.</summary>
    private object? Hooked(object? page, IFeatureModule m)
    {
        if (page is PcTidy.Features.Privacy.UI.PrivacyFlowPage privacy)
        {
            privacy.NavigationRequested += r => Navigation.Open(r.ModuleId, null, r.GroupId);
            privacy.Flow.FlowId = m.Id; // part-way scans are saved, and resumed from Home, under the section's id
        }
        else if (page is StandardFlowPage flow) flow.FlowId = m.Id;
        return page;
    }

    /// <summary>The standard flow of a section (its own page or the one inside a Privacy-style page), or null.</summary>
    public StandardFlowPage? FlowPageFor(string key) => PageObject(key) switch
    {
        StandardFlowPage flow => flow,
        PcTidy.Features.Privacy.UI.PrivacyFlowPage privacy => privacy.Flow,
        _ => null,
    };

    /// <summary>The page object of a section (without the scroll wrapper the shell may have added).</summary>
    public object? PageObject(string key) => PageFor<object>(key) is ScrollViewer { Content: { } inner } ? inner : PageFor<object>(key);

    public static SymbolRegular IconOf(IFeatureModule m) => Enum.TryParse<SymbolRegular>(m.Icon, out var s) ? s : SymbolRegular.Box24;

    /// <summary>(Re)builds the left rail. Called again when Expert mode changes.</summary>
    public void BuildRail()
    {
        RootNavigation.MenuItems.Clear();
        RootNavigation.FooterMenuItems.Clear();
        var modules = Modules;

        _railItems.Clear();
        _railHeaders.Clear();
        _moreItem = null;
        _moreHeader = null;

        RootNavigation.MenuItems.Add(Item("Home", SymbolRegular.Home24, typeof(HomePage)));
        // Layout spec §4.2: the groups, then "More" (rarely used sections) at the end, so the rail starts at about 16 rows.
        foreach (var row in RailLayout.Build(modules))
        {
            switch (row.Kind)
            {
                case RailEntryKind.Header:
                    var h = new NavigationViewItemHeader { Text = row.Text };
                    AutomationProperties.SetName(h, "Group: " + System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(row.Text.ToLowerInvariant()));
                    _railHeaders.Add(h);
                    RootNavigation.MenuItems.Add(h);
                    break;
                case RailEntryKind.Module:
                    RootNavigation.MenuItems.Add(TrackedItem(row.Module!));
                    break;
                case RailEntryKind.More:
                    RootNavigation.MenuItems.Add(MoreItem(row.MoreChildren));
                    break;
            }
        }
        foreach (var m in modules.Where(m => m.Group == NavGroup.Footer)) RootNavigation.FooterMenuItems.Add(TrackedItem(m));
        RootNavigation.FooterMenuItems.Add(_historyItem = Item("History", SymbolRegular.History24, typeof(HistoryPage)));
        RootNavigation.FooterMenuItems.Add(_quarantineItem = Item("Quarantine", SymbolRegular.ArchiveArrowBack24, typeof(QuarantinePage)));
        RootNavigation.FooterMenuItems.Add(_settingsItem = Item("Settings", SymbolRegular.Settings24, typeof(SettingsPage)));
        _denseRail = null; // new items: apply the density again
        ApplyRailDensity();
        ShowBadges();
    }

    // ---------------- rail: More, counters ----------------

    private readonly Dictionary<string, NavigationViewItem> _railItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NavigationViewItemHeader> _railHeaders = new();
    private NavigationViewItem? _moreItem, _historyItem, _quarantineItem, _settingsItem;
    private FrameworkElement? _moreHeader;
    private bool _settingMore;
    private IReadOnlyDictionary<string, string?> _badges = new Dictionary<string, string?>();
    private int _historyUnseen;

    private NavigationViewItem TrackedItem(IFeatureModule m)
    {
        var item = ModuleItem(m);
        _railItems[m.Id] = item;
        return item;
    }

    /// <summary>
    /// "More" at the end of the rail: a NavigationViewItem with its own sections (WPF-UI opens and closes it with a click,
    /// Enter or Space). Its open state is remembered, and it opens by itself when the open section is inside it.
    /// </summary>
    private NavigationViewItem MoreItem(IReadOnlyList<IFeatureModule> children)
    {
        // GOTCHA (WPF-UI 4.3): the rail style's fixed 28 px height would clip the open list, so "More" sizes itself; its
        // header row is 40 px in the template, so its bottom is pulled in to line the first section up with the other rows.
        var more = new NavigationViewItem { Content = RailLayout.MoreText, Icon = new SymbolIcon(SymbolRegular.MoreHorizontal24), Height = double.NaN, MinHeight = 28 };
        more.Loaded += (_, _) =>
        {
            more.ApplyTemplate();
            if (more.Template?.FindName("MainBorder", more) is FrameworkElement header)
            {
                header.Margin = new Thickness(0, 0, 0, -12);
                _moreHeader = header;
            }
        };
        foreach (var m in children)
        {
            var child = TrackedItem(m);
            child.Height = 28;
            child.MinHeight = 28;
            more.MenuItems.Add(child);
        }
        var names = string.Join(", ", children.Select(c => c.Title));
        AutomationProperties.SetName(more, $"More sections: {names}");
        AutomationProperties.SetHelpText(more, "Press Enter or Space to open or close.");
        more.ToolTip = "Sections you'll use now and then: " + names;
        more.IsExpanded = ShellState.Get(Settings).RailMoreOpen;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(NavigationViewItem.IsExpandedProperty, typeof(NavigationViewItem))
            .AddValueChanged(more, (_, _) =>
            {
                if (_settingMore) return;
                ShellState.Update(Settings, s => s with { RailMoreOpen = more.IsExpanded });
                SaveSettingsQuietly();
            });
        _moreItem = more;
        return more;
    }

    /// <summary>Whether the rail's "More" is open right now.</summary>
    public bool IsMoreOpen => _moreItem?.IsExpanded == true;

    /// <summary>Opens "More" when the shown section is inside it (not remembered: that stays the user's own choice).</summary>
    private void OpenMoreFor(string? moduleId)
    {
        if (_moreItem is null || _moreItem.IsExpanded || !RailLayout.ShouldOpenMore(false, moduleId)) return;
        _settingMore = true;
        try { _moreItem.IsExpanded = true; }
        finally { _settingMore = false; }
    }

    /// <summary>Screenshots and the tour: open or close "More" as the user would (remembered).</summary>
    public void SetMoreOpen(bool open)
    {
        if (_moreItem is not null) _moreItem.IsExpanded = open;
    }

    /// <summary>
    /// Fetches the rail counters: every <see cref="INavBadgeSource"/> section (off the UI thread, 5 s limit, a failure
    /// shows nothing) and History's undo-able runs since it was last opened. Called at start and after each run.
    /// </summary>
    public async Task RefreshBadgesAsync()
    {
        var modules = Modules;
        IReadOnlyDictionary<string, string?> badges;
        try { badges = await new NavBadgeFetcher().FetchAsync(modules, CancellationToken.None); }
        catch (Exception) { badges = new Dictionary<string, string?>(); }
        int unseen = 0;
        try
        {
            var state = ShellState.Get(Settings);
            if (state.HistoryViewedUtc is null)
            {
                // First start with counters: what is already in History counts as seen.
                ShellState.Update(Settings, s => s with { HistoryViewedUtc = DateTime.UtcNow });
            }
            else if (_services.GetService(typeof(IRunJournal)) is IRunJournal journal)
            {
                var runs = await Task.Run(() => journal.ListAsync(CancellationToken.None));
                unseen = HistoryBadge.Count(runs, state.HistoryViewedUtc);
            }
        }
        catch (Exception) { unseen = 0; } // History unreadable: no counter
        _badges = badges;
        _historyUnseen = unseen;
        ShowBadges();
    }

    private void ShowBadges()
    {
        foreach (var (id, item) in _railItems)
        {
            var m = Modules.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (m is null) continue;
            SetBadge(item, m.Title, _badges.TryGetValue(id, out var b) ? b : null, null);
        }
        if (_historyItem is not null)
            SetBadge(_historyItem, "History", HistoryBadge.Text(_historyUnseen), _historyUnseen > 0 ? HistoryBadge.AccessibleText(_historyUnseen) : null);
    }

    /// <summary>The rail counter of a section ("7 of 31 on"), or null. For tests and screenshots.</summary>
    public string? BadgeOf(string key) => key.Equals("history", StringComparison.OrdinalIgnoreCase) ? HistoryBadge.Text(_historyUnseen)
        : _badges.TryGetValue(key, out var b) ? b : null;

    /// <summary>A rail row's text with its counter on the right (or just the text). The accessible name says both.</summary>
    private static void SetBadge(NavigationViewItem item, string title, string? badge, string? spoken)
    {
        if (badge is null)
        {
            if (item.Content is not string) item.Content = title;
            AutomationProperties.SetName(item, title);
            return;
        }
        // The section name wins: it always gets its full width, and a long counter is the part that gets cut (with the
        // whole counter in its tooltip and in the accessible name).
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new System.Windows.Controls.TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var pill = new PcTidy.UI.Controls.BadgePill { Text = badge, Tone = PcTidy.UI.Controls.PillTone.Neutral, ShowDot = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 1), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = badge };
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
        item.Content = grid;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(item, $"{title}, {spoken ?? badge}");
    }

    /// <summary>Called for every page the rail shows: "More" follows, History clears its counter, parameters reach the page.</summary>
    private void OnPageShown(object? page)
    {
        var key = KeyOf(page);
        _shownKey = key;
        OpenMoreFor(key);
        if (key == "history")
        {
            ShellState.Update(Settings, s => s with { HistoryViewedUtc = DateTime.UtcNow });
            _historyUnseen = 0;
            ShowBadges();
        }
        var inner = page is ScrollViewer { Content: { } c } ? c : page;
        if (inner is INavigationTarget target)
        {
            var parameter = _pendingParameter is { } p && p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? p.Value : null;
            _pendingParameter = null;
            try { target.OnNavigatedTo(parameter); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CrashHandler.Write(ex, "Opening a section with a parameter", terminating: false); }
        }
    }

    private KeyValuePair<string, object?>? _pendingParameter;

    /// <summary>
    /// Opens a section and hands its page <paramref name="parameter"/> (an <see cref="INavigationTarget"/> gets it in
    /// OnNavigatedTo). A page that is already showing gets it straight away.
    /// </summary>
    public void NavigateWithParameter(string key, object? parameter)
    {
        _pendingParameter = new KeyValuePair<string, object?>(key, parameter);
        NavigateTo(key);
        if (_pendingParameter is null) return; // delivered by OnPageShown
        // Already on that page (WPF-UI doesn't navigate again): deliver directly.
        if (PageObject(key) is INavigationTarget target && _shownKey is { } shown && shown.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            _pendingParameter = null;
            target.OnNavigatedTo(parameter);
        }
    }

    private string? _shownKey;

    /// <summary>The key of the page on screen ("home", a module id…).</summary>
    public string? ShownKey => _shownKey;

    /// <summary>The shell key ("home", "history", a module id…) of a page object, or null.</summary>
    private string? KeyOf(object? page)
    {
        if (page is null) return null;
        foreach (var (key, type) in _keys)
            if (_pages.IsCreated(type, out var created) && (ReferenceEquals(created, page) || created is ScrollViewer { Content: var c } && ReferenceEquals(c, page)))
                return key;
        return null;
    }

    /// <summary>Rail elements the spotlight tour points at ("rail.groups", "rail.more", "rail.history"…), or null.</summary>
    public FrameworkElement? RailTarget(string id) => id switch
    {
        "rail.history" => _historyItem,
        "rail.quarantine" => _quarantineItem,
        "rail.settings" => _settingsItem,
        // The "More" row itself (not its open list, which can run off the bottom of a short window).
        "rail.more" => _moreHeader is { IsLoaded: true } h ? h : _moreItem,
        "rail.groups" => _railHeaders.FirstOrDefault(h => h.IsLoaded),
        _ => null,
    };

    /// <summary>The rail's sections from the first group header to the last section (the tour's "groups" step).</summary>
    public IReadOnlyList<FrameworkElement> RailGroupElements()
        => RootNavigation.MenuItems.OfType<FrameworkElement>().Skip(1).Where(e => !ReferenceEquals(e, _moreItem) && e.IsLoaded).ToList();

    private NavigationViewItem ModuleItem(IFeatureModule m)
    {
        var key = NavKeys.For(m.Id);
        if (!_keys.ContainsKey(m.Id))
            Key(m.Id, key, () => SafePage(m, () => ScrollIfNeeded(Hooked(m.CreatePage(_services), m)) ?? new StandardFlowPage(_services, m.Title, m.Summary, IconOf(m), m.Scanners.Select(Tracked).ToList(),
                (m as IModuleRiskInfo)?.Risk, (m as IModuleFlowText)?.PreviewNote) { FlowId = m.Id }));
        var item = Item(m.Title, IconOf(m), key);
        item.ToolTip = m.Summary;
        return item;
    }

    /// <summary>
    /// A section whose page throws while it is created (a XAML resource missing in this build, a bad setting) shows a
    /// calm card instead of the app-wide error dialog; the error goes to the log. The other sections keep working.
    /// </summary>
    internal static object SafePage(IFeatureModule m, Func<object> create)
    {
        try { return create(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashHandler.Write(ex, $"Opening the {m.Title} section", terminating: false);
            return PageFailedCard(m.Title);
        }
    }

    internal static FrameworkElement PageFailedCard(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new SymbolIcon(SymbolRegular.Warning24) { FontSize = 32, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
        var heading = new System.Windows.Controls.TextBlock { Text = "This section couldn't open", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Children.Add(heading);
        var body = new System.Windows.Controls.TextBlock
        {
            Text = $"Something inside {title} went wrong while it was loading, so it was left closed. Nothing on your PC was changed. " +
                   "The rest of PC Tidy works as usual. The details were saved to PC Tidy's log.",
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(body);
        AutomationProperties.SetName(panel, $"{title} couldn't open");
        return panel;
    }

    /// <summary>
    /// WPF-UI only gives WPF Page objects an outer ScrollViewer. Custom module pages that are plain controls (and not
    /// IFixedHeightPage) get one here so long content can scroll.
    /// </summary>
    private static object? ScrollIfNeeded(object? page) => page is FrameworkElement fe and not Page and not IFixedHeightPage
        ? new ScrollViewer { Content = fe, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }
        : page;

    private static NavigationViewItem Item(string title, SymbolRegular icon, Type key)
    {
        var item = new NavigationViewItem { Content = title, Icon = new SymbolIcon(icon), TargetPageType = key };
        AutomationProperties.SetName(item, title);
        return item;
    }

    /// <summary>"home", "history", "quarantine", "settings", "optimize" or a module id.</summary>
    public void NavigateTo(string key)
    {
        if (!_keys.TryGetValue(key, out var type))
        {
            var m = Modules.FirstOrDefault(x => x.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (m == null) return;
            ModuleItem(m);
            type = _keys[m.Id];
        }
        RootNavigation.Navigate(type);
    }

    /// <summary>Opens Settings scrolled to "help" or "about".</summary>
    public void OpenSettingsSection(string section)
    {
        NavigateTo("settings");
        PageFor<SettingsPage>("settings")?.ShowSection(section);
    }

    public bool HasPage(string key) => _keys.ContainsKey(key) || Modules.Any(m => m.Id.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets a page instance by key (creating it), e.g. to start a scan from Home.</summary>
    public T? PageFor<T>(string key) where T : class
    {
        if (!_keys.ContainsKey(key) && Modules.FirstOrDefault(m => m.Id.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } m) ModuleItem(m);
        return _keys.TryGetValue(key, out var t) ? _pages.Get<T>(t) : null;
    }

    /// <summary>
    /// GOTCHA (WPF-UI 4.3): pages are hosted in the NavigationView's ScrollViewer, which measures with infinite
    /// height, so "*" rows, sticky bottom bars and list virtualization break. Fixed-height pages get the outer
    /// vertical scrolling turned off; normal pages keep it.
    /// </summary>
    private static void FixScrolling(DependencyObject? page)
    {
        if (page == null) return;
        DependencyObject? cur = VisualTreeHelper.GetParent(page);
        while (cur != null && cur is not ScrollViewer) cur = VisualTreeHelper.GetParent(cur);
        if (cur is ScrollViewer sv)
        {
            sv.VerticalScrollBarVisibility = page is IFixedHeightPage ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            sv.ScrollToTop();
        }
    }

    // ---------------- overlays ----------------

    public void ShowOnboarding(bool show)
    {
        if (show)
        {
            _onboarding = new OnboardingView(_services);
            _onboarding.Finished += () => ShowOnboarding(false);
            OnboardingOverlay.Children.Clear();
            OnboardingOverlay.Children.Add(_onboarding);
            OnboardingOverlay.Visibility = Visibility.Visible;
            _onboarding.FocusFirst();
        }
        else
        {
            OnboardingOverlay.Visibility = Visibility.Collapsed;
            OnboardingOverlay.Children.Clear();
            _onboarding = null;
        }
    }

    public OnboardingView? Onboarding => _onboarding;

    // ---------------- spotlight tour (Help and Settings) ----------------

    private TourOverlay? _tour;

    /// <summary>The tour on screen, or null.</summary>
    public TourOverlay? Tour => _tour;

    /// <summary>
    /// Starts the replayable tour on Home: it dims everything except one part at a time (Scan everything, the groups,
    /// More, History, Quarantine, Settings, Simulate first). Esc or Close ends it; it never presses anything.
    /// </summary>
    public void StartTour()
    {
        if (_onboarding is not null) ShowOnboarding(false);
        EndTour();
        NavigateTo("home");
        var model = new TourModel();
        model.Changed += () => PrepareTourTarget(model.Current.Target);
        model.Closed += EndTour;
        _tour = new TourOverlay(model, TourTargetBounds);
        TourLayer.Children.Clear();
        TourLayer.Children.Add(_tour);
        TourLayer.Visibility = Visibility.Visible;
        PrepareTourTarget(model.Current.Target);
        Dispatcher.BeginInvoke(() => _tour?.Focus(), DispatcherPriority.Loaded);
    }

    private void EndTour()
    {
        if (_tour is null) return;
        _tour = null;
        TourLayer.Children.Clear();
        TourLayer.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => RootNavigation.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Scrolls the step's target into view first (the rail scrolls on short windows).</summary>
    private void PrepareTourTarget(string? id)
    {
        if (id is null) return;
        if (id == "home.scan" && ShownKey != "home") NavigateTo("home");
        TourElement(id)?.BringIntoView();
    }

    private FrameworkElement? TourElement(string id) => id switch
    {
        "home.scan" => PageFor<HomePage>("home")?.OptimizeButton,
        _ => RailTarget(id),
    };

    /// <summary>Where the step's target is on the overlay (the groups step covers every group header and section).</summary>
    private Rect? TourTargetBounds(string id)
    {
        try
        {
            if (id == "rail.groups")
            {
                Rect? all = null;
                foreach (var e in RailGroupElements())
                    if (Bounds(e) is { } b) all = all is { } a ? Rect.Union(a, b) : b;
                // Only the part of the rail that is on screen.
                if (all is { } u && RootNavigation.IsLoaded && Bounds(RootNavigation) is { } nav) { u.Intersect(nav); return u.IsEmpty ? null : u; }
                return all;
            }
            if (id == "rail.more" && TourElement(id) is { } moreRow && ReferenceEquals(moreRow, _moreHeader) && Bounds(moreRow) is { } row)
            {
                // The header's template row is 40 px with its bottom pulled in by 12 px: ring the 28 px row people see.
                row.Height = Math.Min(row.Height, 28);
                if (RootNavigation.IsLoaded && Bounds(RootNavigation) is { } nav) row.Intersect(nav);
                return row.IsEmpty ? null : row;
            }
            return TourElement(id) is { } el ? Bounds(el) : null;
        }
        catch (InvalidOperationException) { return null; } // not in the same visual tree (yet)
    }

    private Rect? Bounds(FrameworkElement e)
    {
        if (!e.IsLoaded || !e.IsVisible || e.ActualWidth <= 0) return null;
        return e.TransformToAncestor(RootGrid).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
    }

    /// <summary>Shows the run progress overlay; returns a token that "Stop after this step" cancels.</summary>
    public CancellationToken ShowProgress(string title, bool canStop, string stopText = "Stop after this step")
    {
        // One overlay at a time: the previous token source (if any) is finished with.
        _progressCts?.Dispose();
        var cts = _progressCts = new CancellationTokenSource();
        ProgressTitle.Text = title;
        ProgressMessage.Text = "Starting…";
        ProgressCount.Text = "";
        ProgressBar.Value = 0;
        ProgressStop.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        ProgressStop.IsEnabled = true;
        ProgressStop.Content = stopText;
        AutomationProperties.SetName(ProgressStop, stopText);
        ShowProgressSections(null, savedLine: false);
        RoutedEventHandler? handler = null;
        handler = (_, _) => { cts.Cancel(); ProgressStop.IsEnabled = false; ProgressStop.Content = "Stopping…"; };
        ProgressStop.Click += handler;
        _stopHandler = handler;
        ProgressOverlay.Visibility = Visibility.Visible;
        return cts.Token;
    }

    private RoutedEventHandler? _stopHandler;
    private CancellationTokenSource? _progressCts;

    public void ReportProgress(RunProgress p)
    {
        ProgressMessage.Text = p.Message;
        if (p.StepCount > 0)
        {
            ProgressBar.Value = 100.0 * (p.StepIndex + 1) / p.StepCount;
            ProgressCount.Text = $"Step {p.StepIndex + 1:N0} of {p.StepCount:N0}";
        }
    }

    public void SetProgressTitle(string title) => ProgressTitle.Text = title;

    private ProgressSections? _sections;

    /// <summary>
    /// Shows the run's sections with their states (null = no list, for single-section runs) and, for real runs, the line
    /// "Everything done so far is already saved and can be undone." (layout spec §4.3).
    /// </summary>
    public void ShowProgressSections(ProgressSections? sections, bool savedLine)
    {
        if (_sections is not null) _sections.Changed -= DrawProgressSections;
        _sections = sections;
        if (sections is not null) sections.Changed += DrawProgressSections;
        ProgressSaved.Text = ProgressSections.SavedLine;
        ProgressSaved.Visibility = savedLine ? Visibility.Visible : Visibility.Collapsed;
        DrawProgressSections();
    }

    /// <summary>The section list on the progress overlay right now (screenshots and tests).</summary>
    public ProgressSections? ProgressSectionList => _sections;

    private void DrawProgressSections()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(DrawProgressSections); return; }
        ProgressSectionsPanel.Children.Clear();
        ProgressSectionsScroll.Visibility = _sections is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (_sections is null) return;
        foreach (var s in _sections.Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var label = ProgressSections.Label(s.State);
            var pill = new PcTidy.UI.Controls.BadgePill
            {
                Text = label,
                MinWidth = 104,
                Tone = s.State switch
                {
                    SectionRunState.Done => PcTidy.UI.Controls.PillTone.Success,
                    SectionRunState.Running => PcTidy.UI.Controls.PillTone.Accent,
                    SectionRunState.NeedsRestart => PcTidy.UI.Controls.PillTone.Caution,
                    _ => PcTidy.UI.Controls.PillTone.Neutral,
                },
            };
            DockPanel.SetDock(pill, Dock.Right);
            row.Children.Add(pill);
            var text = new System.Windows.Controls.TextBlock { Text = s.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.SetResourceReference(StyleProperty, s.State == SectionRunState.Running ? "BodyStrong" : "Body");
            row.Children.Add(text);
            AutomationProperties.SetName(row, $"{s.Title}: {label}");
            ProgressSectionsPanel.Children.Add(row);
        }
    }

    public void HideProgress()
    {
        if (_stopHandler != null) ProgressStop.Click -= _stopHandler;
        _stopHandler = null;
        // The run's linked token source already captured this token; nothing cancels it after the overlay closes.
        _progressCts?.Dispose();
        _progressCts = null;
        ProgressOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Brings the window forward (second instance started).</summary>
    public void Activate2()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }

    public void ApplyTheme(ThemePreference pref) => TidyTheme.ApplyPreference(pref, ScreenshotMode ? null : this);
}
