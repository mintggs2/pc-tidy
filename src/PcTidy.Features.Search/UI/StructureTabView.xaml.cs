using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Structure;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Search.UI;

/// <summary>A row of the proposed-tree preview.</summary>
public sealed class TreeNode(string text, SymbolRegular icon, string note = "")
{
    public string Text { get; } = text;
    public string Note { get; } = note;
    public SymbolRegular Icon { get; } = icon;
    public List<TreeNode> Children { get; } = new();
}

/// <summary>
/// "Suggest a folder structure": pick a preset, see the proposed tree per drive, tick the folders that fit, confirm.
/// Scanning only reads names and sizes; the confirm dialog, run, Done card and Undo belong to the shell's flow host.
/// </summary>
public partial class StructureTabView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly AppSettings _settings;
    private SelectionModel? _model;
    private CancellationTokenSource? _scanCts;
    /// <summary>Rows the user could tick in the last result (the rest are shown with their reason).</summary>
    private int _offered;

    public StructureTabView(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _settings = services.Get<AppSettings>();
        Presets.SelectedIndex = (int)SearchSettings.Load(_settings).Preset;
        ShowPresetSummary();
        Presets.SelectionChanged += (_, _) =>
        {
            var state = SearchSettings.Load(_settings);
            state.Preset = SelectedPreset;
            state.Store(_settings);
            ShowPresetSummary();
            if (_model is not null) ScanAsync().Forget(Host, "That didn't finish");
        };
    }

    private StructurePreset SelectedPreset => (StructurePreset)Math.Clamp(Presets.SelectedIndex, 0, StructurePresets.All.Count - 1);
    private IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Host as IShellFlowHost;

    private void ShowPresetSummary() => PresetSummary.Text = StructurePresets.Get(SelectedPreset).Summary;

    private void OnScanClick(object sender, RoutedEventArgs e) => ScanAsync().Forget(Host, "That didn't finish");

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        Show(scanning: true);
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var preset = SelectedPreset;
            var planner = new StructurePlanner(ctx.Locations, ctx.Safety, ctx.Walker);
            var progress = new Progress<ScanProgress>(p => ScanMessage.Text = p.Message);
            var started = DateTime.UtcNow;
            var proposals = await Task.Run(() => planner.ProposeAll(preset, null, progress, cts.Token), cts.Token);
            var result = planner.ToScanResult(preset, proposals, DateTime.UtcNow - started);
            if (cts.IsCancellationRequested) return;
            Load(result, proposals);
        }
        catch (OperationCanceledException) { Show(scanning: false); }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't finish", "Something went wrong while looking: " + ex.Message + " Nothing was changed.", isError: true);
            Show(scanning: false);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
        }
    }

    internal void Load(ScanResult result, IReadOnlyList<StructureProposal> proposals)
    {
        _model = new SelectionModel(showSectionHeaders: false);
        _model.Changed += UpdateBar;
        // Reorganizing is always your pick: nothing starts ticked.
        _model.Load(new[] { new SectionModel(result.FeatureId, "Folder structure", SymbolRegular.FolderArrowRight24, result) },
            (_, _, _) => false, (_, _) => true);
        Results.Model = _model;
        Results.OpenInExplorerRequested -= OnOpenInExplorer;
        Results.OpenInExplorerRequested += OnOpenInExplorer;
        Tree.ItemsSource = BuildTree(proposals);

        NotesPanel.Children.Clear();
        foreach (var note in result.Notes)
            NotesPanel.Children.Add(new InfoBar { Message = note, IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 8) });

        bool empty = _model.TotalItems == 0;
        _offered = result.Groups.SelectMany(g => g.Items).Count(i => i.DisabledReason is null);
        // Every row is "not offered": say it in one line instead of showing an empty selection bar.
        if (!empty && _offered == 0)
            NotesPanel.Children.Add(new InfoBar
            {
                Title = "Nothing to move here",
                Message = "No folder can move for this preset. Each one below says what keeps it where it is.",
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Margin = new Thickness(0, 0, 0, 8),
            });
        Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingFound.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Message = "No top-level folders need a new place for this preset.";
        // The header button only appears once there are results (the empty state has the big one).
        ScanButton.Visibility = Visibility.Visible;
        Show(scanning: false);
        UpdateBar();
    }

    /// <summary>Root → tree folders (with "← Photos" for each folder that would land there).</summary>
    private static List<TreeNode> BuildTree(IReadOnlyList<StructureProposal> proposals)
    {
        var roots = new List<TreeNode>();
        foreach (var p in proposals)
        {
            var root = new TreeNode(p.Root.Label, SymbolRegular.HardDrive20);
            var nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
            TreeNode NodeFor(string relative)
            {
                if (nodes.TryGetValue(relative, out var n)) return n;
                var parentRel = Path.GetDirectoryName(relative);
                var parent = string.IsNullOrEmpty(parentRel) ? root : NodeFor(parentRel);
                bool exists = Directory.Exists(Path.Combine(p.Root.Path, relative));
                n = new TreeNode(Path.GetFileName(relative), SymbolRegular.Folder20, exists ? "" : "new");
                parent.Children.Add(n);
                nodes[relative] = n;
                return n;
            }
            foreach (var f in p.Preset.Tree) NodeFor(f.RelativePath);
            foreach (var m in p.Moves.Where(m => m.Destination is not null && m.DisabledReason is null))
            {
                var rel = Path.GetRelativePath(p.Root.Path, m.Destination!);
                var parentRel = m.BecomesCategory ? rel : Path.GetDirectoryName(rel) ?? "";
                var target = parentRel.Length == 0 ? root : NodeFor(parentRel);
                target.Children.Add(new TreeNode(m.Folder.Name, SymbolRegular.ArrowEnter20, m.BecomesCategory ? "becomes this folder" : "moves here"));
            }
            roots.Add(root);
        }
        return roots;
    }

    private void OnOpenInExplorer(string path) => Host?.OpenInExplorer(path);

    private void Show(bool scanning)
    {
        ScanningState.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !scanning;
        bool hasResults = _model is not null && !scanning;
        ReadyState.Visibility = !scanning && _model is null ? Visibility.Visible : Visibility.Collapsed;
        ResultsState.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        StickyBar.Visibility = hasResults && _offered > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBar()
    {
        if (_model is null) return;
        var actions = _model.CheckedActions();
        int folders = actions.Count(a => a.Kind == ActionKind.MovePath);
        int created = actions.Where(a => a.Kind == ActionKind.CreateDirectory).Select(a => a.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SelectedText.Text = actions.Count == 0 ? "Nothing selected"
            : folders > 0 ? $"Move {folders:N0} folder{(folders == 1 ? "" : "s")}" + (created > 0 ? $" · {created:N0} new" : "")
            : $"Create {created:N0} folder{(created == 1 ? "" : "s")}";
        PrimaryButton.Content = actions.Count == 0 ? "Nothing selected" : folders > 0 ? (folders == 1 ? "Move 1 folder" : $"Move {folders:N0} folders") : "Create folders";
        PrimaryButton.IsEnabled = SimulateButton.IsEnabled = actions.Count > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryButton.Content as string ?? "Run");
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => RunAsync(simulate: false).Forget(Host, "That didn't work");

    private void OnSimulate(object sender, RoutedEventArgs e) => RunAsync(simulate: true).Forget(Host, "That didn't work");

    private async Task RunAsync(bool simulate)
    {
        if (_model is null) return;
        var plans = _model.BuildPlans(parentRunId: null);
        if (plans.Count == 0) return;
        try
        {
            bool ran = false;
            if (Shell is { } shell)
            {
                var options = new FlowOptions { Title = "Folder structure", ShowResult = true };
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
            if (ran) await ScanAsync(); // show the new state (only looks)
        }
        catch (Exception ex)
        {
            Host?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
    }
}
