using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Games.Library;
using PcTidy.Features.Games.Optimize;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Games.UI;

/// <summary>What a tab shows and how.</summary>
internal sealed record TabSpec(string Title, string Blurb, IFeatureScanner Scanner, bool Available, string UnavailableMessage,
    bool ShowPreview = false, bool ShowSort = false, bool AutoScan = false);

/// <summary>
/// One Games tab: Scan → pick → confirm, on the shared results list. Read-only until the user confirms; the confirm,
/// run, Done card and undo all belong to the shell's flow host. Several ticked edits of one config file are merged
/// into a single edit just before running (<see cref="EditMerger"/>).
/// </summary>
public partial class GamesTabView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly TabSpec _spec;
    private readonly ThumbnailLoader _thumbnails = new();
    private SelectionModel? _model;
    private CancellationTokenSource? _scanCts;
    private bool _scanned;

    internal GamesTabView(IServiceProvider services, TabSpec spec)
    {
        InitializeComponent();
        SetStep(1);
        _services = services;
        _spec = spec;
        TabTitle.Text = spec.Title;
        TabBlurb.Text = spec.Blurb;
        AutomationProperties.SetName(this, spec.Title);
        SortPicker.Visibility = spec.ShowSort ? Visibility.Visible : Visibility.Collapsed;
        SortPicker.SelectionChanged += (_, _) => Resort();

        if (spec.Available)
        {
            ReadyEmpty.Title = $"Scan to see your {spec.Title.ToLowerInvariant()}";
            ReadyEmpty.Message = "Scanning only looks. You get a plain-English list with reasons and sources, and nothing changes until you confirm.";
        }
        else
        {
            ReadyEmpty.Title = "Not available on this PC";
            ReadyEmpty.Message = spec.UnavailableMessage;
            ReadyEmpty.ActionText = null;
            ScanButton.IsEnabled = false;
        }

        // The shared results list has no selection event of its own: listen to its list for the clip preview.
        if (spec.ShowPreview && Results.FindName("List") is Selector list)
            list.SelectionChanged += (_, _) => ShowPreview(list.SelectedItem as ItemRow);
    }

    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    /// <summary>Quick tabs (the library) scan the first time they are shown: reading launcher lists only, never changing anything.</summary>
    public void EnsureScanned()
    {
        if (!_spec.AutoScan || _scanned || !_spec.Available) return;
        // Only once the tab is really on screen (its dispatcher is running), never from a constructor.
        if (IsLoaded) _ = ScanAsync();
        else
        {
            void OnLoaded(object s, RoutedEventArgs e) { Loaded -= OnLoaded; EnsureScanned(); }
            Loaded += OnLoaded;
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    public async Task ScanAsync()
    {
        if (!_spec.Available || _scanCts is not null) return;
        _scanned = true;
        _scanCts = new CancellationTokenSource();
        Show(scanning: true);
        ScanMessage.Text = "Starting…";
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
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

    private void Resort()
    {
        if (_spec.Scanner is LibraryScanner library && library.Resort(SortPicker.SelectedIndex == 1 ? LibrarySort.LastPlayed : LibrarySort.Size) is { } r)
            Load(r);
    }

    private void Load(ScanResult result)
    {
        _model = new SelectionModel(showSectionHeaders: false);
        _model.Changed += UpdateBar;
        _model.Load(new[] { new SectionModel(result.FeatureId, _spec.Title, SymbolRegular.Games24, result) },
            (_, _, i) => OptimizePresets.IsTickableByDefault(i.Item),
            (s, g) => g == s.Groups.FirstOrDefault());
        Results.Model = _model;
        Results.OpenInExplorerRequested -= OnOpenInExplorer;
        Results.OpenInExplorerRequested += OnOpenInExplorer;

        NotesPanel.Children.Clear();
        foreach (var note in result.Notes)
            NotesPanel.Children.Add(Note(note));
        // Rows held back because they're in a OneDrive folder: one bar with the way to allow it, like Photos (not a dead end on every row).
        if (Shell is { } shell && result.Groups.SelectMany(g => g.Items).Any(i => i.DisabledReason?.Contains("Allow this folder in Settings", StringComparison.Ordinal) == true))
        {
            var open = new Wpf.Ui.Controls.Button { Content = "Open Settings", Appearance = ControlAppearance.Secondary, Icon = new SymbolIcon(SymbolRegular.Settings24) };
            AutomationProperties.SetName(open, "Open Settings to allow this OneDrive folder");
            open.Click += (_, _) => shell.NavigateTo("settings");
            NotesPanel.Children.Add(PcTidy.UI.Controls.InfoBars.WithActions(
                Note("Some clips are in OneDrive, so PC Tidy asks first. Allow the folder in Settings, under OneDrive folders PC Tidy may organize, then scan again."), open));
        }

        bool empty = _model.TotalItems == 0;
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingFound.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Message = $"Nothing to show in {_spec.Title} right now.";
        PreviewStrip.Visibility = Visibility.Collapsed;
        ReadyState.Visibility = Visibility.Collapsed;
        ScanningState.Visibility = Visibility.Collapsed;
        ResultsState.Visibility = Visibility.Visible;
        StickyBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Content = "Scan again";
        SetStep(2);
        UpdateBar();
    }

    private void OnOpenInExplorer(string path) => Host?.OpenInExplorer(path);

    private static InfoBar Note(string message)
    {
        var bar = new InfoBar { Message = message, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) };
        // WPF-UI's close button has no name of its own: give it one for screen readers.
        bar.Loaded += (_, _) => NameCloseButtons(bar);
        return bar;
    }

    internal static void NameCloseButtons(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is ButtonBase b && string.IsNullOrEmpty(AutomationProperties.GetName(b))) AutomationProperties.SetName(b, "Close this note");
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
        long bytes = actions.Sum(a => a.ExpectedSize);
        SelectedText.Text = items == 0 ? "Nothing selected" : $"Selected {items:N0} item{(items == 1 ? "" : "s")}" + (bytes > 0 ? " · " + Sizes.Format(bytes) : "");
        SelectedCaption.Text = actions.Any(a => a.Kind == ActionKind.QuarantinePath)
            ? "Files go to quarantine, so you can put them back from History"
            : actions.Any(a => a.Kind == ActionKind.EditTextFile)
                ? "Each file is backed up first; undo from History. The game must be closed."
                : actions.Any(a => a.Kind == ActionKind.OpenExternal) ? "Opens the launcher's own screen. Nothing on your PC changes until you click there." : "";
        PrimaryButton.Content = PlanFacts.PrimaryAction(actions);
        PrimaryButton.IsEnabled = SimulateButton.IsEnabled = actions.Count > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: false);

    private void OnSimulate(object sender, RoutedEventArgs e) => _ = RunAsync(simulate: true);

    private async Task RunAsync(bool simulate)
    {
        if (_model is null) return;
        IReadOnlyList<ActionPlan> plans;
        try
        {
            plans = _model.BuildPlans(parentRunId: null).Select(p => p with { Items = EditMerger.Merge(p.Items) }).ToList();
        }
        catch (EditMerger.FileChangedException ex)
        {
            Host?.ShowMessage("Scan again first", ex.Message);
            return;
        }
        if (plans.Count == 0) return;

        SetStep(3);
        try
        {
            var options = new FlowOptions { Title = _spec.Title, ShowResult = true, PrimaryVerb = PlanFacts.PrimaryAction(plans.SelectMany(p => p.Items).ToList()) };
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
        if (_scanCts is null) SetStep(_model is null ? 1 : 2);
    }

    // ---------------- clip preview ----------------

    private async void ShowPreview(ItemRow? row)
    {
        if (row?.Item.PreviewPath is not { } path)
        {
            PreviewStrip.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewStrip.Visibility = Visibility.Visible;
        PreviewTitle.Text = row.Item.Title;
        PreviewCaption.Text = row.Item.Detail ?? path;
        PreviewImage.Source = null;
        var image = await _thumbnails.LoadAsync(path, 256);
        // The user may have moved on while the thumbnail loaded.
        if (Results.FindName("List") is Selector list && ReferenceEquals(list.SelectedItem, row)) PreviewImage.Source = image;
    }
}
