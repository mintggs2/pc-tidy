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

        RegisterBuiltInPages();
        BuildRail();
        RootNavigation.SetPageProviderService(_pages);
        RootNavigation.Navigated += (_, args) =>
        {
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
        };
    }

    public bool ScreenshotMode { get; }

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
        foreach (var list in new[] { RootNavigation.MenuItems, RootNavigation.FooterMenuItems })
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
    private object? Hooked(object? page)
    {
        if (page is PcTidy.Features.Privacy.UI.PrivacyFlowPage privacy)
            privacy.NavigationRequested += r => Navigation.Open(r.ModuleId, null, r.GroupId);
        return page;
    }

    /// <summary>The page object of a section (without the scroll wrapper the shell may have added).</summary>
    public object? PageObject(string key) => PageFor<object>(key) is ScrollViewer { Content: { } inner } ? inner : PageFor<object>(key);

    public static SymbolRegular IconOf(IFeatureModule m) => Enum.TryParse<SymbolRegular>(m.Icon, out var s) ? s : SymbolRegular.Box24;

    /// <summary>(Re)builds the left rail. Called again when Expert mode changes.</summary>
    public void BuildRail()
    {
        RootNavigation.MenuItems.Clear();
        RootNavigation.FooterMenuItems.Clear();
        var modules = Modules;

        RootNavigation.MenuItems.Add(Item("Home", SymbolRegular.Home24, typeof(HomePage)));
        foreach (var (group, header) in NavGroups.Body)
        {
            var inGroup = modules.Where(m => m.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            if (header.Length > 0)
            {
                var h = new NavigationViewItemHeader { Text = header };
                AutomationProperties.SetName(h, "Group: " + System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(header.ToLowerInvariant()));
                RootNavigation.MenuItems.Add(h);
            }
            foreach (var m in inGroup) RootNavigation.MenuItems.Add(ModuleItem(m));
        }
        foreach (var m in modules.Where(m => m.Group == NavGroup.Footer)) RootNavigation.FooterMenuItems.Add(ModuleItem(m));
        RootNavigation.FooterMenuItems.Add(Item("History", SymbolRegular.History24, typeof(HistoryPage)));
        RootNavigation.FooterMenuItems.Add(Item("Quarantine", SymbolRegular.ArchiveArrowBack24, typeof(QuarantinePage)));
        RootNavigation.FooterMenuItems.Add(Item("Settings", SymbolRegular.Settings24, typeof(SettingsPage)));
        _denseRail = null; // new items: apply the density again
        ApplyRailDensity();
    }

    private NavigationViewItem ModuleItem(IFeatureModule m)
    {
        var key = NavKeys.For(m.Id);
        if (!_keys.ContainsKey(m.Id))
            Key(m.Id, key, () => SafePage(m, () => ScrollIfNeeded(Hooked(m.CreatePage(_services))) ?? new StandardFlowPage(_services, m.Title, m.Summary, IconOf(m), m.Scanners.Select(Tracked).ToList(),
                (m as IModuleRiskInfo)?.Risk, (m as IModuleFlowText)?.PreviewNote)));
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
