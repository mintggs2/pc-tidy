using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PcTidy.Features.Speed.Common;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Speed.UI;

/// <summary>What a Speed tab shows and which toolbar options it has.</summary>
public sealed record SpeedTabSpec(
    string Title,
    string Blurb,
    IFeatureScanner Scanner,
    string Icon,
    bool ShowHideMicrosoft = false,
    bool ShowCopyPath = false,
    bool PicksPath = false,
    bool AutoScan = true);

/// <summary>
/// One Speed tab: read → pick → confirm on the shared results list. Scanning only reads; the confirm, run, Done card
/// and undo belong to the shell's flow host. "Copy path" puts the selected row's registry key (or folder, or task)
/// on the clipboard, so you can find it yourself in regedit or Explorer.
/// </summary>
public partial class SpeedTabView : UserControl
{
    /// <summary>Detail keys that hold a location worth copying, in order of preference.</summary>
    internal static readonly string[] CopyableDetails = { "Registry key", "Folder", "Task", "File" };

    private readonly IServiceProvider _services;
    private readonly SpeedTabSpec _spec;
    private SelectionModel? _model;
    private CancellationTokenSource? _scanCts;
    private IReadOnlyList<string>? _roots;
    private bool _scanned;

    public SpeedTabView(IServiceProvider services, SpeedTabSpec spec)
    {
        InitializeComponent();
        SetStep(1);
        _services = services;
        _spec = spec;
        TabTitle.Text = spec.Title;
        TabBlurb.Text = spec.Blurb;
        AutomationProperties.SetName(this, spec.Title);
        var icon = Enum.TryParse<SymbolRegular>(spec.Icon, out var s) ? s : SymbolRegular.TopSpeed24;
        ReadyEmpty.Icon = icon;

        HideMicrosoft.Visibility = spec.ShowHideMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        HideMicrosoft.Checked += (_, _) => RescanIfShown();
        HideMicrosoft.Unchecked += (_, _) => RescanIfShown();
        CopyPathButton.Visibility = spec.ShowCopyPath ? Visibility.Visible : Visibility.Collapsed;
        PickFileButton.Visibility = PickFolderButton.Visibility = spec.PicksPath ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Visibility = spec.PicksPath ? Visibility.Collapsed : Visibility.Visible;

        if (spec.PicksPath)
        {
            ReadyEmpty.Title = "Pick a file or folder";
            ReadyEmpty.Message = "PC Tidy asks Windows which apps have it open. Nothing is closed until you confirm, and Windows and security apps are never closed.";
            ReadyEmpty.ActionText = "Pick a file";
            ReadyEmpty.ActionIcon = SymbolRegular.Document24;
        }
        else
        {
            ReadyEmpty.Title = $"Scan to see {spec.Title.ToLowerInvariant()}";
            ReadyEmpty.Message = "Scanning only looks. You get a plain-English list with reasons, and nothing changes until you confirm.";
        }

        if (Results.FindName("List") is Selector list)
            list.SelectionChanged += (_, _) => CopyPathButton.IsEnabled = CopyablePath(list.SelectedItem as ItemRow) is not null;
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    /// <summary>Read-only tabs scan the first time they're shown.</summary>
    public void EnsureScanned()
    {
        if (_spec.AutoScan && !_scanned) _ = ScanAsync();
    }

    private void RescanIfShown()
    {
        if (_scanned) _ = ScanAsync();
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnReadyAction(object sender, RoutedEventArgs e)
    {
        if (_spec.PicksPath) OnPickFile(sender, e);
        else _ = ScanAsync();
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    // ------------------------------------------------------------------ pick (What's locking)

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Which file is in use?", CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Pick(dialog.FileName);
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Which folder is in use?", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Pick(dialog.FolderName);
    }

    private void Pick(string path)
    {
        _roots = new[] { path };
        PickedPath.Text = path;
        PickedPath.Visibility = Visibility.Visible;
        ScanButton.Visibility = Visibility.Visible;
        ScanButton.Content = "Check again";
        _ = ScanAsync();
    }

    // ------------------------------------------------------------------ scan

    private ScanContext Context()
    {
        var b = ScanRunner.CreateContext(_services, ScanDepth.Quick);
        return new ScanContext
        {
            Locations = b.Locations, Safety = b.Safety, InUse = b.InUse, Settings = b.Settings, Walker = b.Walker,
            Depth = b.Depth, Preset = b.Preset, RunningProcesses = b.RunningProcesses, System = b.System,
            Roots = _roots,
            Options = new Dictionary<string, object> { [SpeedOptions.HideMicrosoft] = HideMicrosoft.IsChecked != false },
        };
    }

    public async Task ScanAsync()
    {
        if (_scanCts is not null) return;
        if (_spec.PicksPath && _roots is null) return;
        _scanned = true;
        _scanCts = new CancellationTokenSource();
        Show(scanning: true);
        ScanMessage.Text = "Starting…";
        try
        {
            var ctx = Context();
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanMessage.Text = p.Message;
                if (p.Fraction is { } f) { ScanBar.IsIndeterminate = false; ScanBar.Value = Math.Clamp(f, 0, 1) * 100; }
            });
            var token = _scanCts.Token;
            var result = await Task.Run(() => _spec.Scanner.ScanAsync(ctx, progress, token), token);
            Load(result);
        }
        catch (OperationCanceledException)
        {
            Show(scanning: false);
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("Scan didn't finish", "Something went wrong while looking: " + ex.Message + " Nothing was changed.", isError: true);
            Show(scanning: false);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            // Re-enabled here, not only in Show(): a finished scan goes through Load(), which never touched the buttons.
            ScanButton.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = true;
        }
    }

    private void Load(ScanResult result)
    {
        _model = new SelectionModel(showSectionHeaders: false);
        _model.Changed += UpdateBar;
        _model.Load(new[] { new SectionModel(result.FeatureId, _spec.Title, SymbolRegular.TopSpeed24, result) },
            (_, _, i) => OptimizePresets.IsTickableByDefault(i.Item),
            (s, g) => g == s.Groups.FirstOrDefault());
        Results.Model = _model;
        Results.OpenInExplorerRequested -= OnOpenInExplorer;
        Results.OpenInExplorerRequested += OnOpenInExplorer;
        CopyPathButton.IsEnabled = false;

        NotesPanel.Children.Clear();
        foreach (var note in result.Notes)
        {
            var bar = new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
            bar.Loaded += (_, _) => NameCloseButton(bar);
            NotesPanel.Children.Add(bar);
        }

        var empty = _model.TotalItems == 0;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingFound.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Title = _spec.PicksPath ? "Not in use" : "All tidy";
        NothingFound.Message = _spec.PicksPath ? "No app has it open right now." : $"Nothing to show in {_spec.Title} right now.";
        ReadyState.Visibility = Visibility.Collapsed;
        ScanningState.Visibility = Visibility.Collapsed;
        ResultsState.Visibility = Visibility.Visible;
        StickyBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!_spec.PicksPath) ScanButton.Content = "Scan again";
        SetStep(2);
        UpdateBar();
    }

    private void OnOpenInExplorer(string path) => Host?.OpenInExplorer(path);

    /// <summary>WPF-UI's InfoBar close button has no name of its own: give it one for screen readers.</summary>
    private static void NameCloseButton(DependencyObject bar)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(bar);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is System.Windows.Controls.Primitives.ButtonBase b && string.IsNullOrEmpty(AutomationProperties.GetName(b))) AutomationProperties.SetName(b, "Close this note");
            for (int i = System.Windows.Media.VisualTreeHelper.GetChildrenCount(cur) - 1; i >= 0; i--) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(cur, i));
        }
    }

    /// <summary>The shared Scan → Preview → Confirm → Done bar (with what was found under "Scan").</summary>
    private void SetStep(int step, bool scanning = false) => Steps.Set(step,
        scanning ? "Scanning…" : _model is null ? "Looks only" : $"{_model.TotalItems:N0} found",
        "Pick what changes", step >= 3 ? "One last check" : "Nothing changed yet", "");

    private void Show(bool scanning)
    {
        SetStep(scanning || _model is null ? 1 : 2, scanning);
        ScanningState.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = !scanning;
        if (scanning) { ReadyState.Visibility = ResultsState.Visibility = StickyBar.Visibility = Visibility.Collapsed; return; }
        var hasResults = _model is not null;
        ReadyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsState.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        StickyBar.Visibility = hasResults && _model!.TotalItems > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBar()
    {
        if (_model is null) return;
        var actions = _model.CheckedActions();
        var items = _model.CheckedCount;
        SelectedText.Text = items == 0 ? "Nothing selected" : $"Selected {items:N0} item{(items == 1 ? "" : "s")}";
        SelectedCaption.Text = Caption(actions);
        PrimaryButton.Content = PlanFacts.PrimaryAction(actions);
        PrimaryButton.IsEnabled = SimulateButton.IsEnabled = actions.Count > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
    }

    /// <summary>What happens to the ticked rows, in plain words (shown above the buttons).</summary>
    internal static string Caption(IReadOnlyList<PlanItem> actions)
    {
        var parts = new List<string>();
        if (actions.Any(a => a.Kind == ActionKind.CloseApp)) parts.Add("apps are asked to close and can ask you to save");
        if (actions.Any(a => a.Kind is ActionKind.StartupItemSetEnabled or ActionKind.ScheduledTaskSetEnabled or ActionKind.ServiceSetStartType))
            parts.Add("startup changes can be undone from History");
        if (actions.Any(a => a.Kind is ActionKind.RegistrySetValue or ActionKind.PowerPlanSet or ActionKind.ShellExtensionSetEnabled))
            parts.Add("the old setting is kept so you can undo");
        if (actions.Any(a => a.Kind == ActionKind.VolumeOptimize)) parts.Add("Windows' own optimizer runs; it can take a while on a hard drive");
        if (actions.Any(a => a.NeedsAdmin)) parts.Add("asks for admin once");
        if (parts.Count == 0) return "";
        var text = string.Join(" · ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: false);

    private void OnSimulate(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: true);

    private async Task RunAsync(bool simulate)
    {
        if (_model is null) return;
        var plans = _model.BuildPlans(parentRunId: null);
        if (plans.Count == 0) return;
        SetStep(3);
        try
        {
            var options = new FlowOptions
            {
                Title = _spec.Title, ShowResult = true, RestartItemIds = _model.RestartIds(),
                PrimaryVerb = PlanFacts.PrimaryAction(plans.SelectMany(p => p.Items).ToList()),
            };
            var ran = false;
            if (Shell is { } shell)
            {
                if (simulate) await shell.SimulateAsync(plans, options, CancellationToken.None);
                else ran = (await shell.ReviewAndRunAsync(plans, options, CancellationToken.None)).Kind == FlowOutcomeKind.Ran;
            }
            else if (Host is { } host)
            {
                foreach (var plan in plans)
                {
                    if (simulate) await host.SimulateAsync(plan, CancellationToken.None);
                    else ran |= await host.ReviewAndRunAsync(plan, CancellationToken.None) is not null;
                }
            }
            // Show the new state after a real run (only reads, changes nothing).
            if (ran) await ScanAsync();
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
        if (_scanCts is null) SetStep(_model is null ? 1 : 2);
    }

    // ------------------------------------------------------------------ copy path ("jump to key")

    internal static string? CopyablePath(ItemRow? row)
    {
        if (row?.Item.Details is not { } d) return null;
        foreach (var key in CopyableDetails)
            if (d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (Results.FindName("List") is not Selector list || CopyablePath(list.SelectedItem as ItemRow) is not { } path) return;
        try
        {
            Clipboard.SetText(path);
            CopyPathButton.Content = "Copied";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { CopyPathButton.Content = "Copy path"; timer.Stop(); };
            timer.Start();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app is holding the clipboard; the user can simply click again.
            CopyPathButton.Content = "Try again";
        }
    }
}
