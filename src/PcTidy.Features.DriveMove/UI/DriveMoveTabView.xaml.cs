using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Caches;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.DriveMove.UI;

/// <summary>What a tab shows and how.</summary>
internal sealed record TabSpec(string Title, string Blurb, DriveMovePartScanner Scanner, bool Available, string UnavailableMessage,
    bool AutoScan = false, bool ShowFolderPicker = false, bool ShowDriveStrip = false);

/// <summary>
/// One tab of the Move to drive page: Scan → pick → confirm, on the shared results list. Read-only until the user confirms;
/// the confirm dialog, run, Done card and Undo all belong to the shell's flow host. Two ticked edits of one config file
/// (npm and pnpm share .npmrc) are merged into one just before running.
/// </summary>
public partial class DriveMoveTabView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly TabSpec _spec;
    private readonly List<string> _pickedFolders = new();
    private SelectionModel? _model;
    private CancellationTokenSource? _scanCts;
    private bool _scanned;

    internal DriveMoveTabView(IServiceProvider services, TabSpec spec)
    {
        InitializeComponent();
        SetStep(1);
        _services = services;
        _spec = spec;
        TabTitle.Text = spec.Title;
        TabBlurb.Text = spec.Blurb;
        AutomationProperties.SetName(this, spec.Title);
        PickButton.Visibility = spec.ShowFolderPicker && spec.Available ? Visibility.Visible : Visibility.Collapsed;

        if (spec.Available)
        {
            ReadyEmpty.Title = $"Scan to see your {spec.Title.ToLowerInvariant()}";
            ReadyEmpty.Message = "Scanning only looks. You get a plain-English list of what could move and where, and nothing moves until you confirm.";
        }
        else
        {
            ReadyEmpty.Title = "Not available on this PC";
            ReadyEmpty.Message = spec.UnavailableMessage;
            ReadyEmpty.ActionText = null;
            ScanButton.IsEnabled = false;
        }
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    /// <summary>Tabs marked AutoScan scan the first time they're shown (only looking, never changing anything).</summary>
    public void EnsureScanned()
    {
        if (!_spec.AutoScan || _scanned || !_spec.Available) return;
        if (IsLoaded) ScanAsync().Forget(Host, "Scan didn't finish");
        else
        {
            void OnLoaded(object s, RoutedEventArgs e) { Loaded -= OnLoaded; EnsureScanned(); }
            Loaded += OnLoaded;
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => ScanAsync().Forget(Host, "Scan didn't finish");

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>Windows' folder picker; the folder is added to the list and checked like any other (never moved yet).</summary>
    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to move to another drive", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        if (!_pickedFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) _pickedFolders.Add(dialog.FolderName);
        ScanAsync().Forget(Host, "Scan didn't finish");
    }

    public async Task ScanAsync()
    {
        if (!_spec.Available || _scanCts is not null) return;
        _scanned = true;
        _scanCts = new CancellationTokenSource();
        Show(scanning: true);
        ScanMessage.Text = "Starting…";
        try
        {
            var baseCtx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var ctx = _pickedFolders.Count == 0 ? baseCtx : WithRoots(baseCtx, _pickedFolders.ToList());
            // Marshalled explicitly: progress must reach the UI thread even without a synchronization context.
            var progress = new Progress<ScanProgress>(p => Dispatcher.BeginInvoke(() =>
            {
                ScanMessage.Text = p.Message;
                if (p.Fraction is { } f) { ScanBar.IsIndeterminate = false; ScanBar.Value = Math.Clamp(f, 0, 1) * 100; }
            }));
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
            ScanButton.IsEnabled = PickButton.IsEnabled = _spec.Available;
        }
    }

    private static ScanContext WithRoots(ScanContext c, IReadOnlyList<string> roots) => new()
    {
        Locations = c.Locations, Safety = c.Safety, InUse = c.InUse, Settings = c.Settings, Walker = c.Walker, Depth = c.Depth,
        Roots = roots, Preset = c.Preset, RunningProcesses = c.RunningProcesses, System = c.System, Options = c.Options,
    };

    /// <summary>Shows a result (also used by tests to check layout without scanning).</summary>
    internal void Load(ScanResult result)
    {
        _model = new SelectionModel(showSectionHeaders: false);
        _model.Changed += UpdateBar;
        // Moves are always your pick: nothing starts ticked.
        // With the drive rings on screen, the "Your drives" rows would only repeat them (as tickable-looking, "Safe" rows): leave them out.
        var shown = _spec.ShowDriveStrip ? result with { Groups = result.Groups.Where(g => g.Id != DriveMoveIds.GroupDrives).ToList() } : result;
        _model.Load(new[] { new SectionModel(result.FeatureId, _spec.Title, SymbolRegular.ArrowSwap24, shown) },
            (_, _, _) => false,
            (s, g) => g == s.Groups.FirstOrDefault());
        Results.Model = _model;
        Results.OpenInExplorerRequested -= OnOpenInExplorer;
        Results.OpenInExplorerRequested += OnOpenInExplorer;

        bool empty = _model.TotalItems == 0;
        NotesPanel.Children.Clear();
        // An empty tab says its note once, in the empty state below, not in a bar above it as well.
        foreach (var note in empty ? Array.Empty<string>() : result.Notes)
        {
            var bar = new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
            bar.Loaded += (_, _) => NameCloseButton(bar);
            NotesPanel.Children.Add(bar);
        }
        if (_spec.ShowDriveStrip && _spec.Scanner is DriveBalanceScanner balance && balance.LastAdvice is { } advice) ShowDrives(advice);

        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingFound.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Message = result.Notes.Count > 0 ? string.Join(" ", result.Notes) : $"Nothing to show in {_spec.Title} right now.";
        ReadyState.Visibility = Visibility.Collapsed;
        ScanningState.Visibility = Visibility.Collapsed;
        ResultsState.Visibility = Visibility.Visible;
        StickyBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Content = "Scan again";
        SetStep(2);
        UpdateBar();
    }

    private void ShowDrives(BalanceAdvice advice)
    {
        DriveStrip.Children.Clear();
        int i = 0;
        foreach (var d in advice.Drives)
        {
            DriveStrip.Children.Add(new DriveRing
            {
                Root = d.Name,
                DriveLabel = d.MediaText,
                TotalBytes = d.TotalBytes,
                FreeBytes = d.FreeBytes,
                RingSize = 64,
                Orientation = Orientation.Horizontal,
                IsSecondary = i++ > 0,
                Margin = new Thickness(0, 0, 28, 8),
            });
        }
        DriveStrip.Visibility = DriveStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ScanButton.IsEnabled = PickButton.IsEnabled = !scanning && _spec.Available;
        if (scanning) { ReadyState.Visibility = ResultsState.Visibility = StickyBar.Visibility = Visibility.Collapsed; return; }
        bool hasResults = _model is not null;
        ReadyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsState.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        StickyBar.Visibility = hasResults && _model!.TotalItems > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBar()
    {
        if (_model is null) return;
        var actions = _model.CheckedActions();
        int items = _model.CheckedCount;
        long moving = _model.CheckedItems().Where(i => i.IsSelectable).Sum(i => i.Item.SizeBytes);
        SelectedText.Text = items == 0 ? "Nothing selected" : $"Selected {items:N0} item{(items == 1 ? "" : "s")}" + (moving > 0 ? " · " + Sizes.Format(moving) : "");
        SelectedCaption.Text = Caption(actions);
        PrimaryButton.Content = PrimaryText(actions);
        PrimaryButton.IsEnabled = SimulateButton.IsEnabled = actions.Count > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
    }

    internal static string PrimaryText(IReadOnlyList<PlanItem> actions)
    {
        if (actions.Count == 0) return "Nothing selected";
        if (actions.All(a => a.Kind == ActionKind.OpenExternal)) return actions.Count == 1 ? "Open launcher" : "Open launchers";
        if (actions.All(a => a.Kind == ActionKind.RunSystemTool)) return "Stop WSL";
        // Folders moved with a link or by Windows' Location setting: "Move 2 folders" (PlanFacts would call them "files").
        int folders = actions.Count(a => a.Kind is ActionKind.MoveFolderWithJunction or ActionKind.KnownFolderRedirect);
        if (folders > 0) return folders == 1 ? "Move 1 folder" : $"Move {folders:N0} folders";
        return PlanFacts.PrimaryAction(actions);
    }

    private static string Caption(IReadOnlyList<PlanItem> actions)
    {
        if (actions.Any(a => a.Kind == ActionKind.MoveFolderWithJunction)) return "Copied and checked first; a link stays at the old place. Undo from History.";
        if (actions.Any(a => a.Kind == ActionKind.KnownFolderRedirect))
            return actions.Any(a => a.Kind == ActionKind.QuarantinePath)
                ? "Windows' own Location setting copies the folder; once it's checked, the old copy goes to quarantine. Undo from History."
                : "Windows' own Location setting moves the folder. Undo from History.";
        if (actions.Any(a => a.Kind == ActionKind.EditTextFile)) return "The tool's config file is backed up first. Undo from History.";
        if (actions.Any(a => a.Kind == ActionKind.QuarantinePath)) return "Old copies go to quarantine, so you can put them back from History.";
        if (actions.Any(a => a.Kind == ActionKind.OpenExternal)) return "Opens the launcher's own screen. Nothing on your PC changes until you click there.";
        return "";
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => RunAsync(simulate: false).Forget(Host, "That didn't work");

    private void OnSimulate(object sender, RoutedEventArgs e) => RunAsync(simulate: true).Forget(Host, "That didn't work");

    private async Task RunAsync(bool simulate)
    {
        if (_model is null) return;
        IReadOnlyList<ActionPlan> plans;
        try
        {
            plans = _model.BuildPlans(parentRunId: null).Select(p => p with { Items = ConfigEditMerger.Merge(p.Items) }).ToList();
        }
        catch (ConfigEditMerger.FileChangedException ex)
        {
            Host?.ShowMessage("Scan again first", ex.Message);
            return;
        }
        if (plans.Count == 0) return;

        SetStep(3);
        try
        {
            var options = new FlowOptions { Title = _spec.Title, ShowResult = true };
            bool ran = false;
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
            // Show what the drives look like now (only looks, changes nothing).
            if (ran) await ScanAsync();
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
        if (_scanCts is null) SetStep(_model is null ? 1 : 2);
    }
}
