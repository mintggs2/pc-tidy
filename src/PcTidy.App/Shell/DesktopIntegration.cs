using System.Windows;
using System.Windows.Threading;
using PcTidy.App.Composition;
using PcTidy.App.Notifications;
using PcTidy.App.Pages;
using PcTidy.App.Tray;
using PcTidy.App.WhatIs;
using PcTidy.Core;
using PcTidy.UI.Flow;

namespace PcTidy.App.Shell;

/// <summary>
/// Everything that connects PC Tidy to the Windows desktop while it runs: requests forwarded from Explorer's
/// right-click entries (What is this?, Tidy this folder), the optional tray icon with its low-disk alert, and clicks
/// on PC Tidy's notifications. Created once by the app; all methods run on the UI thread.
/// </summary>
public sealed class DesktopIntegration : IDisposable
{
    /// <summary>The low-disk check runs this often while the tray icon is on (the first one a few minutes after start).</summary>
    private static readonly TimeSpan AlertInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstAlertDelay = TimeSpan.FromMinutes(3);
    /// <summary>At most this many "What is this?" windows at once (selecting 200 files mustn't open 200 windows).</summary>
    private const int MaxWhatIsWindows = 6;

    private readonly AppServices _services;
    private readonly MainWindow _window;
    private readonly List<WhatIsWindow> _whatIs = new();
    private readonly IDriveSpaceSource _drives;
    private TrayIcon? _tray;
    private DispatcherTimer? _alerts;

    public DesktopIntegration(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;
        Notifications = AppNotifications.For(services);
        _drives = Notifications.IsReal ? new FixedDriveSpace()
            : services.GetService(typeof(ISystemInfo)) is ISystemInfo sys ? new SimulatedDriveSpace(sys) : new FixedDriveSpace();
        // Listening registers PC Tidy with Windows' notification system, so only when a notification can exist:
        // Windows started PC Tidy because one was clicked.
        if (Notifications.IsReal && services.Args.Raw.Any(a => a.Equals("-ToastActivated", StringComparison.OrdinalIgnoreCase))) ListenForClicks();
    }

    private bool _listening;

    private void ListenForClicks()
    {
        if (_listening || !Notifications.IsReal) return;
        _listening = true;
        ToastNotifier.ListenForClicks((page, mute) => _window.Dispatcher.BeginInvoke(() => OnNotificationClick(page, mute)));
    }

    public AppNotifications Notifications { get; }

    /// <summary>Raised when the last window closes and nothing keeps PC Tidy open (no main window shown, no tray).</summary>
    public event Action? NothingLeftOpen;

    private AppSettings Settings => _services.Get<AppSettings>();

    // ---------------- forwarded requests ----------------

    public void Handle(ShellCommand command)
    {
        switch (command.Verb)
        {
            case ShellCommand.WhatIs when command.Path is { } p: ShowWhatIs(p); break;
            case ShellCommand.Tidy when command.Path is { } f: _window.Navigation.OpenFolder(f); break;
            default: _window.ShowFromBackground(); break;
        }
    }

    /// <summary>Opens (or brings forward) the read-only explanation window for one path.</summary>
    public WhatIsWindow? ShowWhatIs(string path)
    {
        var existing = _whatIs.FirstOrDefault(w => string.Equals(w.Report.Path, PathUtil.Normalize(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { existing.Activate(); return existing; }
        if (_whatIs.Count >= MaxWhatIsWindows) { _whatIs[^1].Activate(); return null; }

        var report = Classifier().Classify(path);
        var window = new WhatIsWindow(report, _services.Get<IFileWalker>(), _window.HasPage, solidBackground: _window.ScreenshotMode);
        window.OpenSectionRequested += section => { _window.ShowFromBackground(); _window.FlowHost.NavigateTo(section); };
        window.ShowInExplorerRequested += p => _window.FlowHost.OpenInExplorer(p);
        window.Closed += (_, _) =>
        {
            _whatIs.Remove(window);
            if (_whatIs.Count == 0 && !_window.IsVisible && _tray is null) NothingLeftOpen?.Invoke();
        };
        _whatIs.Add(window);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>The classifier for this mode: the real Uninstall registry, or the practice PC's sample apps.</summary>
    public WhatIsClassifier Classifier()
    {
        var k = _services.Get<IKnownLocations>();
        IInstalledApps apps = k.IsSandbox ? FixedInstalledApps.FromLocations(k) : new UninstallRegistryApps();
        return new WhatIsClassifier(k, _services.Get<ISafetyGuard>(), apps, Settings.DataRoot);
    }

    // ---------------- tray ----------------

    public bool TrayShown => _tray is not null;

    /// <summary>Shows or removes the tray icon. With it, closing the window keeps PC Tidy in the tray.</summary>
    public void SetTray(bool on)
    {
        if (on == TrayShown || _window.ScreenshotMode) return;
        if (on)
        {
            try
            {
                _tray = new TrayIcon(new TrayActions(
                    Open: () => _window.ShowFromBackground(),
                    QuickTidy: QuickTidy,
                    CheckNow: () => CheckNowAsync().Forget(_window.FlowHost, "Check didn't finish"),
                    Quit: () => _window.Quit()));
            }
            catch (Exception ex)
            {
                CrashHandler.Write(ex, "Tray icon", terminating: false);
                _window.FlowHost.ShowMessage("Couldn't show the tray icon", "Windows didn't accept PC Tidy's tray icon. PC Tidy works the same without it.", isError: true);
                return;
            }
            ListenForClicks(); // the tray's alerts are notifications this copy should answer
            _alerts = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = FirstAlertDelay };
            _alerts.Tick += (_, _) => { _alerts!.Interval = AlertInterval; CheckLowDisk(); };
            _alerts.Start();
        }
        else
        {
            _alerts?.Stop();
            _alerts = null;
            _tray?.Dispose();
            _tray = null;
        }
        _window.KeepInTray = TrayShown;
    }

    /// <summary>"Quick tidy": the Optimize everything preview (a read-only scan). Nothing is cleaned without the confirm.</summary>
    private void QuickTidy()
    {
        _window.ShowFromBackground();
        _window.NavigateTo("optimize");
        if (_window.PageFor<CombinedFlowPage>("optimize") is { State: FlowState.Ready or FlowState.Done } page)
            page.StartScanAsync().Forget(_window.FlowHost, "Scan didn't finish");
    }

    /// <summary>"Check now": Home's free-space and health check, plus the low-disk alert right away.</summary>
    private async Task CheckNowAsync()
    {
        _window.ShowFromBackground();
        _window.NavigateTo("home");
        if (_window.PageFor<HomePage>("home") is { } home) await home.CheckFreeSpaceAsync();
        CheckLowDisk();
    }

    /// <summary>Low-disk alert: the Windows drive under 10 % and under 20 GB free, at most once a day, never in quiet hours.</summary>
    public AlertVerdict? CheckLowDisk()
    {
        IReadOnlyList<DriveSpace> drives;
        try { drives = _drives.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
        var systemDrive = (_services.GetService(typeof(IKnownLocations)) as IKnownLocations)?.SystemDrive;
        if (LowDiskCheck.Notification(LowDiskCheck.LowDrives(drives, systemDrive)) is not { } n) return null;
        return Notifications.TryShow(n, ShellPrefs.Get(Settings));
    }

    // ---------------- notification clicks ----------------

    /// <summary>Only PC Tidy's own page keys are followed; "Turn off these alerts" really turns them off.</summary>
    private void OnNotificationClick(string? page, AlertKind? mute)
    {
        if (mute is { } kind)
        {
            var prefs = ShellPrefs.Get(Settings);
            ShellPrefs.Set(Settings, kind == AlertKind.LowDisk ? prefs with { LowDiskAlerts = false } : prefs with { ScanReportAlerts = false });
            _window.SaveSettingsQuietly();
            return;
        }
        _window.ShowFromBackground();
        if (page is "optimize" or "schedule" or "home" or "settings" && _window.HasPage(page)) _window.NavigateTo(page);
    }

    public void Dispose()
    {
        SetTray(false);
        foreach (var w in _whatIs.ToList()) w.Close();
    }
}
