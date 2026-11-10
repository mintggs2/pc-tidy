using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Organize.Takeout;
using PcTidy.Features.Organize.Triage;

namespace PcTidy.Features.Organize.UI;

/// <summary>Result of "Test this rule": what it would catch. Nothing is moved.</summary>
public sealed record RuleTestResult(IReadOnlyList<string> Matches, int CaughtByEarlierRule, IReadOnlyList<string> Notes);

/// <summary>The Organize page: folders, rules, scan → before/after preview → review and run (through the shell).</summary>
public sealed partial class OrganizeViewModel : ObservableObject
{
    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _safety;
    private readonly AppSettings _appSettings;
    private readonly IFlowHost _flow;
    private readonly OrganizePlanner _planner;
    private readonly IFileWalker _walker;
    private CancellationTokenSource? _scanCts;

    public OrganizeViewModel(IServiceProvider services, OrganizeModule module)
    {
        _locations = services.Get<IKnownLocations>();
        _safety = services.Get<ISafetyGuard>();
        _appSettings = services.Get<AppSettings>();
        _flow = services.Get<IFlowHost>();
        _walker = services.Get<IFileWalker>();
        _planner = new OrganizePlanner(_safety, services.Get<IInUseChecker>(), _walker, _appSettings);
        Settings = OrganizeSettings.Load(_appSettings);
        _isDeep = _appSettings.DefaultDepth == ScanDepth.Deep;
        _useDateFolders = Settings.UseDateFolders;
        _moveWholeFolders = Settings.MoveWholeFolders;

        BuildFolders();
        _selectedFolder = Folders.FirstOrDefault(f => !f.IsPicker);
        _title = _selectedFolder is null ? "Organize" : $"Organize {_selectedFolder.Label}";
        foreach (var r in Settings.Rules) Rules.Add(new RuleRowViewModel(r, SaveSettings));
        Rename = new RenameViewModel(_flow, () => SelectedFolder?.Path);
        Watch = new WatchViewModel(module.GetWatchService(services), _flow, _planner, () => Settings, SaveSettings, _locations);
        Takeout = new TakeoutViewModel(_flow, new TakeoutPlanner(_safety, services.Get<IInUseChecker>(), _walker), _locations, _safety, _appSettings);
    }

    public OrganizeSettings Settings { get; }
    public RenameViewModel Rename { get; }
    public WatchViewModel Watch { get; }
    public TakeoutViewModel Takeout { get; }

    public ObservableCollection<FolderOption> Folders { get; } = new();
    public ObservableCollection<RuleRowViewModel> Rules { get; } = new();
    public ObservableCollection<TreeRow> BeforeRows { get; } = new();
    public ObservableCollection<TreeRow> AfterRows { get; } = new();
    /// <summary>Replaced as a whole (not item by item) so big scans don't raise thousands of change events.</summary>
    [ObservableProperty] private IReadOnlyList<SkipRow> _skippedRows = Array.Empty<SkipRow>();
    public ObservableCollection<ActionGroupViewModel> Groups { get; } = new();
    /// <summary>Group headers and rows in one flat list, so the detailed preview virtualizes even with 100k files.</summary>
    [ObservableProperty] private IReadOnlyList<object> _detailRows = Array.Empty<object>();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private FolderOption? _selectedFolder;
    [ObservableProperty] private bool _isDeep;
    [ObservableProperty] private bool _useDateFolders;
    [ObservableProperty] private bool _moveWholeFolders;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string _title = "Organize";
    [ObservableProperty] private string _subtitle = "Rules run top to bottom, first match wins. Every move can be put back.";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "Scan to see what would move";
    [ObservableProperty] private string _summaryDetail = "Nothing moves until you confirm.";
    [ObservableProperty] private string _skippedTitle = "";
    [ObservableProperty] private string _moveButtonText = "Move files";
    [ObservableProperty] private string? _folderNote;
    /// <summary>The open tab: 0 Sort files, 1 Rename, 2 Watch folders, 3 Google Takeout.</summary>
    [ObservableProperty] private int _selectedTab;

    public const int SortTab = 0, RenameTab = 1, WatchTab = 2, TakeoutTab = 3;

    /// <summary>The page title for the open tab (Takeout and Watch aren't about the folder picked at the top).</summary>
    public string HeaderTitle => SelectedTab switch
    {
        RenameTab => SelectedFolder is null ? "Rename files" : $"Rename files in {SelectedFolder.Label}",
        WatchTab => "Watch folders",
        TakeoutTab => "Sort a Google Takeout export",
        _ => Title,
    };

    public string HeaderSubtitle => SelectedTab switch
    {
        RenameTab => "See every new name before anything changes. Every rename can be put back.",
        WatchTab => "New files are sorted by the rules you switched to automatic. Every batch can be put back.",
        TakeoutTab => "Photos go into year and month folders by the date Google recorded. Every move can be put back.",
        _ => Subtitle,
    };

    /// <summary>The folder picker only applies to Sort files and Rename.</summary>
    public bool ShowFolderPicker => SelectedTab is SortTab or RenameTab;
    /// <summary>Quick/Deep, Scan and One by one belong to the Sort files tab.</summary>
    public bool ShowSortTools => SelectedTab == SortTab;

    partial void OnSelectedTabChanged(int value) => RaiseHeader();
    partial void OnTitleChanged(string value) => RaiseHeader();
    partial void OnSubtitleChanged(string value) => RaiseHeader();

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(ShowFolderPicker));
        OnPropertyChanged(nameof(ShowSortTools));
    }

    private OrganizePreview? _preview;
    private int SelectedCount => Groups.SelectMany(g => g.Items).Count(i => i.IsChecked);

    private void BuildFolders()
    {
        Folders.Clear();
        Folders.Add(Option(_locations.Downloads, "Downloads"));
        if (!string.IsNullOrEmpty(_locations.Desktop)) Folders.Add(Option(_locations.Desktop, "Desktop"));
        foreach (var extra in Settings.ExtraFolders.Where(Directory.Exists))
            if (!Folders.Any(f => PathUtil.Normalize(f.Path).Equals(PathUtil.Normalize(extra), StringComparison.OrdinalIgnoreCase)))
                Folders.Add(Option(extra, Path.GetFileName(PathUtil.Normalize(extra))));
        Folders.Add(new FolderOption("", "Choose a folder…", null, IsPicker: true));
    }

    private FolderOption Option(string path, string label)
    {
        // Desktop is often redirected into OneDrive; detect it, never assume either way.
        bool cloud = _safety.IsInCloudRoot(path) && !_appSettings.AllowedAskFirstFolders.Any(f => PathUtil.IsUnder(path, f));
        return new FolderOption(path, label, cloud ? "in OneDrive, needs your OK" : null);
    }

    partial void OnSelectedFolderChanged(FolderOption? value)
    {
        if (value is { IsPicker: true })
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to organize" };
            if (dialog.ShowDialog() == true && Directory.Exists(dialog.FolderName))
            {
                Settings.ExtraFolders.Add(dialog.FolderName);
                SaveSettings();
                BuildFolders();
                SelectedFolder = Folders.FirstOrDefault(f => !f.IsPicker && PathUtil.Normalize(f.Path).Equals(PathUtil.Normalize(dialog.FolderName), StringComparison.OrdinalIgnoreCase));
            }
            else SelectedFolder = Folders.FirstOrDefault(f => !f.IsPicker);
            return;
        }
        FolderNote = value?.Note is null ? null : $"{value.Label} is in OneDrive. Allow it in Settings first; moves would stay inside OneDrive.";
        Title = value is null ? "Organize" : $"Organize {value.Label}";
        ClearPreview();
    }

    partial void OnUseDateFoldersChanged(bool value) { Settings.UseDateFolders = value; SaveSettings(); ClearPreview(); }
    partial void OnMoveWholeFoldersChanged(bool value) { Settings.MoveWholeFolders = value; SaveSettings(); ClearPreview(); }
    partial void OnIsDeepChanged(bool value) { OnPropertyChanged(nameof(DepthIndex)); ClearPreview(); }

    /// <summary>The shared Quick | Deep switch (0 = Quick, 1 = Deep).</summary>
    public int DepthIndex
    {
        get => IsDeep ? 1 : 0;
        set => IsDeep = value == 1;
    }

    /// <summary>The preview card's "Before → after | Every file" switch (0 = the tree, 1 = every change with ticks).</summary>
    public int DetailsIndex
    {
        get => ShowDetails ? 1 : 0;
        set => ShowDetails = value == 1;
    }

    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(DetailsIndex));

    partial void OnStepChanged(int value) => OnPropertyChanged(nameof(StepCaptions));

    /// <summary>Captions under the shared step bar ("a|b|c|d").</summary>
    public string StepCaptions => string.Join("|",
        Step >= 2 && _preview?.Roots.FirstOrDefault() is { } root ? $"{root.LooseFiles + root.LooseFolders:N0} items found" : "Looks only",
        "Check the moves",
        Step >= 4 ? "Moved" : "Nothing moved yet",
        "+ Undo");
    partial void OnIsScanningChanged(bool value) { MoveCommand.NotifyCanExecuteChanged(); SimulateCommand.NotifyCanExecuteChanged(); }

    private void SaveSettings()
    {
        lock (OrganizeLedger.SettingsLock)
        {
            Settings.Rules = Rules.Select(r => r.Rule).ToList();
            // Watch mode may have added to the ledger in the background: keep its entries.
            Settings.Ledger = OrganizeSettings.Load(_appSettings).Ledger;
            Settings.Store(_appSettings);
        }
        Watch?.RefreshAutomaticRules();
    }

    /// <summary>Settings for a scan, with the latest ledger (so files PC Tidy already sorted stay put).</summary>
    private OrganizeSettings ScanSettings()
    {
        var copy = Settings.Clone();
        lock (OrganizeLedger.SettingsLock) copy.Ledger = OrganizeSettings.Load(_appSettings).Ledger;
        return copy;
    }

    private void ClearPreview()
    {
        _preview = null;
        HasPreview = false;
        ShowDetails = false;
        Step = 1;
        BeforeRows.Clear(); AfterRows.Clear(); SkippedRows = Array.Empty<SkipRow>(); Groups.Clear(); DetailRows = Array.Empty<object>(); Notes.Clear();
        foreach (var r in Rules) r.Count = "";
        Summary = "Scan to see what would move";
        SummaryDetail = "Nothing moves until you confirm.";
        MoveButtonText = "Move files";
        MoveCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ scan

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedFolder is not { IsPicker: false } folder) return;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ProgressText = $"Looking at {folder.Label}…";
        try
        {
            var progress = new Progress<ScanProgress>(p => ProgressText = p.Message);
            var preview = await _planner.PlanAsync(new OrganizeRequest
            {
                Roots = new[] { folder.Path },
                Settings = ScanSettings(),
                Depth = IsDeep ? ScanDepth.Deep : ScanDepth.Quick,
            }, progress, ct);
            ShowPreview(preview);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _flow.ShowMessage("Couldn't scan", $"PC Tidy couldn't look at {folder.Label}: {ex.Message}", isError: true);
        }
        finally
        {
            IsScanning = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void ShowPreview(OrganizePreview preview)
    {
        ClearPreview();
        _preview = preview;
        var roots = preview.Roots.Select(r => r.Root).ToList();
        foreach (var row in PreviewTree.Before(preview)) BeforeRows.Add(row);
        foreach (var row in PreviewTree.After(preview)) AfterRows.Add(row);
        SkippedRows = preview.Skipped.Where(s => s.Kind != SkipKind.AlreadySorted)
            .Select(s => new SkipRow(s.Name, s.Reason, s.IsDirectory ? "Folder24" : PreviewTree.IconFor(s.Name))).ToList();
        var detail = new List<object>();
        foreach (var n in preview.Notes) Notes.Add(n);

        foreach (var g in preview.Actions.GroupBy(a => a.Group))
        {
            var desc = g.First().Kind switch
            {
                PlannedActionKind.DuplicateQuarantine => "Identical copies already there. Your call: quarantine the extra copy.",
                PlannedActionKind.Quarantine => "Your call. Quarantined files can be restored.",
                PlannedActionKind.Rename => "Renamed in the same folder.",
                _ => OrganizeResultBuilder.DescribeMoves(g, roots),
            };
            var group = new ActionGroupViewModel(g.Key, desc, g.Select(a => new ActionRowViewModel(a, roots, UpdateTotals)));
            Groups.Add(group);
            detail.Add(group);
            detail.AddRange(group.Items);
        }
        DetailRows = detail;

        var counts = preview.Actions.GroupBy(a => a.RuleId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var r in Rules) r.Count = counts.TryGetValue(r.Rule.Id, out var c) ? c.ToString("N0") : "0";
        foreach (var p in preview.Problems.Where(p => p.Blocking))
            if (Rules.FirstOrDefault(r => r.Rule.Id == p.RuleId) is { } row) row.Problem = p.Message;

        var root = preview.Roots.FirstOrDefault();
        Subtitle = root is null ? "" : $"{root.LooseFiles + root.LooseFolders:N0} loose items · rules run top to bottom, first match wins · every move can be put back";
        SkippedTitle = SkippedRows.Count == 1 ? "1 item stays put" : $"{SkippedRows.Count:N0} items stay put";
        HasPreview = true;
        Step = 2;
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Action).ToList();
        int moves = selected.Count(a => a.Kind is PlannedActionKind.Move or PlannedActionKind.MoveFolder);
        long bytes = selected.Sum(a => a.Size);
        Summary = selected.Count == 0 ? "Nothing ticked" : $"{Files(selected.Count)} will change · {Sizes.Format(bytes)}";
        var stay = SkippedRows.Count;
        SummaryDetail = $"Moves stay on the same drive (instant) · {stay:N0} stay put · put back any time from History";
        MoveButtonText = moves == selected.Count ? $"Move {Files(moves)}" : $"Tidy {Files(selected.Count)}";
        MoveCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";

    private bool CanRun() => HasPreview && SelectedCount > 0 && !IsScanning;

    private ActionPlan BuildPlan()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Action);
        return PlanAssembler.Build($"Organize {SelectedFolder?.Label}", selected);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task MoveAsync()
    {
        Step = 3;
        var run = await _flow.ReviewAndRunAsync(BuildPlan(), CancellationToken.None);
        if (run is null) { Step = 2; return; }
        OrganizeLedger.RecordInto(_appSettings, run, DateTime.UtcNow);
        Step = 4;
        await ScanAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SimulateAsync() => await _flow.SimulateAsync(BuildPlan(), CancellationToken.None);

    // ------------------------------------------------------------------ rules list

    [RelayCommand]
    private void MoveRuleUp(RuleRowViewModel? row) => MoveRule(row, -1);

    [RelayCommand]
    private void MoveRuleDown(RuleRowViewModel? row) => MoveRule(row, +1);

    private void MoveRule(RuleRowViewModel? row, int delta)
    {
        if (row is null) return;
        var i = Rules.IndexOf(row);
        MoveRuleTo(row, i + delta);
    }

    /// <summary>Drag-and-drop and up/down both end here. Order matters: first match wins.</summary>
    public void MoveRuleTo(RuleRowViewModel row, int newIndex)
    {
        var i = Rules.IndexOf(row);
        newIndex = Math.Clamp(newIndex, 0, Rules.Count - 1);
        if (i < 0 || i == newIndex) return;
        Rules.Move(i, newIndex);
        SaveSettings();
        ClearPreview();
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new OrganizeRule
        {
            Name = "My rule",
            Icon = "Folder24",
            Conditions = ConditionGroup.AllOf(RuleCondition.Ext("pdf")),
            Action = new RuleAction { Kind = RuleActionKind.MoveToFolder, Destination = @"Documents\Sorted" },
        };
        if (EditInDialog(rule, isNew: true) is { } saved)
        {
            // New rules go on top so they win over the broad categories.
            Rules.Insert(0, new RuleRowViewModel(saved, SaveSettings));
            SaveSettings();
            ClearPreview();
        }
    }

    [RelayCommand]
    private void EditRule(RuleRowViewModel? row)
    {
        if (row is null) return;
        if (EditInDialog(row.Rule.Clone(), isNew: false) is { } saved)
        {
            row.Replace(saved);
            row.Problem = null;
            SaveSettings();
            ClearPreview();
        }
    }

    [RelayCommand]
    private void DeleteRule(RuleRowViewModel? row)
    {
        if (row is null || row.Rule.IsBuiltIn) return;
        Rules.Remove(row);
        SaveSettings();
        ClearPreview();
    }

    [RelayCommand]
    private void ResetRules()
    {
        Rules.Clear();
        foreach (var r in DefaultRules.Create()) Rules.Add(new RuleRowViewModel(r, SaveSettings));
        SaveSettings();
        ClearPreview();
    }

    /// <summary>"Presets…": shows what a preset would change; only an explicit "Use this preset" changes the rules.</summary>
    [RelayCommand]
    private void OpenPresets()
    {
        var model = new PresetsViewModel(Rules.Select(r => r.Rule).ToList());
        var window = new PresetsWindow(model) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() == true && model.Result is { } rules) ApplyRules(rules);
    }

    /// <summary>Replaces the rule list (a preset the user confirmed). Nothing moves: the next scan shows the effect.</summary>
    public void ApplyRules(IReadOnlyList<OrganizeRule> rules)
    {
        Rules.Clear();
        foreach (var r in rules) Rules.Add(new RuleRowViewModel(r, SaveSettings));
        SaveSettings();
        ClearPreview();
    }

    // ------------------------------------------------------------------ keyboard triage

    /// <summary>Loads the one-file-at-a-time session for the chosen folder (read-only).</summary>
    public TriageSession CreateTriageSession(CancellationToken ct = default)
        => TriageSession.Load(SelectedFolder is { IsPicker: false } f ? f.Path : _locations.Downloads, ScanSettings(), _walker, _safety, DateTime.UtcNow, ct: ct);

    /// <summary>
    /// "One by one": keyboard triage in a focused window. When the user finishes, every decision becomes ONE plan
    /// shown in the standard confirm; nothing moves before that. The folder is listed and checked against the rules off
    /// the UI thread (a big Downloads on a slow disk takes a while), with the usual Stop button.
    /// </summary>
    [RelayCommand]
    private async Task TriageAsync()
    {
        if (IsScanning) return;
        TriageSession session;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ProgressText = "Listing the files…";
        // Settings and folder are read here, on the UI thread; only the listing and rule checks run in the background.
        var folder = SelectedFolder is { IsPicker: false } f ? f.Path : _locations.Downloads;
        var settings = ScanSettings();
        try { session = await Task.Run(() => TriageSession.Load(folder, settings, _walker, _safety, DateTime.UtcNow, ct: ct), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _flow.ShowMessage("Couldn't open the folder", $"PC Tidy couldn't list the files: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            IsScanning = false;
            ProgressText = "";
        }
        var window = new TriageWindow(new TriageViewModel(session)) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;
        await RunTriageAsync(session);
    }

    /// <summary>Builds the plan from a finished triage session and hands it to the shell's confirm.</summary>
    public async Task RunTriageAsync(TriageSession session)
    {
        var plan = session.BuildPlan($"Sort {SelectedFolder?.Label ?? "Downloads"} one by one");
        var problems = string.Join(" ", session.Problems);
        if (plan.Items.Count == 0)
        {
            _flow.ShowMessage("Nothing to do", ("Nothing was decided that changes a file, so nothing moves. " + problems).Trim());
            return;
        }
        if (problems.Length > 0) _flow.ShowMessage("Some files stay put", problems);
        var run = await _flow.ReviewAndRunAsync(plan, CancellationToken.None);
        if (run is null) return;
        OrganizeLedger.RecordInto(_appSettings, run, DateTime.UtcNow);
        if (HasPreview) await ScanAsync();
    }

    private OrganizeRule? EditInDialog(OrganizeRule rule, bool isNew)
    {
        var editor = new RuleEditorViewModel(rule, isNew, Validate, TestRuleAsync);
        var window = new RuleEditorWindow { DataContext = editor, Owner = System.Windows.Application.Current?.MainWindow };
        return window.ShowDialog() == true ? editor.Result : null;
    }

    private IReadOnlyList<RuleProblem> Validate(OrganizeRule rule)
        => RuleValidator.Validate(rule, Folders.Where(f => !f.IsPicker).Select(f => f.Path).ToList(), _safety, _appSettings);

    /// <summary>"Test this rule": runs the planner twice (with all rules, and with this rule alone). Moves nothing.</summary>
    public async Task<RuleTestResult> TestRuleAsync(OrganizeRule rule)
    {
        if (SelectedFolder is not { IsPicker: false } folder) return new RuleTestResult(Array.Empty<string>(), 0, new[] { "Pick a folder first." });
        var withAll = ScanSettings();
        var index = withAll.Rules.FindIndex(r => r.Id == rule.Id);
        var testRule = rule.Clone();
        testRule.Enabled = true;
        if (index >= 0) withAll.Rules[index] = testRule; else withAll.Rules.Insert(0, testRule);
        var alone = ScanSettings();
        alone.Rules = new List<OrganizeRule> { testRule };

        OrganizeRequest Request(OrganizeSettings s) => new() { Roots = new[] { folder.Path }, Settings = s, Depth = IsDeep ? ScanDepth.Deep : ScanDepth.Quick, CheckInUse = false };
        var inOrder = await _planner.PlanAsync(Request(withAll), null, CancellationToken.None);
        var onItsOwn = await _planner.PlanAsync(Request(alone), null, CancellationToken.None);

        var roots = new[] { folder.Path };
        var matches = inOrder.Actions.Where(a => a.RuleId == rule.Id)
            .Select(a => a.DestinationPath is null ? $"{a.Name} · {a.Group}" : $"{a.Name} → {DisplayPaths.Short(Path.GetDirectoryName(a.DestinationPath)!, roots)}")
            .ToList();
        var earlier = onItsOwn.Actions.Count(a => a.RuleId == rule.Id) - matches.Count;
        var notes = inOrder.Problems.Where(p => p.RuleId == rule.Id).Select(p => p.Message).Concat(inOrder.Notes).Distinct().ToList();
        return new RuleTestResult(matches, Math.Max(0, earlier), notes);
    }
}
