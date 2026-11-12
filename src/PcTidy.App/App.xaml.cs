using System.Windows;
using PcTidy.App.Composition;
using PcTidy.App.Screenshots;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.UI.Theming;

namespace PcTidy.App;

/// <summary>
/// PC Tidy. Command line: --demo · --demo-minimal · --sandbox &lt;folder&gt; · --screenshots &lt;dir&gt; · --window WxH · --light / --dark · --release N ·
/// --whatis &lt;path&gt; · --tidy &lt;folder&gt; (Explorer's right-click entries) · --tray (started with Windows) ·
/// --elevated (admin helper) · --scheduled-scan (report-only scan) · --unregister-toasts (uninstall). The last three have no window.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _single;
    private CommandPipe? _pipe;
    private DesktopIntegration? _desktop;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashHandler.Install(this);
        var args = AppArgs.Parse(e.Args);

        if (args.UnregisterToasts)
        {
            // The uninstaller's call. Only the real PC ever registered (demo and practice mode never show real toasts).
            if (!args.Demo && args.SandboxRoot is null) ToastNotifierUnregister();
            Shutdown(0);
            return;
        }
        if (args.Elevated || args.ScheduledScan)
        {
            await RunWindowlessAsync(args);
            return;
        }

        bool screenshots = args.ScreenshotDir != null;
        var command = ShellCommand.FromArgs(args);
        bool sandbox = args.SandboxRoot != null;
        if (!screenshots)
        {
            _single = SingleInstance.TryAcquire(args.Demo, () => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromBackground()),
                sandbox, wakeExisting: command is null);
            if (_single is null)
            {
                // Explorer's right-click: hand the request to the running PC Tidy (or at least bring it forward).
                if (command is not null && !CommandPipe.TrySend(SingleInstance.NameFor(args.Demo, sandbox), command)) SingleInstance.Wake(args.Demo, sandbox);
                Shutdown(0);
                return;
            }
        }

        var services = AppServices.Build(args);
        ScanResumeSetup.Register(services); // resumable scans: checkpoints in PC Tidy's own data folder only
        TidyTheme.SolidBackdrop = screenshots;
        var window = new MainWindow(services, screenshots);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var settings = services.Get<AppSettings>();
        // 3.1.1: the old custom "update address" is dropped (updates come only from PC Tidy's GitHub releases).
        if (ShellPrefs.Migrate(settings)) window.SaveSettingsQuietly();
        window.ApplyTheme(TidyTheme.Parse(args.ForcedTheme ?? (screenshots ? "Dark" : settings.Theme)));
        if (screenshots && args.WindowSize is { } size) { window.Width = size.Width; window.Height = size.Height; }

        _desktop = new DesktopIntegration(services, window);
        services.Add(_desktop);
        _desktop.NothingLeftOpen += () => Shutdown(0); // a "What is this?" window was all this copy showed
        if (_single is not null) _pipe = CommandPipe.Listen(_single.Name, c => Dispatcher.BeginInvoke(() => _desktop?.Handle(c)));

        // Started from Explorer's right-click or with Windows (--tray): no main window until it's asked for.
        if (screenshots || (command is null && !args.Tray)) window.Show();
        if (!screenshots && (settings.TrayEnabled || args.Tray)) _desktop.SetTray(true);
        if (command is not null) _desktop.Handle(command);

        if (screenshots) await new ScreenshotRunner(window, services, args.ScreenshotDir!, _desktop).RunAsync();
        else if (!services.IsDemo)
        {
            _services = services;
            // Real PC only (never practice mode): keep the spare Rescue in the data folder as new as the installed one.
            if (args.SandboxRoot is null)
            {
                var dataRoot = settings.DataRoot;
                _ = Task.Run(() => RescueTool.RefreshSpare(dataRoot)); // never throws; failures go to the crash log
                // The once-a-day update check (Settings › Updates; on by default): later, off the UI thread, never in practice mode.
                window.Updates.StartAutomatic();
            }
            foreach (var hook in AppServices.Started)
            {
                try { hook(services); } catch (Exception ex) { CrashHandler.Write(ex, "Startup hook", terminating: false); }
            }
        }
    }

    private AppServices? _services;

    /// <summary>--elevated / --scheduled-scan: call the handler the lead wires in AppEntry; no window.</summary>
    private async Task RunWindowlessAsync(AppArgs args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int code;
        try
        {
            code = await AppEntry.RunWindowlessAsync(args, () => AppServices.Build(args with { Demo = false }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            CrashHandler.Write(ex, args.Elevated ? "Elevated helper" : "Scheduled scan", terminating: false);
            code = 1;
        }
        Shutdown(code);
    }

    /// <summary>Longest PC Tidy waits for each exit hook (watch mode, host shutdown) before closing anyway.</summary>
    private static readonly TimeSpan ExitHookTimeout = TimeSpan.FromSeconds(5);

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is { } s)
        {
            foreach (var hook in AppServices.Stopping)
            {
                // Run each hook on the thread pool: a hook that does its work synchronously (host.Dispose) or never
                // completes can't hold the window open, because the wait has a real timeout either way.
                try
                {
                    var task = Task.Run(() => hook(s));
                    if (!task.Wait(ExitHookTimeout))
                        CrashHandler.Write(new TimeoutException($"An exit hook didn't finish within {ExitHookTimeout.TotalSeconds:0} s; PC Tidy closed anyway."), "Exit hook", terminating: false);
                }
                catch (Exception ex) { CrashHandler.Write(ex, "Exit hook", terminating: false); }
            }
        }
        _pipe?.Dispose();
        try { _desktop?.Dispose(); } catch (Exception ex) { CrashHandler.Write(ex, "Closing the tray icon", terminating: false); }
        _single?.Dispose();
        base.OnExit(e);
    }

    private static void ToastNotifierUnregister() => Notifications.ToastNotifier.Unregister();
}
