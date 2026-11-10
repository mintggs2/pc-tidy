using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Developer.UI;

/// <summary>What a tab shows and how.</summary>
/// <param name="Options">Scan options added to every scan of this tab (test/demo overrides; saved settings are read by the scanners).</param>
internal sealed record TabSpec(string Title, string Blurb, IFeatureScanner Scanner, bool Available, string UnavailableMessage,
    Func<IReadOnlyDictionary<string, object>> Options);

/// <summary>
/// One Developer tab: Scan → pick → confirm, on the shared results list. Read-only until the user confirms; the
/// confirm, run, Done card and undo all belong to the shell's flow host.
/// </summary>
public partial class DevTabView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly TabSpec _spec;
    private SelectionModel? _model;
    private CancellationTokenSource? _scanCts;

    internal DevTabView(IServiceProvider services, TabSpec spec, FrameworkElement? options = null)
    {
        InitializeComponent();
        _services = services;
        _spec = spec;
        TabTitle.Text = spec.Title;
        TabBlurb.Text = spec.Blurb;
        AutomationProperties.SetName(this, spec.Title);
        OptionsHost.Content = options;
        OptionsHost.Visibility = options is null ? Visibility.Collapsed : Visibility.Visible;

        if (spec.Available)
        {
            ReadyEmpty.Title = $"Scan to see {spec.Title.ToLowerInvariant()}";
            ReadyEmpty.Message = "Scanning only looks. You get a plain-English list with sizes and reasons, and nothing changes until you confirm.";
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

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    /// <summary>The context for this tab's scan: the shell's usual context plus this tab's roots and options.</summary>
    internal ScanContext CreateContext()
    {
        var c = ScanRunner.CreateContext(_services, ScanDepth.Quick);
        return new ScanContext
        {
            Locations = c.Locations, Safety = c.Safety, InUse = c.InUse, Settings = c.Settings, Walker = c.Walker,
            Depth = c.Depth, System = c.System, RunningProcesses = c.RunningProcesses,
            Options = _spec.Options(),
        };
    }

    public async Task ScanAsync()
    {
        if (!_spec.Available || _scanCts is not null) return;
        _scanCts = new CancellationTokenSource();
        Show(scanning: true);
        ScanMessage.Text = "Starting…";
        try
        {
            var ctx = CreateContext();
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
            // Re-enabled here, not only in Show(): a finished scan goes through Load(), which never touched the button.
            ScanButton.IsEnabled = _spec.Available;
        }
    }

    private void Load(ScanResult result)
    {
        _model = new SelectionModel(showSectionHeaders: false);
        _model.Changed += UpdateBar;
        _model.Load(new[] { new SectionModel(result.FeatureId, _spec.Title, SymbolRegular.Code24, result) },
            (_, _, i) => OptimizePresets.IsTickableByDefault(i.Item),
            (s, g) => g == s.Groups.FirstOrDefault());
        Results.Model = _model;
        Results.OpenInExplorerRequested -= OnOpenInExplorer;
        Results.OpenInExplorerRequested += OnOpenInExplorer;

        NotesPanel.Children.Clear();
        foreach (var note in result.Notes)
        {
            var bar = new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
            bar.Loaded += (_, _) => NameCloseButton(bar);
            NotesPanel.Children.Add(bar);
        }

        bool empty = _model.TotalItems == 0;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingFound.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Message = $"Nothing to show in {_spec.Title} right now.";
        ReadyState.Visibility = Visibility.Collapsed;
        ScanningState.Visibility = Visibility.Collapsed;
        ResultsState.Visibility = Visibility.Visible;
        StickyBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Content = "Scan again";
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

    private void Show(bool scanning)
    {
        ScanningState.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !scanning && _spec.Available;
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
        long bytes = actions.Where(a => a.Kind is ActionKind.QuarantinePath or ActionKind.ArchiveToZip or ActionKind.RunSystemTool).Sum(a => a.ExpectedSize);
        SelectedText.Text = items == 0 ? "Nothing selected" : $"Selected {items:N0} item{(items == 1 ? "" : "s")}" + (bytes > 0 ? " · " + Sizes.Format(bytes) : "");
        SelectedCaption.Text = Caption(actions);
        PrimaryButton.Content = PlanFacts.PrimaryAction(actions);
        PrimaryButton.IsEnabled = SimulateButton.IsEnabled = actions.Count > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
    }

    internal static string Caption(IReadOnlyList<PlanItem> actions)
    {
        if (actions.Any(a => a.Kind == ActionKind.ArchiveToZip)) return "Each zip is checked before the original goes to quarantine; undo from History";
        if (actions.Any(a => a.Kind == ActionKind.ExtractZip)) return "Unzips back to where the project was; the zip is kept";
        if (actions.Any(a => !a.Reversible)) return "Tools clean these themselves, which can't be undone";
        if (actions.Any(a => a.Kind == ActionKind.QuarantinePath)) return "Folders go to quarantine, so you can put them back from History";
        return "";
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: false);

    private void OnSimulate(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: true);

    private async Task RunAsync(bool simulate)
    {
        if (_model is null) return;
        var plans = _model.BuildPlans(parentRunId: null).Select(p => p with { Items = Plans.DropRepeatedShutdowns(p.Items) }).ToList();
        if (plans.Count == 0) return;
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
            // Show what's left after a real run (only looks, changes nothing).
            if (ran) await ScanAsync();
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
    }
}
