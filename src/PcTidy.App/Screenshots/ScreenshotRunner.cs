using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcTidy.App.Composition;
using PcTidy.App.Composition.Demo;
using PcTidy.App.Pages;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.UI.Flow;
using PcTidy.UI.Theming;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcTidy.App.Screenshots;

/// <summary>
/// --screenshots &lt;dir&gt;: drives the app through every page and state and saves PNGs, then exits.
/// Demo data by default; with --sandbox &lt;folder&gt; it captures a practice PC instead, and then only scans and
/// simulates (every confirm dialog is closed with Back: screenshot mode never runs anything for real there).
/// The whole pass uses the theme given on the command line (--light / --dark, default dark); the first Home shot
/// is repeated in the other theme for comparison. The PNGs are the only files this mode writes itself.
/// </summary>
public sealed class ScreenshotRunner
{
    private readonly MainWindow _w;
    private readonly AppServices _services;
    private readonly string _dir;
    private readonly ApplicationTheme _theme;

    private readonly DesktopIntegration? _desktop;

    public ScreenshotRunner(MainWindow window, AppServices services, string dir, DesktopIntegration? desktop = null)
    {
        _w = window;
        _services = services;
        _dir = dir;
        _desktop = desktop;
        _theme = string.Equals(services.Args.ForcedTheme, "Light", StringComparison.OrdinalIgnoreCase) ? ApplicationTheme.Light : ApplicationTheme.Dark;
    }

    /// <summary>A practice PC from --sandbox (demo data also uses sandbox locations, so check both).</summary>
    private bool IsSandbox => !_services.IsDemo && _services.Args.SandboxRoot != null
                              && (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.IsSandbox == true;
    private ApplicationTheme Other => _theme == ApplicationTheme.Light ? ApplicationTheme.Dark : ApplicationTheme.Light;
    private static string Name(ApplicationTheme t) => t == ApplicationTheme.Light ? "light" : "dark";

    public async Task RunAsync()
    {
        try
        {
            if (!_services.IsDemo && !IsSandbox) throw new InvalidOperationException("Screenshots run on demo data or a practice PC (--sandbox) only.");
            Directory.CreateDirectory(_dir);
            TidyTheme.Apply(_theme);
            if (_services.IsDemo) DemoScanner.DurationMs = 900;
            await Settle(900);

            if (_services.Args.Raw.Any(a => a.Equals("--shell-shots", StringComparison.OrdinalIgnoreCase))) await ShellSetAsync();
            else if (_services.Args.Raw.Any(a => a.Equals("--v31-shots", StringComparison.OrdinalIgnoreCase))) await V31SetAsync();
            else if (_services.Args.Raw.Any(a => a.Equals("--demo-minimal", StringComparison.OrdinalIgnoreCase))) await MinimalSetAsync();
            else if (IsSandbox) await SandboxSetAsync();
            else await DemoSetAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    // ---------------- demo: every page and state, including real (fake) runs ----------------

    private async Task DemoSetAsync()
    {
        // Home before and after "Check now" (no number before a check), then the other theme once.
        var home = _w.PageFor<HomePage>("home")!;
        _w.NavigateTo("home");
        await Settle(500);
        await Shot("00-home-not-checked.png");
        await home.CheckFreeSpaceAsync();
        await Shot($"01-home-{Name(_theme)}.png");
        TidyTheme.Apply(Other);
        await Shot($"02-home-{Name(Other)}.png");
        TidyTheme.Apply(_theme);

        // Clean: ready → scanning → preview → details → confirm → simulation → progress → done
        var clean = _w.PageFor<StandardFlowPage>("cleanup")!;
        _w.NavigateTo("cleanup");
        await Shot("03-clean-ready.png");
        var scan = clean.StartScanAsync();
        await Task.Delay(450);
        Save("04-clean-scanning.png");
        await scan;
        await Shot("05-clean-preview.png");
        clean.ResultsList.SelectFirstItem();
        await Shot("06-clean-details.png");
        clean.ResultsList.ClearSelection();

        var run = clean.RunAsync();
        await Settle(700);
        Save("07-clean-confirm.png");
        ConfirmDialog.Current?.Hide(ContentDialogResult.None);
        await run;

        await clean.SimulateAsync();
        await Shot("08-clean-simulation.png");
        clean.BackToPreview();
        await Settle(300);

        run = clean.RunAsync();
        await Settle(600);
        ConfirmDialog.Current?.Hide(ContentDialogResult.Primary); // demo only: nothing real exists to change
        await Task.Delay(350);
        Save("09-run-progress.png");
        await run;
        await Shot("10-clean-done.png");

        // Optimize & Organize everything
        var optimize = _w.PageFor<CombinedFlowPage>("optimize")!;
        _w.NavigateTo("optimize");
        await Shot("11-optimize-ready.png");
        await optimize.StartScanAsync();
        await Shot("12-optimize-preview.png");
        var confirm = optimize.ConfirmAsync();
        await Settle(700);
        Save("13-optimize-confirm.png");
        ConfirmDialog.Current?.Hide(ContentDialogResult.Primary);
        await confirm;
        await Shot("14-optimize-done.png");

        // History (with an interrupted run), Quarantine (one run opened), Settings
        if (DemoComposition.Journal is { } j && DemoComposition.Quarantine is { } q) DemoData.AddInterruptedRun(j, q);
        var history = _w.PageFor<HistoryPage>("history")!;
        _w.NavigateTo("history");
        await history.RefreshAsync();
        await Shot("15-history.png");
        var quarantine = _w.PageFor<QuarantinePage>("quarantine")!;
        _w.NavigateTo("quarantine");
        await quarantine.RefreshAsync();
        quarantine.OpenFirstRun();
        await Shot("16-quarantine.png");
        _w.NavigateTo("settings");
        await Shot("17-settings.png");
        _w.OpenSettingsSection("about");
        await Shot("18-settings-about.png");

        await OnboardingAsync(19);

        // Tweaks (the v3.1 page on the demo's invented registry) and a section without file sizes.
        if (_w.HasPage("tweaks") && await ScannedTweaksAsync() is { } tweaks)
        {
            tweaks.ChooseView(0);
            await Shot("22-tweaks-preview.png");
        }
        var organize = _w.PageFor<StandardFlowPage>("organize")!;
        _w.NavigateTo("organize");
        await organize.StartScanAsync();
        await Shot("23-organize-preview.png");

        await V31SetAsync();
    }

    // ---------------- v3.1 shell screens (also alone with --v31-shots) ----------------

    /// <summary>
    /// The v3.1 shell: rail with counters and "More" open, the Now card (invented numbers), the confirm dialog's list and
    /// footer, per-section progress, the one-task-at-a-time reason, History's restore points, the spotlight tour.
    /// Demo data only: nothing real is read or changed.
    /// </summary>
    private async Task V31SetAsync()
    {
        if (!_services.IsDemo) return;
        var settings = _services.Get<AppSettings>();

        // Rail: History's counter (runs since an invented "last opened") and the demo sections' counters; More open.
        ShellState.Update(settings, s => s with { HistoryViewedUtc = DateTime.UtcNow.AddDays(-2) });
        await _w.RefreshBadgesAsync();
        _w.NavigateTo("home");
        _w.SetMoreOpen(true);
        await Shot("40-rail-more-open.png");
        _w.SetMoreOpen(false);
        await Shot("41-rail-more-closed.png");
        // A section inside More opens it by itself (not remembered).
        if (_w.HasPage("photos"))
        {
            _w.NavigateTo("photos");
            await Shot("41b-rail-more-follows-page.png");
            _w.NavigateTo("home");
            _w.SetMoreOpen(false);
        }

        // Home with the Now card switched on (it is off by default).
        var home = _w.PageFor<HomePage>("home")!;
        ShellPrefs.Set(settings, ShellPrefs.Get(settings) with { ShowNowCard = true });
        await home.RefreshAsync();
        home.FillNowForScreenshot();
        await Shot("42-home-now-card.png");
        home.NowCard?.SetCollapsed(true, raise: true);
        await Shot("43-home-now-folded.png");
        home.NowCard?.SetCollapsed(false, raise: true);
        ShellPrefs.Set(settings, ShellPrefs.Get(settings) with { ShowNowCard = false });
        await home.RefreshAsync();

        // Confirm dialog (scrollable list, Back before Confirm), then a several-section run's progress with states.
        var optimize = _w.PageFor<CombinedFlowPage>("optimize")!;
        _w.NavigateTo("optimize");
        await optimize.StartScanAsync();
        var confirm = optimize.ConfirmAsync();
        await Settle(700);
        Save("44-confirm-list-and-footer.png");
        ConfirmDialog.Current?.Hide(ContentDialogResult.Primary); // demo only
        await Task.Delay(500);
        Save("45-progress-sections.png");
        await confirm;
        await Shot("46-optimize-done.png");

        // One long task at a time: the sticky bar's reason while another task holds the gate (demo: nothing runs).
        var clean = _w.PageFor<StandardFlowPage>("cleanup")!;
        _w.NavigateTo("cleanup");
        await clean.StartScanAsync();
        using (RunGate.Shared.Begin("Clean")) // "Available when Clean finishes"
            await Shot("47-busy-reason.png");
        await Settle(200);

        // History › Restore points (invented points).
        var history = _w.PageFor<HistoryPage>("history")!;
        _w.NavigateTo("history");
        await history.RefreshAsync();
        await history.ShowRestorePointsAsync();
        if (history.RestorePointRows.Skip(1).FirstOrDefault() is { } tagged)
        {
            ShellState.Update(settings, s => s with { RestorePointTags = RestorePointsModel.WithTag(s.RestorePointTags, tagged.TagKey, "Blue") });
            await history.ShowRestorePointsAsync();
        }
        await Shot("48-history-restore-points.png");

        // The spotlight tour: first step, the groups, History.
        _w.StartTour();
        await Shot("49-tour-1-scan.png");
        _w.Tour?.Model.GoTo(1);
        await Shot("50-tour-2-groups.png");
        _w.Tour?.Model.GoTo(2);
        await Shot("50b-tour-3-more.png");
        _w.Tour?.Model.GoTo(3);
        await Shot("51-tour-4-history.png");
        if (_w.Tour is { } tour) tour.Model.GoTo(tour.Model.Count - 1);
        await Shot("52-tour-last.png");
        _w.Tour?.Model.Close();
        await Settle(200);

        await V31PagesAsync();
    }

    // ---------------- v3.1 pages (demo, invented data only) ----------------

    /// <summary>
    /// The v3.1 pages: Tweaks (list, cards, the "Doesn't help" shelf, a preset's popover), Measure with an invented
    /// comparison, Games › Optimize (master/detail), PC care's Lag check (off) and Fixes, and System info (an invented PC).
    /// </summary>
    private async Task V31PagesAsync()
    {
        if (await ScannedTweaksAsync() is { } tweaks)
        {
            tweaks.SelectChipCommandless(PcTidy.Features.Tweaks.UI.TweakChip.All);
            tweaks.ChooseView(0);
            await Shot("53-tweaks-list.png");
            tweaks.ChooseView(1);
            await Shot("54-tweaks-cards.png");
            tweaks.ChooseView(0);
            tweaks.SelectChipCommandless(PcTidy.Features.Tweaks.UI.TweakChip.DoesntHelp);
            await Shot("55-tweaks-shelf.png");
            tweaks.SelectChipCommandless(PcTidy.Features.Tweaks.UI.TweakChip.All);
            if (tweaks.Presets.FirstOrDefault() is { } preset)
            {
                tweaks.PreviewPresetCommand.Execute(preset);
                await Shot("56-tweaks-preset-popover.png");
                tweaks.CancelPresetCommand.Execute(null);
                await Settle(200);
            }
        }

        if (_w.HasPage("measure"))
        {
            _w.NavigateTo("measure");
            await Settle(300);
            if (_w.PageObject("measure") is PcTidy.Features.Measure.UI.MeasurePage measure)
            {
                DemoV31.FillComparison(measure.ViewModel);
                await Settle(200);
                // Step 2 (the imported runs, each with its timing): the first run's card at the top.
                if (measure.ViewModel.BeforeRuns.FirstOrDefault() is { } firstRun
                    && Descendants<FrameworkElement>(measure).FirstOrDefault(e => ReferenceEquals(e.DataContext, firstRun)) is { } runCard)
                    runCard.BringIntoView(new Rect(0, -180, 10, 520));
                await Shot("57-measure-runs.png");
                (measure.FindName("VerdictChip") as FrameworkElement)?.BringIntoView(new Rect(0, -40, 10, 520));
                await Shot("58-measure-compare.png");
            }
        }

        if (_w.HasPage("games"))
        {
            _w.NavigateTo("games");
            await Settle(300);
            if (_w.PageObject("games") is FrameworkElement games && games.FindName("Tabs") is PcTidy.UI.Controls.Segmented gameTabs)
            {
                gameTabs.SelectedIndex = 2; // Optimize
                await Settle(400);
                if (Descendants<PcTidy.Features.Games.UI.GameOptimizeView>(games).FirstOrDefault() is { } optimize)
                {
                    for (int i = 0; i < 40 && optimize.FindName("ScanningState") is UIElement { Visibility: Visibility.Visible }; i++) await Task.Delay(250);
                    if (optimize.FindName("GameListBox") is System.Windows.Controls.ListBox list && list.Items.Count > 0) list.SelectedIndex = 0;
                }
                await Shot("59-games-optimize.png");
            }
        }

        var care = PcTidy.Features.PcCare.Planning.PcCareActions.FeatureId;
        if (_w.HasPage(care))
        {
            _w.NavigateTo(care);
            await Settle(300);
            if (_w.PageObject(care) is FrameworkElement carePage
                && carePage.FindName("TabGroups") is PcTidy.UI.Controls.Segmented groups && carePage.FindName("Tabs") is PcTidy.UI.Controls.Segmented tabs)
            {
                var all = PcTidy.Features.PcCare.Scanning.PcCareTabs.Groups;
                foreach (var (tab, shot) in new[] { (PcTidy.Features.PcCare.Scanning.PcCareTabs.LagCheck, "60-pccare-lag-check-off.png"), (PcTidy.Features.PcCare.Scanning.PcCareTabs.Fixes, "61-pccare-fixes.png") })
                {
                    int g = all.ToList().FindIndex(x => x.TabIds.Contains(tab));
                    if (g < 0) continue;
                    groups.SelectedIndex = g;
                    await Settle(150);
                    tabs.SelectedIndex = all[g].TabIds.ToList().IndexOf(tab);
                    await Shot(shot);
                }
            }
        }

        // System info: the demo opener's modal with the invented PC (a separate window, captured on its own).
        _ = _w.Dispatcher.BeginInvoke(() => new DemoSystemInfoOpener().Show(_w, _services));
        await Settle(900);
        if (DemoSystemInfoOpener.Current is { } info)
        {
            SaveWindow(info, "62-system-info.png");
            info.Close();
            await Settle(200);
        }
        _w.NavigateTo("home");
    }

    /// <summary>The demo's Tweaks page, scanned (on the invented registry), or null when the section isn't the v3.1 page.</summary>
    private async Task<PcTidy.Features.Tweaks.UI.TweaksViewModel?> ScannedTweaksAsync()
    {
        _w.NavigateTo("tweaks");
        await Settle(300);
        if (_w.PageObject("tweaks") is not FrameworkElement { DataContext: PcTidy.Features.Tweaks.UI.TweaksViewModel vm }) return null;
        if (!vm.HasScanned) await vm.ScanAsync(onlyTweaks: false);
        await Settle(300);
        return vm;
    }

    // ---------------- --shell-shots: the desktop-integration screens only (demo or practice PC) ----------------

    private async Task ShellSetAsync()
    {
        var prefix = _services.IsDemo ? "demo" : "pc";
        var theme = Name(_theme);
        _w.NavigateTo("settings");
        await Settle(400);
        _w.OpenSettingsSection("background");
        await Shot($"{prefix}-{theme}-1-settings-background.png");
        _w.OpenSettingsSection("updates");
        await Shot($"{prefix}-{theme}-2-settings-updates.png");

        // Import preview with a made-up file: a few harmless changes, one that loosens protection, one path not on this PC.
        if (_w.PageFor<SettingsPage>("settings") is { } settings)
        {
            var current = _services.Get<AppSettings>();
            var theirs = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(current, AppSettings.Json), AppSettings.Json)!;
            theirs.Theme = "Light";
            theirs.QuarantineDays = 3;
            theirs.ExpertMode = true;
            theirs.ScanExclusions.Add(@"Q:\Old backups");
            // The demo's pretend PC lives in the temp folder, whose path names the real user: no such path on screen.
            if (!_services.IsDemo) theirs.ExtraProtectedFolders.Add(Path.Combine(_services.Get<IKnownLocations>().Documents, "Tax"));
            ShellPrefs.Set(theirs, ShellPrefs.Get(theirs) with { QuietFrom = "21:00", WhatIsMenu = true });
            var preview = settings.ImportPackageAsync(new PcTidy.App.Portability.LoadedPackage(new PcTidy.App.Portability.PackageManifest { AppVersion = "1.0.0", CreatedUtc = DateTime.UtcNow.AddDays(-2) }, theirs, null));
            await Settle(900);
            Save($"{prefix}-{theme}-3-import-preview.png");
            CloseOpenDialog();
            await preview;
        }

        // "What is this?" for real files of the practice PC (the demo has no files on disk).
        if (!_services.IsDemo && _desktop is not null)
        {
            var k = _services.Get<IKnownLocations>();
            int n = 4;
            foreach (var path in WhatIsSamples(k))
            {
                if (_desktop.ShowWhatIs(path) is not { } win) continue;
                await win.Measured.WaitAsync(TimeSpan.FromSeconds(25));
                await Settle(500);
                SaveWindow(win, $"{prefix}-{theme}-{n++}-whatis-{win.Report.Kind.ToString().ToLowerInvariant()}.png");
                win.Close();
            }
        }

        // Clean opened by another section with two items picked for it (scan first, nothing runs).
        if (_w.PageFor<StandardFlowPage>("cleanup") is { } clean)
        {
            _w.NavigateTo("cleanup");
            await clean.StartScanAsync();
            var ids = clean.Selection.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items).Where(i => i.IsSelectable && !i.IsPermanent).Skip(1).Take(2).Select(i => i.Item.Id).ToList();
            await clean.PreselectAsync(ids.Append("no-longer-there").ToList());
            await Shot($"{prefix}-{theme}-12-clean-preselected.png");
        }
        _w.NavigateTo("quarantine");
        if (_w.PageFor<QuarantinePage>("quarantine") is { } q) await q.RefreshAsync();
        await Shot($"{prefix}-{theme}-13-quarantine.png");
    }

    /// <summary>One sample of each kind on the practice PC (only paths that exist).</summary>
    private static IEnumerable<string> WhatIsSamples(IKnownLocations k)
    {
        string? First(string folder, string pattern = "*")
        {
            try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).FirstOrDefault() : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        var candidates = new[]
        {
            First(k.UserTemp),
            First(Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data")),
            First(k.WindowsDir),
            k.GameInstallDirs.Select(d => First(d)).FirstOrDefault(f => f is not null),
            k.CloudRoots.Select(d => First(d)).FirstOrDefault(f => f is not null),
            First(k.Downloads),
            k.Downloads,
        };
        return candidates.Where(p => p is not null).Select(p => p!).Distinct();
    }

    private void CloseOpenDialog()
    {
        foreach (var d in Descendants<ContentDialog>(_w)) d.Hide(ContentDialogResult.None);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    // ---------------- sandbox: a practice PC; scans and simulations only ----------------

    private async Task SandboxSetAsync()
    {
        var home = _w.PageFor<HomePage>("home")!;
        _w.NavigateTo("home");
        await Settle(800);
        await Shot("00-home-not-checked.png");
        await home.CheckFreeSpaceAsync();
        await Shot("01-home.png");

        if (_w.PageFor<StandardFlowPage>("cleanup") is { } clean)
        {
            _w.NavigateTo("cleanup");
            await Shot("02-clean-ready.png");
            await clean.StartScanAsync();
            await Shot("03-clean-preview.png");
            await ConfirmAndBackAsync(clean.RunAsync, "04-clean-confirm.png");
            await clean.SimulateAsync();
            await Shot("05-clean-simulation.png");
            clean.BackToPreview();
        }
        if (_w.PageFor<StandardFlowPage>("organize") is { } organize)
        {
            _w.NavigateTo("organize");
            await organize.StartScanAsync();
            await Shot("06-organize-preview.png");
            await ConfirmAndBackAsync(organize.RunAsync, "07-organize-confirm.png");
        }
        var optimize = _w.PageFor<CombinedFlowPage>("optimize")!;
        _w.NavigateTo("optimize");
        await optimize.StartScanAsync();
        await Shot("08-optimize-preview.png");
        await ConfirmAndBackAsync(optimize.ConfirmAsync, "09-optimize-confirm.png");
        await optimize.SimulateAsync();
        await Shot("10-optimize-simulation.png");

        _w.NavigateTo("history");
        await _w.PageFor<HistoryPage>("history")!.RefreshAsync();
        await Shot("11-history.png");
        _w.NavigateTo("quarantine");
        await _w.PageFor<QuarantinePage>("quarantine")!.RefreshAsync();
        await Shot("12-quarantine.png");
        _w.NavigateTo("settings");
        await Shot("13-settings.png");
        await OnboardingAsync(14);
    }

    /// <summary>Opens the confirm dialog, captures it, then closes it with Back: nothing runs.</summary>
    private async Task ConfirmAndBackAsync(Func<Task> start, string shot)
    {
        var task = start();
        await Settle(700);
        Save(shot);
        ConfirmDialog.Current?.Hide(ContentDialogResult.None);
        await task;
        await Settle(200);
    }

    private async Task OnboardingAsync(int first)
    {
        _w.NavigateTo("home");
        _w.ShowOnboarding(true);
        for (int i = 0; i < 3; i++)
        {
            _w.Onboarding!.Show(i);
            await Shot($"{first + i}-onboarding-{i + 1}.png");
        }
        _w.ShowOnboarding(false);
    }

    /// <summary>--demo-minimal: a one-drive PC with no OneDrive and no games.</summary>
    private async Task MinimalSetAsync()
    {
        _w.NavigateTo("home");
        await Settle(400);
        await _w.PageFor<HomePage>("home")!.CheckFreeSpaceAsync();
        await Shot("30-home-one-drive.png");
        _w.NavigateTo("settings");
        await Shot("31-settings-no-onedrive.png");
    }

    private async Task Shot(string name)
    {
        await Settle(500);
        Save(name);
    }

    private static async Task Settle(int ms)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private void Save(string name) => SaveWindow(_w, name);

    private void SaveWindow(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int w = (int)Math.Round(root.ActualWidth), h = (int)Math.Round(root.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(root);
        var bg = (Brush?)Application.Current.TryFindResource("ApplicationBackgroundBrush") ?? Brushes.Black;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            // Open popups (a preset's "what will change") live in their own window: draw them where they are on screen.
            foreach (var popup in Descendants<System.Windows.Controls.Primitives.Popup>(root).Where(p => p.IsOpen && p.Child is FrameworkElement { IsVisible: true }))
            {
                var child = (FrameworkElement)popup.Child;
                try
                {
                    var at = child.PointToScreen(new Point(0, 0)) - root.PointToScreen(new Point(0, 0));
                    var rect = new Rect(at.X / dpi.DpiScaleX, at.Y / dpi.DpiScaleY, child.ActualWidth, child.ActualHeight);
                    dc.DrawRectangle(new VisualBrush(child) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
                }
                catch (InvalidOperationException) { /* not on screen yet */ }
            }
        }
        var rtb = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(_dir, name));
        enc.Save(fs);
    }
}
