using System.IO.Compression;
using System.Windows;
using PcTidy.Core;
using PcTidy.Setup.Install;
using PcTidy.Setup.Payload;
using PcTidy.Setup.Windows;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PcTidy.Setup;

/// <summary>
/// The Setup / uninstall window: Welcome → Options → Progress → Done, or Remove → Progress → Done.
/// Only page flow here; the work is done by <see cref="SetupFlow"/> (one admin prompt for the Program Files part).
/// </summary>
public partial class SetupWindow : FluentWindow
{
    private enum Step { Welcome, Options, Working, Done, Uninstall }

    private readonly PayloadInfo? _payload;
    private readonly string _version = "";
    private readonly InstallEnvironment _env;
    private readonly bool _uninstalling;
    private readonly string? _runningUninstaller;
    private readonly ExistingInstall? _existing;
    private Step _step;
    private bool _succeeded;

    private SetupWindow(InstallEnvironment env)
    {
        _env = env;
        InitializeComponent();
        Closing += (_, e) => { if (_step == Step.Working) e.Cancel = true; };
    }

    private SetupWindow(PayloadInfo payload, string version, InstallEnvironment env) : this(env)
    {
        _payload = payload;
        _version = version;
        _existing = new Registration(env.Registry).FindExisting();
        Bar.Title = $"PC Tidy {version} Setup";
        VersionText.Text = $"Version {version}";
        UpdateInfo.IsOpen = _existing is not null;
        if (_existing is not null)
            UpdateInfo.Message = $"PC Tidy {_existing.Version} is installed. It will be updated in place; your settings, history and quarantine are kept.";
        FolderBox.Text = env.InstallDir;
        FolderHint.Text = (_existing is not null ? "Updating the existing installation. " : "")
                          + $"About {SpaceNeeded(payload)} of space needed. Windows asks for admin permission once, to put PC Tidy in Program Files.";
        Show(Step.Welcome);
    }

    private SetupWindow(InstallEnvironment env, string? runningUninstaller) : this(env)
    {
        _uninstalling = true;
        _runningUninstaller = runningUninstaller;
        Bar.Title = "Remove PC Tidy";
        DataText.Text = $"Your data folder ({env.DataRoot}) holds your history and the quarantine: files PC Tidy set aside so you can get them back. " +
                        "Keep it if you might still want them. A copy of PC Tidy Rescue stays in it too.";
        Show(Step.Uninstall);
    }

    public static SetupWindow ForInstall(PayloadInfo payload, string version) => new(payload, version, InstallEnvironment.Real());
    public static SetupWindow ForUninstall(InstallEnvironment env, string? runningUninstaller = null) => new(env, runningUninstaller);

    private static string SpaceNeeded(PayloadInfo payload)
    {
        try
        {
            using var zip = new ZipArchive(payload.Open(), ZipArchiveMode.Read);
            return Sizes.Format(zip.Entries.Sum(e => e.Length));
        }
        catch { return Sizes.Format(payload.Length * 2); }
    }

    // ------------------------------------------------------------------ page flow

    private void Show(Step step)
    {
        _step = step;
        WelcomePanel.Visibility = Vis(step == Step.Welcome);
        OptionsPanel.Visibility = Vis(step == Step.Options);
        ProgressPanel.Visibility = Vis(step == Step.Working);
        DonePanel.Visibility = Vis(step == Step.Done);
        UninstallPanel.Visibility = Vis(step == Step.Uninstall);

        BackButton.Visibility = Vis(step == Step.Options);
        CancelButton.Visibility = Vis(step is Step.Welcome or Step.Options or Step.Uninstall);
        NextButton.IsEnabled = step != Step.Working;
        NextButton.Content = step switch
        {
            Step.Options => _existing is null ? "Install" : "Update",
            Step.Uninstall => "Remove",
            Step.Done => _succeeded && !_uninstalling && LaunchBox.IsChecked == true ? "Start PC Tidy" : "Close",
            _ => "Next",
        };
        NextButton.Appearance = step == Step.Uninstall ? ControlAppearance.Danger : ControlAppearance.Primary;
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Welcome: Show(Step.Options); break;
            case Step.Options: await InstallAsync(); break;
            case Step.Uninstall: await UninstallAsync(); break;
            case Step.Done: Finish(); break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(Step.Welcome);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void RemoveDataBox_Changed(object sender, RoutedEventArgs e) => DataWarning.Visibility = Vis(RemoveDataBox.IsChecked == true);

    // ------------------------------------------------------------------ install / uninstall

    private async Task InstallAsync()
    {
        if (!await CloseRunningAsync()) return;

        ProgressTitle.Text = _existing is null ? "Installing PC Tidy…" : "Updating PC Tidy…";
        Show(Step.Working);
        Report(new SetupProgress(0, "Checking the download is complete…"));
        SetupResult result;
        if (!await Task.Run(() => _payload!.Verify()))
            result = SetupResult.Fail("This installer is damaged or incomplete. Please download PC Tidy Setup again.");
        else
            result = await new SetupFlow(_env, new RunAsSetupElevation())
                .InstallAsync(Program.Request(_payload!, StartMenuBox.IsChecked == true, DesktopBox.IsChecked == true), new Progress<SetupProgress>(Report));
        SetupLog.Result(result);
        ShowDone(result, result.Ok
            ? StartMenuBox.IsChecked == true ? "You'll find PC Tidy in the Start menu, with PC Tidy Rescue next to it." : $"PC Tidy is in {_env.InstallDir}."
            : "Your data, settings, history and quarantine weren't touched by this attempt.");
    }

    private async Task UninstallAsync()
    {
        var removeData = RemoveDataBox.IsChecked == true;
        if (removeData)
        {
            // Exactly what goes and what stays: only PC Tidy's own folders and files, never anything else in the folder.
            var plan = new Uninstaller(_env).PlanDataRemoval();
            var text = "Delete your PC Tidy data, including everything in quarantine? This can't be undone.\n\n" + plan.Describe();
            if (MessageBox.Show(this, text, "Delete your data too?", MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        if (!await CloseRunningAsync()) return;

        ProgressTitle.Text = "Removing PC Tidy…";
        Show(Step.Working);
        var result = await new SetupFlow(_env, new RunAsSetupElevation()).UninstallAsync(keepData: !removeData, _runningUninstaller, new Progress<SetupProgress>(Report));
        SetupLog.Result(result);
        ShowDone(result, result.Ok ? (removeData ? "Your PC Tidy data was deleted too." : $"Your data folder was kept: {_env.DataRoot}") : "");
    }

    /// <summary>Asks before closing a running PC Tidy (never kills it).</summary>
    private async Task<bool> CloseRunningAsync()
    {
        var all = _env.Processes.FindRunning(_env.InstallDir);
        if (all.Count == 0) return true;
        // Another account's copy can't be closed from here: say whose it is instead of asking to close something unseen.
        var elsewhere = all.Where(r => r.InOtherSession).ToList();
        if (elsewhere.Count > 0)
        {
            MessageBox.Show(this, RunningApp.OpenMessage(elsewhere), "PC Tidy is open in another account", MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return false;
        }
        var running = all;
        var names = string.Join(" and ", running.Select(r => r.Name == "PcTidy" ? "PC Tidy" : r.Name).Distinct());
        if (MessageBox.Show(this, $"{names} is open. Close it now to continue?", "Close PC Tidy?", MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return false;
        var still = await _env.Processes.CloseAsync(running, TimeSpan.FromSeconds(15), CancellationToken.None);
        if (still.Count == 0) return true;
        MessageBox.Show(this, $"{names} didn't close. Close it yourself (also check the icon near the clock), then try again.",
            "Still open", MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return false;
    }

    private void Report(SetupProgress p)
    {
        Progress.Value = p.Fraction;
        ProgressText.Text = p.Message;
    }

    private void ShowDone(SetupResult result, string text)
    {
        _succeeded = result.Ok;
        DoneIcon.Symbol = result.Ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;
        DoneIcon.Foreground = (System.Windows.Media.Brush)FindResource(result.Ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        DoneTitle.Text = result.Message;
        DoneText.Text = text;
        var notes = result.Notes.Where(n => !n.Contains("   at ", StringComparison.Ordinal)).Take(8).ToList(); // no stack traces on screen
        DoneNotes.Text = string.Join("\n", notes) + (SetupLog.FilePath is { } log && !result.Ok ? $"\n\nDetails: {log}" : "");
        Show(Step.Done);
    }

    /// <summary>Starts PC Tidy from this (normal-rights) window, so it never runs as admin.</summary>
    private void Finish()
    {
        if (_succeeded && !_uninstalling && LaunchBox.IsChecked == true)
        {
            try { _env.Processes.Launch(_env.AppPath); }
            catch (Exception ex) { SetupLog.Error("Starting PC Tidy", ex); }
        }
        Close();
    }
}
