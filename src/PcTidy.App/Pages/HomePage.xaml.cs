using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using PcTidy.App.Composition;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using PcTidy.UI.Flow.Resume;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Pages;

/// <summary>
/// Home dashboard: Optimize-everything, the clean-up estimate and health score (both only after "Check now", never
/// automatic), drives, last run, quarantine, quick actions; banners for a settings-file problem and unfinished runs.
/// </summary>
public partial class HomePage : UserControl
{
    private readonly AppServices _services;
    private RunRecord? _lastRun;
    private IReadOnlyList<RunRecord> _lastRunGroup = Array.Empty<RunRecord>();
    private IReadOnlyList<RunRecord> _unfinished = Array.Empty<RunRecord>();
    private IReadOnlyList<DriveDetails> _drives = Array.Empty<DriveDetails>();
    private long? _reclaimable;
    /// <summary>Windows services something switched off (the Services repair's finding) at the last check; null = not read.</summary>
    private int? _switchedOffServices;
    private DateTime _checkedAt;
    private bool _checking;
    private bool _refreshing;
    private string? _cleanupModuleId;
    private bool _protectionOff;

    public HomePage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        PresetPicker.SelectionChanged += (_, _) => PresetHint.Text = OptimizePresets.Describe(OptimizePresets.All[PresetPicker.SelectedIndex]) + " Nothing changes until you confirm.";
        DrivesHost.SizeChanged += (_, e) => { if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 1) ShowDrives(); };
        Loaded += (_, _) =>
        {
            if (Window is { } w && !ReferenceEquals(_watchedWindow, w))
            {
                _watchedWindow = w;
                w.StateChanged += (_, _) => UpdateNowSampling();
            }
            RefreshAsync().Forget(Host, "Couldn't refresh Home");
        };
        // The Now card only samples while Home is on screen.
        IsVisibleChanged += (_, _) => UpdateNowSampling();
        Unloaded += (_, _) => UpdateNowSampling();
    }

    private System.Windows.Window? _watchedWindow;

    // ---------------- the optional "Now" card (layout spec §4.3) ----------------

    private Now.NowCard? _now;
    private Now.NowSampler? _sampler;
    private readonly Now.NowHistory _nowHistory = new();

    /// <summary>The Now card when it is switched on (screenshots and tests).</summary>
    public Now.NowCard? NowCard => _now;

    /// <summary>Shows or removes the Now card as Settings › Appearance says (off by default).</summary>
    private void ShowNow()
    {
        bool on = ShellPrefs.Get(Settings).ShowNowCard;
        if (!on)
        {
            NowHost.Content = null;
            NowHost.Visibility = Visibility.Collapsed;
            _now = null;
            UpdateNowSampling();
            return;
        }
        if (_now is null)
        {
            var state = ShellState.Get(Settings);
            _now = new Now.NowCard();
            _now.SetCollapsed(state.NowCollapsed, raise: false);
            _now.SetChart(state.NowChart);
            _now.FreeUpSpaceRequested += () => { if ((_cleanupModuleId ?? CleanupModule()?.Id) is { } id) Host.NavigateTo(id); };
            _now.CollapsedChanged += folded =>
            {
                ShellState.Update(Settings, s => s with { NowCollapsed = folded });
                Window?.SaveSettingsQuietly();
                UpdateNowSampling();
            };
            _now.ChartChanged += chart =>
            {
                ShellState.Update(Settings, s => s with { NowChart = chart });
                if (_lastNow is { } last) _now?.Show(last, _nowHistory);
            };
            NowHost.Content = _now;
        }
        NowHost.Visibility = Visibility.Visible;
        _now.FreeButton.Visibility = CleanupModule() is null ? Visibility.Collapsed : Visibility.Visible;
        var system = _drives.FirstOrDefault(d => d.IsSystem) ?? _drives.FirstOrDefault();
        _now.SetDrive(system, system is null ? "" : DriveName(system));
        UpdateNowSampling();
    }

    private Now.NowSample? _lastNow;

    /// <summary>Starts or stops the 1-second sampling: only while the card is on, open, Home is shown and the window isn't minimised.</summary>
    private void UpdateNowSampling()
    {
        bool minimized = Window?.WindowState == WindowState.Minimized;
        bool run = Now.NowPolicy.ShouldSample(_now is not null, _now?.IsCollapsed ?? true, IsVisible && IsLoaded, minimized);
        if (!run)
        {
            _sampler?.Stop();
            return;
        }
        if (_sampler is null)
        {
            // Demo mode and screenshots show invented numbers, never this PC's.
            Now.INowSource source = _services.IsDemo || Window?.ScreenshotMode == true ? new Now.DemoNowSource() : new Now.WindowsNowSource();
            _sampler = new Now.NowSampler(source);
            _sampler.Sampled += s => Dispatcher.BeginInvoke(() =>
            {
                _nowHistory.Add(s);
                _lastNow = s;
                _now?.Show(s, _nowHistory);
            });
        }
        _sampler.Start();
    }

    /// <summary>Whether the Now card is sampling right now (tests and screenshots).</summary>
    public bool IsNowSampling => _sampler?.IsRunning == true;

    /// <summary>Screenshots: a minute of invented readings at once, so the line is full.</summary>
    internal void FillNowForScreenshot()
    {
        var demo = new Now.DemoNowSource();
        for (int i = 0; i < Now.NowHistory.Seconds; i++) _nowHistory.Add(_lastNow = demo.Read());
        if (_lastNow is { } s) _now?.Show(s, _nowHistory);
    }

    // ---------------- System info (Insights' read-only window) ----------------

    private Action<System.Windows.Window>? _systemInfo;
    private bool _systemInfoLooked;

    /// <summary>
    /// The System info opener: an <see cref="ISystemInfoOpener"/> from the services, else Insights'
    /// <c>SystemInfoWindow.Show(Window, IServiceProvider)</c> when this build has it (looked up by name, so Home builds
    /// before Insights adds it), else none and the button is hidden.
    /// </summary>
    internal static Action<System.Windows.Window>? FindSystemInfo(IServiceProvider services)
    {
        if (services.GetService(typeof(ISystemInfoOpener)) is ISystemInfoOpener opener) return owner => opener.Show(owner, services);
        try
        {
            var type = typeof(PcTidy.Features.Insights.InsightsModule).Assembly.GetTypes().FirstOrDefault(t => t.Name == "SystemInfoWindow");
            var show = type?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Show" && m.GetParameters() is { Length: 2 } p
                                     && p[0].ParameterType.IsAssignableFrom(typeof(System.Windows.Window))
                                     && p[1].ParameterType.IsAssignableFrom(typeof(IServiceProvider)));
            return show is null ? null : owner => show.Invoke(null, new object[] { owner, services });
        }
        catch (Exception ex) when (ex is System.Reflection.ReflectionTypeLoadException or TypeLoadException or FileNotFoundException) { return null; }
    }

    private void ShowSystemInfoButton()
    {
        if (!_systemInfoLooked)
        {
            _systemInfoLooked = true;
            _systemInfo = FindSystemInfo(_services);
        }
        SystemInfoButton.Visibility = _systemInfo is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True when Home offers "System info".</summary>
    public bool HasSystemInfo => _systemInfo is not null;

    private void OnSystemInfo(object sender, RoutedEventArgs e) => OpenSystemInfo();

    private void OpenSystemInfo()
    {
        if (_systemInfo is null || Window is not { } owner) return;
        if (_services.IsDemo)
        {
            Host.ShowMessage("Not in demo mode", "System info reads this PC's hardware, so the demo doesn't open it.");
            return;
        }
        try { _systemInfo(owner); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashHandler.Write(ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex, "Opening System info", terminating: false);
            Host.ShowMessage("Couldn't open System info", "Something went wrong while reading this PC. Nothing was changed.", isError: true);
        }
    }

    private IShellFlowHost Host => _services.Get<IShellFlowHost>();
    private AppSettings Settings => _services.Get<AppSettings>();
    private MainWindow? Window => System.Windows.Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow;

    /// <summary>Reloads every card (cheap: journal, quarantine total, drive sizes). Never scans by itself.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await LoadAsync(); }
        finally { _refreshing = false; }
    }

    private async Task LoadAsync()
    {
        var hour = DateTime.Now.Hour;
        Greeting.Text = hour < 5 ? "Good evening" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        var system = _services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        var windows = system?.IsWindows11 ?? Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";

        IReadOnlyList<RunRecord> runs = Array.Empty<RunRecord>();
        long quarantineBytes = 0;
        string? problem = null;
        try
        {
            // Journal and quarantine read files: off the UI thread.
            var journal = _services.Get<IRunJournal>();
            var store = _services.Get<IQuarantineStore>();
            (runs, _unfinished, quarantineBytes) = await Task.Run(async () => (
                await journal.ListAsync(CancellationToken.None),
                await journal.FindUnfinishedAsync(CancellationToken.None),
                await store.TotalBytesAsync(CancellationToken.None)));
        }
        catch (Exception ex) { problem = ex.Message; }

        var real = runs.Where(r => r.Mode == ExecutionMode.Real).ToList();
        _lastRun = real.FirstOrDefault(r => r.Status != RunStatus.Interrupted && _unfinished.All(u => u.RunId != r.RunId));
        _lastRunGroup = _lastRun?.ParentRunId is { } parent ? real.Where(r => r.ParentRunId == parent).ToList() : _lastRun is null ? Array.Empty<RunRecord>() : new[] { _lastRun };
        SubtitleText.Text = problem is not null ? "Couldn't read your history just now: " + problem
            : (_lastRun is null ? "Welcome. Start with a look around: nothing changes until you confirm" : $"Last run {RunSummary.WhenInSentence(_lastRun.CreatedUtc)}") + $" · {windows}";

        _drives = Drives();
        bool sandboxOrDemo = _services.IsDemo || (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.IsSandbox != false;
        _protectionOff = await Task.Run(() => SystemProtectionNotice.ShouldShow(sandboxOrDemo));
        _pendingScan = await PendingScanAsync();
        ShowBanners();
        ShowLastRun();
        ShowQuarantine(quarantineBytes);
        ShowDrives();
        ShowSystemInfoButton();
        ShowHealth();
        ShowNow();
        ShowQuickActions();
        ShowTrust(real);
        ShowFreeCard();
    }

    // ---------------- banners: settings problem, unfinished run ----------------

    private void ShowBanners()
    {
        Banners.Children.Clear();
        var s = Settings;
        if (s.LoadProblem is { } why && !s.LoadProblemAcknowledged)
        {
            // Calm, specific, and the user's file is never overwritten until they say OK (AppSettings.Save refuses).
            var bar = new InfoBar
            {
                Title = "Your settings file couldn't be read.",
                Message = ShellPrefs.FriendlyLoadProblem(why) + " PC Tidy won't save over your file until you say OK. Please check Settings (protected folders, quarantine days) first.",
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 12),
            };
            var ok = new Wpf.Ui.Controls.Button { Content = "OK, use these settings", Appearance = ControlAppearance.Primary };
            AutomationProperties.SetName(ok, "OK, use these settings and save them from now on");
            ok.Click += (_, _) => AcknowledgeSettingsProblem();
            var open = new Wpf.Ui.Controls.Button { Content = "Open Settings" };
            AutomationProperties.SetName(open, "Open Settings to check them first");
            open.Click += (_, _) => Host.NavigateTo("settings");
            Banners.Children.Add(InfoBars.WithActions(bar, ok, open));
        }
        if (UpdateCheckNoticeDue())
        {
            // Once, for people who used PC Tidy before the daily update check existed (3.1.1 review L8).
            var bar = new InfoBar
            {
                Message = HelpContent.UpdateCheckNotice,
                IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 12),
            };
            var open = new Wpf.Ui.Controls.Button { Content = "Open Settings" };
            AutomationProperties.SetName(open, "Open Settings to turn the update check on or off");
            open.Click += (_, _) => { Host.NavigateTo("settings"); Window?.OpenSettingsSection("updates"); };
            var wrapper = InfoBars.WithActions(bar, open);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar)).AddValueChanged(bar, (_, _) =>
            {
                if (bar.IsOpen) return;
                _updateNotice = false;
                wrapper.Visibility = Visibility.Collapsed;
            });
            Banners.Children.Add(wrapper);
        }
        if (_pendingScan is { } cp)
        {
            // A scan cut short (a Windows Update restart, a crash, closing PC Tidy): carry on, or start again.
            var card = ResumeNotes.ResumeOffer(cp, () => ResumeScan(cp), () => StartScanOver(cp));
            Banners.Children.Add(card);
        }
        if (_unfinished.Count > 0)
        {
            var bar = new InfoBar
            {
                Title = "A run didn't finish.",
                Message = $"\"{_unfinished[0].Title}\" stopped part way (maybe the PC turned off). You can finish it or roll it back.",
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 12),
            };
            var review = new Wpf.Ui.Controls.Button { Content = "Review in History" };
            AutomationProperties.SetName(review, "Review the unfinished run in History");
            review.Click += OnHistory;
            Banners.Children.Add(InfoBars.WithActions(bar, review));
        }
        if (_protectionOff)
        {
            // Calm and closable: undo works without it; only the extra Windows safety net is missing.
            var bar = new InfoBar
            {
                Title = SystemProtectionNotice.Title, Message = SystemProtectionNotice.Message,
                IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 12),
            };
            var open = new Wpf.Ui.Controls.Button { Content = SystemProtectionNotice.ButtonText };
            AutomationProperties.SetName(open, "Open Windows Settings to turn on System protection");
            open.Click += (_, _) =>
            {
                try { PcTidy.App.Updates.OwnProcesses.OpenSystemProtectionSettings(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Host.ShowMessage("Couldn't open Settings", ex.Message, isError: true); }
            };
            var wrapper = InfoBars.WithActions(bar, open);
            // The bar's own X closes it: remembered for this session, and the button below it goes too.
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar)).AddValueChanged(bar, (_, _) =>
            {
                if (bar.IsOpen) return;
                SystemProtectionNotice.Dismiss();
                _protectionOff = false;
                wrapper.Visibility = Visibility.Collapsed;
            });
            Banners.Children.Add(wrapper);
        }
        Banners.Visibility = Banners.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- the one-time update-check notice ----------------

    private bool? _updateNotice;

    /// <summary>True when Home shows "PC Tidy now checks GitHub once a day" (tests and screenshots).</summary>
    public bool ShowsUpdateCheckNotice => _updateNotice == true;

    /// <summary>
    /// Decided once a session: shown to people who finished the welcome screen before 3.1.1 (it didn't mention the
    /// check), on a real PC, with the check on. Marked seen as soon as it is shown, so it never comes back.
    /// </summary>
    private bool UpdateCheckNoticeDue()
    {
        if (_updateNotice is { } decided) return decided;
        var s = Settings;
        bool practice = _services.IsDemo || _services.Args.SandboxRoot is not null || (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.IsSandbox == true;
        bool show = PcTidy.App.Updates.UpdatePolicy.ShowUpdateCheckNotice(s.OnboardingDone, ShellState.Get(s).UpdateCheckNoticeSeen, ShellPrefs.Get(s).AutoUpdateCheck, practice);
        if (show)
        {
            ShellState.Update(s, st => st with { UpdateCheckNoticeSeen = true });
            Window?.SaveSettingsQuietly();
        }
        _updateNotice = show;
        return show;
    }

    // ---------------- a scan saved part way ----------------

    private ScanCheckpoint? _pendingScan;

    /// <summary>The scan Home offers to resume (tests and screenshots).</summary>
    public ScanCheckpoint? PendingScan => _pendingScan;

    /// <summary>The newest scan that stopped part way and may still be resumed, for a section this window has.</summary>
    private async Task<ScanCheckpoint?> PendingScanAsync()
    {
        if (ScanResume.From(_services) is not { } resume) return null;
        try
        {
            var all = await Task.Run(resume.PendingAll);
            return all.FirstOrDefault(cp => cp.NavigateTo == "optimize" || Window?.HasPage(cp.NavigateTo) != false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private void ResumeScan(ScanCheckpoint cp)
    {
        _pendingScan = null;
        ShowBanners();
        Host.NavigateTo(cp.NavigateTo);
        if (cp.NavigateTo == "optimize")
        {
            if (Window?.PageFor<CombinedFlowPage>("optimize") is { } page) page.ResumeAsync().Forget(Host, "Scan didn't finish");
        }
        else if (Window?.FlowPageFor(cp.NavigateTo) is { } flow) flow.ResumeAsync().Forget(Host, "Scan didn't finish");
    }

    private void StartScanOver(ScanCheckpoint cp)
    {
        ScanResume.From(_services)?.Discard(cp.Key.Flow);
        _pendingScan = null;
        ShowBanners();
        if (cp.NavigateTo == "optimize") StartOptimize(cp.Key.Preset is { } p && OptimizePresets.All.Contains(p) ? p : OptimizePresets.All[PresetPicker.SelectedIndex]);
        else
        {
            Host.NavigateTo(cp.NavigateTo);
            if (Window?.FlowPageFor(cp.NavigateTo) is { } flow) flow.StartOverAsync().Forget(Host, "Scan didn't finish");
        }
    }

    private void AcknowledgeSettingsProblem()
    {
        Settings.LoadProblemAcknowledged = true;
        try
        {
            _services.Get<ISettingsStore>().Save(Settings);
        }
        catch (Exception ex)
        {
            Host.ShowMessage("Settings not saved yet", "PC Tidy couldn't write the settings file: " + ex.Message + " Your choices still apply until you close PC Tidy.", isError: true);
        }
        ShowBanners();
    }

    // ---------------- drives ----------------

    private IReadOnlyList<DriveDetails> Drives()
    {
        if (_services.GetService(typeof(ISystemInfo)) is ISystemInfo si)
        {
            try { return si.Drives.Where(d => d.Media is not (DriveMedia.Removable or DriveMedia.Network)).ToList(); }
            catch { /* fall through */ }
        }
        // Fallback: read-only DriveInfo for the known fixed drives.
        var list = new List<DriveDetails>();
        var loc = _services.GetService(typeof(IKnownLocations)) as IKnownLocations;
        foreach (var root in loc?.FixedDrives ?? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name).ToList())
        {
            try
            {
                var d = new DriveInfo(root);
                if (!d.IsReady) continue;
                list.Add(new DriveDetails(d.Name, d.VolumeLabel, DriveMedia.Unknown, d.TotalSize, d.AvailableFreeSpace,
                    string.Equals(d.Name, loc?.SystemDrive, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* drive vanished or not ready: just skip it */ }
        }
        return list;
    }

    /// <summary>"C:" for a drive; a practice PC's folder name ("C (practice)") in sandbox mode, never a long path.</summary>
    private string DriveName(DriveDetails d)
    {
        var root = d.Root.TrimEnd('\\');
        if (root.Length <= 2) return root;
        var name = System.IO.Path.GetFileName(root);
        bool sandbox = (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.IsSandbox == true;
        return sandbox ? $"{name} (practice)" : name;
    }

    private void ShowDrives()
    {
        var drives = _drives;
        if (drives.Count == 0)
        {
            DrivesHost.Content = Text("Drive sizes aren't available on this PC right now.", "Secondary");
            return;
        }
        var ordered = drives.OrderByDescending(d => d.IsSystem).ThenBy(d => d.Root).ToList();
        double width = DrivesHost.ActualWidth > 0 ? DrivesHost.ActualWidth : 280;
        if (ordered.Count <= 2)
        {
            // Two rings side by side: each gets half the card minus a gap, and shrinks (never touches) on narrow cards.
            const double gap = 16;
            double cell = (width - gap) / 2;
            double ring = Math.Clamp(cell - 8, 60, 96);
            var grid = new UniformGrid { Columns = 2 };
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = ordered[i];
                grid.Children.Add(new DriveRing
                {
                    RingSize = ring, Root = DriveName(d), DriveLabel = d.Label, TotalBytes = d.TotalBytes, FreeBytes = d.FreeBytes, IsSecondary = i > 0,
                    Margin = new Thickness(i == 0 ? 0 : gap / 2, 0, i == 0 ? gap / 2 : 0, 0),
                });
            }
            if (ordered.Count == 1)
                grid.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap / 2, 0, 0, 0), Children = { Text("One drive on this PC.", "Secondary"), Text("Move to drive appears when you add another one.", "Caption") } });
            DrivesHost.Content = grid;
            return;
        }
        // 3+ drives: compact rows so the card keeps its size.
        var stack = new StackPanel();
        foreach (var (d, i) in ordered.Take(4).Select((d, i) => (d, i)))
        {
            // Orientation first: the other properties re-run the layout update.
            var row = new DriveRing { Orientation = Orientation.Horizontal, Root = DriveName(d), DriveLabel = d.Label, TotalBytes = d.TotalBytes, FreeBytes = d.FreeBytes, IsSecondary = i > 0, RingSize = 44, Margin = new Thickness(0, 0, 0, 6) };
            stack.Children.Add(row);
        }
        if (ordered.Count > 4) stack.Children.Add(Text($"and {ordered.Count - 4} more", "Caption"));
        DrivesHost.Content = stack;
    }

    // ---------------- health: nothing until the user checks, then the score and every reason ----------------

    private void ShowHealth()
    {
        bool show = ShellPrefs.Get(Settings).ShowHealthScore;
        HealthCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        HealthReasons.Children.Clear();
        if (_reclaimable is not { } junk)
        {
            HealthNotChecked.Visibility = Visibility.Visible;
            HealthChecked.Visibility = Visibility.Collapsed;
            HealthCheckButton.IsEnabled = !_checking && CleanupModule() is not null;
            AutomationProperties.SetName(HealthCard, "PC health: not checked yet");
            return;
        }

        var system = _services.GetService(typeof(ISystemInfo)) as ISystemInfo;
        var score = new HealthScore(_drives, junk, _unfinished.Count, system?.RebootPending ?? false, DriveName, _switchedOffServices);
        HealthNotChecked.Visibility = Visibility.Collapsed;
        HealthChecked.Visibility = Visibility.Visible;
        HealthRing.Value = score.Score / 100.0;
        HealthNumber.Text = score.Score.ToString(CultureInfo.CurrentCulture);
        HealthLabel.Text = score.Label;
        HealthCaption.Text = $"{score.Score} of 100 · checked at {_checkedAt.ToString("t", CultureInfo.CurrentCulture)}";
        AutomationProperties.SetName(HealthCard, $"PC health {score.Score} of 100, {score.Label}. " + string.Join(". ", score.Reasons.Select(r => r.Text)));
        foreach (var r in score.Reasons.Take(5))
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var icon = new SymbolIcon
            {
                Symbol = r.Tone switch { HealthTone.Good => SymbolRegular.CheckmarkCircle24, HealthTone.Attention => SymbolRegular.Info24, _ => SymbolRegular.Circle24 },
                Filled = r.Tone == HealthTone.Good, FontSize = 16, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(ForegroundProperty, r.Tone == HealthTone.Good ? "TidyAccentBrush" : "TextFillColorSecondaryBrush");
            dock.Children.Add(icon);
            if (r.Points != 0)
            {
                var pts = Text(r.Points.ToString("+0;−0", CultureInfo.CurrentCulture), "Caption");
                DockPanel.SetDock(pts, Dock.Right);
                pts.Margin = new Thickness(8, 1, 0, 0);
                dock.Children.Add(pts);
            }
            if (r.OpenSection is { } section && Host is { } host)
            {
                var open = SectionLink("Open", section, host);
                DockPanel.SetDock(open, Dock.Right);
                dock.Children.Add(open);
            }
            dock.Children.Add(Text(r.Text, "Secondary"));
            HealthReasons.Children.Add(dock);
        }
        // Say what the score covers, so "100" isn't read as "nothing anywhere": other sections have their own checks.
        // 3.2: one short line; what the score covers is in its tooltip (and read out by screen readers).
        var covers = Text(score.AnyPointsOff ? "Points each reason takes off are on the right." : "Nothing in these checks takes points off.", "Caption");
        covers.ToolTip = (score.AnyPointsOff ? "The number on the right is what each reason takes off. " : "Nothing in these checks is taking points off. ") + score.Covers;
        AutomationProperties.SetHelpText(covers, (string)covers.ToolTip);
        covers.TextTrimming = TextTrimming.CharacterEllipsis;
        HealthReasons.Children.Add(covers);
        if (Window?.HasPage("insights") == true)
        {
            var insights = SectionLink("Open Insights", "insights", Host);
            insights.Margin = new Thickness(0, 6, 0, 0); // lines up with the text above (no negative margin)
            insights.HorizontalAlignment = HorizontalAlignment.Left;
            HealthReasons.Children.Add(insights);
        }
    }

    private static Wpf.Ui.Controls.Button SectionLink(string text, string section, IShellFlowHost host)
    {
        var b = new Wpf.Ui.Controls.Button { Content = text, Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 2, 8, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(b, text == "Open" ? $"Open {section}" : text);
        b.Click += (_, _) => host.NavigateTo(section);
        return b;
    }

    /// <summary>
    /// Services' "switched off by something" finding (read-only inventory), so Home's score never says "nothing"
    /// while Services shows its amber banner. Null when Services isn't in this build or couldn't be read.
    /// </summary>
    private async Task<int?> CountSwitchedOffServicesAsync(ScanContext ctx)
    {
        var modules = Window?.Modules ?? _services.VisibleModules;
        if (modules.OfType<PcTidy.Features.Services.ServicesModule>().FirstOrDefault() is not { } services) return null;
        try
        {
            var inventory = await services.Scanner.InventoryAsync(ctx, null, CancellationToken.None);
            return PcTidy.Features.Services.ServiceRepairScanner.Find(inventory.Services).Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // the score then says it didn't count services
        }
    }

    // ---------------- clean-up estimate (only when the user asks: no automatic scans) ----------------

    private IFeatureModule? CleanupModule()
    {
        var modules = Window?.Modules ?? _services.VisibleModules;
        return modules.FirstOrDefault(m => m.Group == NavGroup.CleanUp && m.Scanners.Any(s => s.IncludedInOptimizeAll))
               ?? modules.FirstOrDefault(m => m.Group == NavGroup.CleanUp && m.Scanners.Count > 0);
    }

    private void ShowFreeCard()
    {
        var module = CleanupModule();
        _cleanupModuleId = module?.Id;
        if (module is null)
        {
            FreeNumberPanel.Visibility = Visibility.Collapsed;
            FreeNotChecked.Text = "Not available";
            FreeNotChecked.Visibility = Visibility.Visible;
            FreeHint.Text = "Clean-up isn't part of this build yet.";
            FreeButton.Visibility = Visibility.Collapsed;
            FreeCaption.Text = "";
            return;
        }
        if (_reclaimable is null && !_checking)
        {
            // Before a check there is no number at all (no "?" either): just say so, and offer the check.
            FreeNumberPanel.Visibility = Visibility.Collapsed;
            FreeNotChecked.Visibility = Visibility.Visible;
            FreeButton.Content = "Check now";
            FreeCaption.Text = "Quick look · changes nothing";
        }
    }

    private void OnFreeButton(object sender, RoutedEventArgs e)
    {
        if (_reclaimable is not null) { if (_cleanupModuleId != null) Host.NavigateTo(_cleanupModuleId); return; }
        CheckFreeSpaceAsync().Forget(Host, "The quick look didn't finish");
    }

    private void OnHealthCheck(object sender, RoutedEventArgs e) => CheckFreeSpaceAsync().Forget(Host, "The quick look didn't finish");

    /// <summary>Quick, read-only look with the clean-up scanners; fills the estimate and the health score (public for screenshots).</summary>
    public async Task CheckFreeSpaceAsync()
    {
        var module = CleanupModule();
        if (module is null || _checking) return;
        _checking = true;
        FreeButton.IsEnabled = false;
        HealthCheckButton.IsEnabled = false;
        FreeRing.Visibility = Visibility.Visible;
        FreeCaption.Text = "Looking…";
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var outcomes = await ScanRunner.RunAsync(module.Scanners, ctx, (_, _, p) => FreeCaption.Text = p.Message, CancellationToken.None);
            var groups = outcomes.Where(o => o.Result != null).SelectMany(o => o.Result!.Groups).ToList();
            var picks = groups.Select(g => (g.Title, Bytes: g.Items.Where(OptimizePresets.IsTickableByDefault).Sum(i => i.SizeBytes)))
                              .Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToList();
            _reclaimable = picks.Sum(p => p.Bytes);
            _checkedAt = DateTime.Now;
            ShowBreakdown(picks);
            FreeButton.Content = "Review";
            FreeButton.Icon = new SymbolIcon(SymbolRegular.Eye24);
            AutomationProperties.SetName(FreeButton, "Review what can be cleaned up");
            FreeCaption.Text = "Preview first";
            _switchedOffServices = await CountSwitchedOffServicesAsync(ctx);
            await RefreshHealthOnlyAsync();
        }
        catch (Exception ex)
        {
            FreeCaption.Text = "Couldn't finish the quick look: " + ex.Message;
        }
        finally
        {
            _checking = false;
            FreeButton.IsEnabled = true;
            HealthCheckButton.IsEnabled = true;
            FreeRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RefreshHealthOnlyAsync()
    {
        try
        {
            var journal = _services.Get<IRunJournal>();
            _unfinished = await Task.Run(() => journal.FindUnfinishedAsync(CancellationToken.None));
        }
        catch { /* keep the last known list */ }
        _drives = Drives();
        ShowHealth();
    }

    private void ShowBreakdown(IReadOnlyList<(string Title, long Bytes)> picks)
    {
        var size = Sizes.Format(_reclaimable ?? 0).Split(' ');
        FreeNotChecked.Visibility = Visibility.Collapsed;
        FreeNumberPanel.Visibility = Visibility.Visible;
        FreeNumber.Text = size[0];
        FreeUnit.Text = size.Length > 1 ? size[1] : "";
        FreeHint.Visibility = Visibility.Collapsed;
        BreakdownBar.Children.Clear();
        BreakdownBar.ColumnDefinitions.Clear();
        BreakdownLegend.Children.Clear();
        if (picks.Count == 0)
        {
            FreeHint.Text = "Nothing worth cleaning right now.";
            FreeHint.Visibility = Visibility.Visible;
            BreakdownBar.Visibility = BreakdownLegend.Visibility = Visibility.Collapsed;
            return;
        }
        var parts = picks.Take(3).ToList();
        long other = picks.Skip(3).Sum(p => p.Bytes);
        if (other > 0) parts.Add(("Other", other));
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) BreakdownBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            BreakdownBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(parts[i].Bytes, (_reclaimable ?? 1) / 40), GridUnitType.Star) });
            var brush = $"TidyBreakdown{i + 1}Brush";
            var seg = new Border { CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == parts.Count - 1 ? 4 : 0, i == parts.Count - 1 ? 4 : 0, i == 0 ? 4 : 0) };
            seg.SetResourceReference(Border.BackgroundProperty, brush);
            Grid.SetColumn(seg, BreakdownBar.ColumnDefinitions.Count - 1);
            BreakdownBar.Children.Add(seg);

            // One legend line per part: the name wraps if it must, the size never does.
            var legend = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0) };
            dot.SetResourceReference(Shape.FillProperty, brush);
            legend.Children.Add(dot);
            var sizeText = Text(Sizes.Format(parts[i].Bytes), "Secondary");
            sizeText.TextWrapping = TextWrapping.NoWrap;
            sizeText.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(sizeText, Dock.Right);
            legend.Children.Add(sizeText);
            legend.Children.Add(Text(parts[i].Title, "Secondary"));
            AutomationProperties.SetName(legend, $"{parts[i].Title}: {Sizes.Format(parts[i].Bytes)}");
            BreakdownLegend.Children.Add(legend);
        }
        BreakdownBar.Visibility = Visibility.Visible;
        BreakdownLegend.Visibility = Visibility.Visible;
    }

    // ---------------- last run, quarantine, quick actions, trust ----------------

    private void ShowLastRun()
    {
        if (_lastRun is null)
        {
            LastRunWhen.Text = "LAST RUN";
            LastRunTitle.Text = "No runs yet";
            LastRunDetail.Text = "Your first run shows up here, with a one-click Undo.";
            UndoLastButton.Visibility = Visibility.Collapsed;
            return;
        }
        var s = new RunSummary(_lastRunGroup);
        LastRunWhen.Text = "LAST RUN · " + RunSummary.When(_lastRun.CreatedUtc).ToUpper(CultureInfo.CurrentCulture);
        string title = _lastRunGroup.Count > 1 ? "Optimize everything" : _lastRun.Title;
        LastRunTitle.Text = s.IsUndone ? $"{title} was undone" : $"{title}: {s.FullHeadline}";
        var bits = new List<string>();
        if (s.Quarantined > 0) bits.Add($"{s.Quarantined:N0} item{PlanFacts.S(s.Quarantined)} in quarantine");
        if (s.Moved > 0) bits.Add($"{s.Moved:N0} file{PlanFacts.S(s.Moved)} moved");
        if (s.StartupChanged > 0) bits.Add($"{s.StartupChanged} startup item{PlanFacts.S(s.StartupChanged)}");
        if (s.SettingsChanged > 0) bits.Add($"{s.SettingsChanged} setting{PlanFacts.S(s.SettingsChanged)} changed");
        if (s.Skipped > 0) bits.Add($"{s.Skipped} skipped");
        LastRunDetail.Text = bits.Count > 0 ? string.Join(" · ", bits) : RunSummary.StatusText(_lastRun.Status);
        UndoLastButton.Visibility = s.AnyReversible && !s.IsUndone ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowQuarantine(long bytes)
    {
        int days = Settings.QuarantineDays;
        QuarantineTitle.Text = bytes > 0 ? $"{Sizes.Format(bytes)} restorable" : "Empty";
        QuarantineDetail.Text = Settings.AskBeforePurge
            ? $"Kept {days} days, then PC Tidy asks before deleting anything"
            : $"Emptied after {days} days";
    }

    private void ShowQuickActions()
    {
        while (QuickActions.Children.Count > 1) QuickActions.Children.RemoveAt(1);
        var modules = Window?.Modules ?? _services.VisibleModules;
        void Add(SymbolRegular icon, string text, RoutedEventHandler click)
        {
            var b = new Wpf.Ui.Controls.Button { Content = text, Icon = new SymbolIcon(icon), Appearance = ControlAppearance.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(b, text);
            b.Click += click;
            QuickActions.Children.Add(b);
        }
        if (CleanupModule() is { } clean) Add(SymbolRegular.ScanDash24, "Quick scan  ·  about 20 s", (_, _) => StartModuleScan(clean.Id));
        if (modules.FirstOrDefault(m => m.Id.Contains("organize", StringComparison.OrdinalIgnoreCase)) is { } org)
            Add(SymbolRegular.FolderArrowRight24, "Organize Downloads  ·  preview first", (_, _) => StartModuleScan(org.Id));
        if (modules.FirstOrDefault(m => m.Id.Contains("speed", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("startup", StringComparison.OrdinalIgnoreCase)) is { } speed)
            Add(SymbolRegular.Rocket24, "Check startup apps", (_, _) => StartModuleScan(speed.Id));
        // With the health card hidden, "System info" (normally next to it) moves here.
        if (HealthCard.Visibility != Visibility.Visible && _systemInfo is not null) Add(SymbolRegular.Info24, "System info  ·  read-only", (_, _) => OpenSystemInfo());
        if (QuickActions.Children.Count == 1) Add(SymbolRegular.History24, "Open History", OnHistory);
    }

    private void StartModuleScan(string id)
    {
        Host.NavigateTo(id);
        if (Window?.PageFor<StandardFlowPage>(id) is { State: FlowState.Ready } page) page.StartScanAsync().Forget(Host, "Scan didn't finish");
    }

    private void ShowTrust(IReadOnlyList<RunRecord> real)
    {
        var kept = real.Where(r => r.Status is not (RunStatus.Undone or RunStatus.RolledBack)).ToList();
        if (kept.Count == 0)
        {
            TrustLead.Text = "Your safety net:";
            TrustText.Text = " nothing is deleted without showing you first, and every run can be undone. Everything stays on this PC.";
            return;
        }
        var s = new RunSummary(kept);
        var since = real.Min(r => r.CreatedUtc).ToLocalTime().ToString("d MMMM", CultureInfo.CurrentCulture);
        var bits = new List<string>();
        if (s.BytesQuarantined + s.BytesFreed > 0) bits.Add($"{Sizes.Format(s.BytesQuarantined + s.BytesFreed)} cleaned up");
        if (s.Moved > 0) bits.Add($"{s.Moved:N0} files sorted");
        bits.Add($"{real.Count} run{PlanFacts.S(real.Count)}");
        bits.Add("every one undoable");
        TrustLead.Text = $"Since {since}:";
        TrustText.Text = " " + string.Join(" · ", bits);
    }

    // ---------------- actions ----------------

    private void OnOptimize(object sender, RoutedEventArgs e) => StartOptimize(OptimizePresets.All[PresetPicker.SelectedIndex]);

    public void StartOptimize(string preset)
    {
        Host.NavigateTo("optimize");
        if (Window?.PageFor<CombinedFlowPage>("optimize") is { } page)
        {
            page.Preset = preset;
            if (page.State is FlowState.Ready or FlowState.Done or FlowState.Preview) page.StartScanAsync().Forget(Host, "Scan didn't finish");
        }
    }

    private void OnUndoLast(object sender, RoutedEventArgs e) => UndoLastAsync().Forget(Host, "Undo didn't finish");

    private async Task UndoLastAsync()
    {
        if (_lastRunGroup.Count == 0) return;
        await Host.UndoRunsAsync(_lastRunGroup, CancellationToken.None);
        await RefreshAsync();
    }

    private void OnHistory(object sender, RoutedEventArgs e) => Host.NavigateTo("history");
    private void OnQuarantine(object sender, RoutedEventArgs e) => Host.NavigateTo("quarantine");
    private void OnSafety(object sender, RoutedEventArgs e) => Window?.OpenSettingsSection("help");

    private static TextBlock Text(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }
}
