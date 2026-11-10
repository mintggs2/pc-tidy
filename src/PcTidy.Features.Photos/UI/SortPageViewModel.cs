using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.UI;
using PcTidy.Features.Photos.Shared;
using PcTidy.UI.Flow;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Photos.UI;

/// <summary>What one scan produced for the page: the change list, the before/after tree and what stays put.</summary>
public sealed record PagePreview(
    IReadOnlyList<ChangeGroup> Groups,
    IReadOnlyList<TreeRow> Before,
    IReadOnlyList<SkipRow> Skipped,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> NeedsPermission,
    string FoundText);

/// <summary>
/// The standard Scan → Preview → Confirm → Done flow shared by the Photos and Desktop tidy pages: Quick/Deep switch,
/// step bar, change list with ticks, OneDrive "ask first" bar, sticky action bar counted with <see cref="PlanCounts"/>
/// (the same numbers the confirm dialog shows), confirm through <see cref="IFlowHost"/>, and the engine's run undo.
/// </summary>
public abstract partial class SortPageViewModel : ObservableObject
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _scanCts;
    private bool _refreshQueued;
    private PagePreview? _preview;

    protected SortPageViewModel(IServiceProvider services)
    {
        Services = services;
        Locations = services.Get<IKnownLocations>();
        Safety = services.Get<ISafetyGuard>();
        AppSettings = services.Get<AppSettings>();
        Flow = services.Get<IFlowHost>();
        _isDeep = AppSettings.DefaultDepth == ScanDepth.Deep;
    }

    protected IServiceProvider Services { get; }
    protected IKnownLocations Locations { get; }
    protected ISafetyGuard Safety { get; }
    protected AppSettings AppSettings { get; }
    protected IFlowHost Flow { get; }
    private IShellFlowHost? Shell => Services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Flow as IShellFlowHost;

    /// <summary>The module id the run is recorded under (History).</summary>
    protected abstract string FeatureId { get; }
    /// <summary>Run title for the confirm dialog and History ("Sort photos").</summary>
    protected abstract string RunTitle { get; }
    /// <summary>Read-only scan for the page.</summary>
    protected abstract Task<PagePreview> PlanAsync(ScanDepth depth, IProgress<ScanProgress> progress, CancellationToken ct);
    /// <summary>The before → after tree for the rows ticked right now.</summary>
    protected abstract IReadOnlyList<TreeRow> AfterTree(IReadOnlyList<ChangeRow> ticked);

    public abstract string Title { get; }
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isDeep;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private int _viewIndex;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private IReadOnlyList<object> _rows = Array.Empty<object>();
    [ObservableProperty] private IReadOnlyList<TreeRow> _beforeRows = Array.Empty<TreeRow>();
    [ObservableProperty] private IReadOnlyList<TreeRow> _afterRows = Array.Empty<TreeRow>();
    [ObservableProperty] private IReadOnlyList<SkipRow> _skippedRows = Array.Empty<SkipRow>();
    [ObservableProperty] private string _selectedText = "Scan to see what would change";
    [ObservableProperty] private string _selectedCaption = "Nothing changes until you confirm.";
    [ObservableProperty] private string _primaryText = "Nothing selected";
    [ObservableProperty] private SymbolRegular _primaryIcon = SymbolRegular.FolderArrowRight24;
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string? _permissionMessage;
    /// <summary>The page's options row is open (before a scan) or folded to one summary line (once there's a list to look at).</summary>
    [ObservableProperty] private bool _optionsOpen = true;
    /// <summary>The OneDrive bar's explanation is shown; closing it keeps the title and the Allow button.</summary>
    [ObservableProperty] private bool _permissionDetailsOpen = true;
    public ObservableCollection<string> Notes { get; } = new();

    /// <summary>OneDrive folders in the last scan the user hasn't allowed yet.</summary>
    public IReadOnlyList<string> PermissionFolders => _preview?.NeedsPermission ?? Array.Empty<string>();
    public IReadOnlyList<ChangeGroup> Groups => _preview?.Groups ?? Array.Empty<ChangeGroup>();
    public IEnumerable<ChangeRow> AllRows => Groups.SelectMany(g => g.Rows);
    public IReadOnlyList<RunRecord> LastRuns { get; private set; } = Array.Empty<RunRecord>();
    public bool CanUndo => LastRuns.Count > 0 && Shell is not null;

    /// <summary>The options in one line, shown while the options row is folded ("Move old clutter · 30 days · shortcuts stay").</summary>
    public virtual string OptionsSummary => "";

    [RelayCommand]
    private void ToggleOptions() => OptionsOpen = !OptionsOpen;

    [RelayCommand]
    private void HidePermissionDetails() => PermissionDetailsOpen = false;

    /// <summary>The shared Quick | Deep switch (0 = Quick, 1 = Deep).</summary>
    public int DepthIndex
    {
        get => IsDeep ? 1 : 0;
        set => IsDeep = value == 1;
    }

    partial void OnIsDeepChanged(bool value) { OnPropertyChanged(nameof(DepthIndex)); ClearPreview(); }
    partial void OnStepChanged(int value) => OnPropertyChanged(nameof(StepCaptions));
    partial void OnIsScanningChanged(bool value) => NotifyCommands();

    /// <summary>Captions under the shared step bar ("a|b|c|d").</summary>
    public string StepCaptions => string.Join("|",
        Step >= 2 && _preview is not null ? _preview.FoundText : "Looks only",
        "Pick what changes",
        Step >= 4 ? "Done" : "Nothing changed yet",
        "+ Undo");

    // ------------------------------------------------------------------ scan

    [RelayCommand]
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        // The old list goes away first: its thumbnails would otherwise be read while the new scan checks which files
        // are in use, and PC Tidy's own read would look like another app holding the file.
        ClearPreview();
        IsScanning = true;
        IsDone = false;
        ProgressText = "Starting…";
        try
        {
            var progress = new Progress<ScanProgress>(p => ProgressText = p.Message);
            Show(await PlanAsync(IsDeep ? ScanDepth.Deep : ScanDepth.Quick, progress, cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Flow.ShowMessage("Couldn't scan", $"PC Tidy couldn't finish looking: {ex.Message} Nothing was changed.", isError: true);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            IsScanning = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Settings changed: the old preview no longer says what would happen.</summary>
    protected void ClearPreview()
    {
        _preview = null;
        HasPreview = false;
        Step = 1;
        Rows = Array.Empty<object>();
        BeforeRows = AfterRows = Array.Empty<TreeRow>();
        SkippedRows = Array.Empty<SkipRow>();
        Notes.Clear();
        PermissionMessage = null;
        SelectedText = "Scan to see what would change";
        SelectedCaption = "Nothing changes until you confirm.";
        PrimaryText = "Nothing selected";
        OnPropertyChanged(nameof(PermissionFolders));
        NotifyCommands();
        OnPropertyChanged(nameof(OptionsSummary));
    }

    private void Show(PagePreview preview)
    {
        ClearPreview();
        _preview = preview;
        var flat = new List<object>();
        foreach (var g in preview.Groups) { flat.Add(g); flat.AddRange(g.Rows); }
        Rows = flat;
        BeforeRows = preview.Before;
        SkippedRows = preview.Skipped;
        foreach (var n in preview.Notes) Notes.Add(n);
        PermissionMessage = preview.NeedsPermission.Count == 0 ? null : PermissionText(preview.NeedsPermission);
        OnPropertyChanged(nameof(PermissionFolders));
        // Fold the options so the list gets the room; one click opens them again.
        OptionsOpen = false;
        HasPreview = true;
        Step = 2;
        OnPropertyChanged(nameof(StepCaptions));
        UpdateTotals();
    }

    /// <summary>The plain-English "ask first" explanation for this page.</summary>
    protected abstract string PermissionText(IReadOnlyList<string> folders);

    /// <summary>Rows call this when ticked; many ticks at once (a group box) are folded into one refresh.</summary>
    protected virtual void OnRowChanged(ChangeRow row)
    {
        if (_refreshQueued) return;
        if (_ui is null) { UpdateTotals(); return; }
        _refreshQueued = true;
        _ui.Post(_ => { _refreshQueued = false; UpdateTotals(); }, null);
    }

    private void UpdateTotals()
    {
        foreach (var g in Groups) g.Refresh();
        var ticked = AllRows.Where(r => r.IsChecked && r.IsSelectable).ToList();
        var counts = PlanCounts.Of(PlanFolders.Order(ticked.SelectMany(r => r.Item.Actions)));
        SelectedText = counts.Total == 0 ? "Nothing selected" : "Selected: " + PlanFacts.TotalsLine(counts);
        int stay = SkippedRows.Count;
        SelectedCaption = $"{stay:N0} stay put · quarantined files can be restored · undo the whole run from History";
        PrimaryText = PlanFacts.PrimaryAction(counts);
        PrimaryIcon = PlanFacts.PrimaryIcon(counts);
        AfterRows = AfterTree(ticked);
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ confirm / run / undo

    public ActionPlan BuildPlan()
    {
        var picked = AllRows.Where(r => r.IsChecked && r.IsSelectable).SelectMany(r => r.Item.Actions);
        return new ActionPlan { FeatureId = FeatureId, Title = RunTitle, Items = PlanFolders.Order(picked) };
    }

    private bool CanRun() => HasPreview && !IsScanning && AllRows.Any(r => r.IsChecked && r.IsSelectable);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var plan = BuildPlan();
        if (plan.Items.Count == 0) return;
        Step = 3;
        RunRecord? run;
        try
        {
            run = await Flow.ReviewAndRunAsync(plan, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Flow.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
            run = null;
        }
        if (run is null) { Step = 2; return; }

        LastRuns = new[] { run };
        var c = PlanCounts.Of(run.Steps.Where(s => s.Status == StepStatus.Done).Select(s => s.Item));
        var skipped = run.Steps.Count(s => s.Status is StepStatus.Skipped or StepStatus.Failed && PlanFacts.Categorize(s.Item) != PlanCategory.Housekeeping);
        var parts = new List<string>();
        if (c.Moves > 0) parts.Add($"moved {c.Moves:N0} file{PlanFacts.S(c.Moves)}");
        if (c.Quarantine > 0) parts.Add($"quarantined {c.Quarantine:N0} (restorable)");
        DoneText = (parts.Count == 0 ? "Nothing was changed." : "Done: " + string.Join(" and ", parts) + ".")
                   + (skipped > 0 ? $" {skipped:N0} skipped (they changed or were in use; History says why)." : "")
                   + " Undo puts everything back.";
        ClearPreview();
        IsDone = true;
        Step = 4;
        OnPropertyChanged(nameof(CanUndo));
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SimulateAsync() => await Flow.SimulateAsync(BuildPlan(), CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (Shell is not { } shell) return;
        if (!await shell.UndoRunsAsync(LastRuns, CancellationToken.None)) return;
        LastRuns = Array.Empty<RunRecord>();
        DoneText = "Put back. Everything is where it was.";
        Step = 1;
        OnPropertyChanged(nameof(CanUndo));
        NotifyCommands();
    }

    [RelayCommand]
    private void OpenHistory() => Shell?.NavigateTo("history");

    // ------------------------------------------------------------------ OneDrive permission

    /// <summary>The user's "yes, sort it" for the OneDrive folders in this scan; then a fresh scan.</summary>
    [RelayCommand]
    private async Task AllowAsync()
    {
        foreach (var folder in PermissionFolders) CloudFolders.Allow(folder, AppSettings);
        OnPermissionGranted();
        await ScanAsync();
    }

    /// <summary>Called after the user allowed the OneDrive folders (pages refresh their "asks first" labels).</summary>
    protected virtual void OnPermissionGranted() { }

    [RelayCommand]
    private void OpenSettings() => Shell?.NavigateTo("settings");
}
