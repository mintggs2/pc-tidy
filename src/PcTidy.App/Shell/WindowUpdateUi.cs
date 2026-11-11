using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PcTidy.App.Composition;
using PcTidy.App.Updates;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PcTidy.App.Shell;

/// <summary>The updater's window side in the app: the main window's dialogs, progress overlay and busy state.</summary>
public sealed class WindowUpdateUi : IUpdateUi
{
    private readonly MainWindow _window;
    private readonly AppServices _services;

    public WindowUpdateUi(MainWindow window, AppServices services)
    {
        _window = window;
        _services = services;
    }

    private FlowHost Host => _window.FlowHost;

    public AppSettings Settings => _services.Get<AppSettings>();

    public void SaveSettings() => _window.SaveSettingsQuietly();

    /// <summary>
    /// A run, an undo, a resume or roll back (the one-task gate, FlowHost and the executor), a scan, or an open dialog:
    /// the dialog gate is taken (a question such as "Undo this run?" is open or queued) or a modal window is up.
    /// </summary>
    public string? BusyReason(bool holdingDialogGate = false)
        => UpdatePolicy.BusyReason(_services.GetService(typeof(RunGate)) as RunGate ?? RunGate.Shared, Host.IsWorking, ExecutorChanging(), _window.IsScanning,
            dialogOpen: holdingDialogGate ? ComponentDispatcher.IsThreadModal : Host.IsDialogOpen);

    /// <summary>The engine's executor exposes <c>bool IsChanging</c> (not on Core's IExecutor); read by name like FlowHost does.</summary>
    private bool ExecutorChanging()
    {
        if (_services.GetService(typeof(IExecutor)) is not { } executor) return false;
        try { return executor.GetType().GetProperty("IsChanging", typeof(bool))?.GetValue(executor) is true; }
        catch (Exception) { return false; }
    }

    public bool ReadyForNotice => _window.IsVisible && _window.WindowState != WindowState.Minimized && !_window.IsProgressShown;

    public async Task<UpdateNoticeChoice?> ShowNoticeIfAsync(UpdateOffer offer, Version current, bool practice, Func<bool> stillWanted)
    {
        var body = new StackPanel { Width = 520 };
        body.Children.Add(FlowHost.Body($"You have PC Tidy {ReleaseVersion.Show(current)}. What's new in {offer.VersionText}:"));
        // Plain text only: the release notes come from the internet, so they are never rendered as HTML or links.
        var notes = new TextBlock { Text = offer.Notes, TextWrapping = TextWrapping.Wrap };
        notes.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        System.Windows.Automation.AutomationProperties.SetName(notes, "Release notes");
        body.Children.Add(new ScrollViewer { MaxHeight = 260, Content = notes, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true });
        var caption = new TextBlock
        {
            Text = "From PC Tidy's releases on GitHub. Nothing is installed until you confirm once more after the download." + (practice ? " (Practice mode: invented release, nothing goes online.)" : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        caption.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        body.Children.Add(caption);
        var dialog = new ContentDialog(_window.DialogHost)
        {
            Title = $"PC Tidy {offer.VersionText} is available",
            Content = body,
            PrimaryButtonText = "Update now",
            SecondaryButtonText = "Skip this version",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await Host.ShowDialogIfAsync(dialog, stillWanted);
        return choice switch
        {
            null => null,
            ContentDialogResult.Primary => UpdateNoticeChoice.UpdateNow,
            ContentDialogResult.Secondary => UpdateNoticeChoice.Skip,
            _ => UpdateNoticeChoice.Later,
        };
    }

    public async Task<bool> ConfirmInstallAsync(string title, string body)
    {
        var confirm = new ContentDialog(_window.DialogHost)
        {
            Title = title,
            Content = FlowHost.Body(body),
            PrimaryButtonText = "Install and close PC Tidy",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
        };
        return await Host.ShowDialogAsync(confirm) == ContentDialogResult.Primary;
    }

    public void ShowMessage(string title, string message, bool isError = false) => Host.ShowMessage(title, message, isError);

    public CancellationToken ShowProgress(string title, bool canStop, string stopText = "Stop after this step") => _window.ShowProgress(title, canStop, stopText);

    public void ReportProgress(double fraction) => _window.ReportProgress(new RunProgress((int)(fraction * 99), 100, $"{fraction:P0}"));

    public void HideProgress() => _window.HideProgress();

    public void OpenInExplorer(string path) => Host.OpenInExplorer(path);

    public void Quit() => _window.Quit();

    public Task RunOnUiAsync(Func<Task> work) => _window.Dispatcher.InvokeAsync(work).Task.Unwrap();
}
