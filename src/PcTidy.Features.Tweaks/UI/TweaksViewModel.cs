using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Tweaks.Tweaks;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Tweaks.UI;

/// <summary>
/// The Tweaks page: two tabs (Tweaks, Find bad tweaks), presets, the Expert switch, three-state rows and a sticky
/// bar. Scanning only reads; Preview hands the ticked rows to the shell's confirm dialog (restore point, run,
/// Done card with Undo). Links open through the shell as harmless "open a page" steps, never directly.
/// </summary>
public sealed partial class TweaksViewModel : ObservableObject
{
    public const int TweaksTab = 0;
    public const int FinderTab = 1;

    private readonly IServiceProvider _services;
    private readonly TweaksModule _module;
    private readonly AppSettings _settings;
    private List<TweakItemRow> _tweakRows = new();
    private List<TweakItemRow> _finderRows = new();
    private List<(TweakHeaderRow Header, List<TweakItemRow> Rows)> _finderSections = new();
    private CancellationTokenSource? _scanCts;
    private IReadOnlyList<string> _tweakNotes = Array.Empty<string>();
    private IReadOnlyList<string> _finderNotes = Array.Empty<string>();
    private bool _rescanTweaks;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private TweakChip _chip = TweakChip.All;
    private bool _viewChosen;

    /// <summary>Key of the page's view preferences in <see cref="AppSettings.Features"/> (the shell saves settings).</summary>
    public const string PrefsKey = "tweaks.view";
    /// <summary>Below this window width the page starts in List view unless the user picked a view (layout spec §4.1).</summary>
    public const double CardsMinWidth = 1100;

    public TweaksViewModel(IServiceProvider services, TweaksModule module)
    {
        _services = services;
        _module = module;
        _settings = services.Get<AppSettings>();
        Presets = TweakPresets.All.Select(p => new PresetChoice(p.Preset, p.Title, p.Description + " " + p.Excluded)).ToList();
        Chips = TweakFilters.ChipOrder.Select(c => new ChipItem(c, SelectChip)).ToList();
        Chips[0].SetSelectedQuietly(true);
        var prefs = LoadPrefs();
        if (prefs?.View is "Cards" or "List")
        {
            _viewChosen = true;
            _viewIndex = prefs.View == "Cards" ? 1 : 0;
        }
        UpdateSelection();
    }

    /// <summary>The page's per-user view choice, stored with the other settings (<see cref="AppSettings.SetFeature{T}"/>).</summary>
    public sealed class ViewPrefs
    {
        /// <summary>"List" or "Cards"; null = not picked yet (List below <see cref="CardsMinWidth"/>).</summary>
        public string? View { get; set; }
    }

    private ViewPrefs? LoadPrefs()
    {
        try { return _settings.GetFeature<ViewPrefs>(PrefsKey); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private IFlowHost? Flow => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Flow as IShellFlowHost;

    public sealed record PresetChoice(TweakPreset Preset, string Title, string Description);

    public IReadOnlyList<PresetChoice> Presets { get; }
    /// <summary>The flat list for the visible tab: headers and rows.</summary>
    public ObservableCollection<object> Rows { get; } = new();
    /// <summary>Scan notes for the visible tab (shown as short captions).</summary>
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private int _tab;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _subtitle = "Only real, sourced tweaks. Scanning reads your current settings and changes nothing.";
    [ObservableProperty] private string _scanMessage = "";
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string _stepCaptions = "Reads your settings|Pick what changes|One last check|";
    [ObservableProperty] private string _selectionText = "Nothing selected";
    [ObservableProperty] private string _selectionCaption = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _emptyText = "";

    public bool IsTweaksTab => Tab == TweaksTab;
    public bool ShowExpertWarning => ExpertMode && IsTweaksTab;
    public bool ShowEmpty => HasScanned && !IsScanning && Rows.Count == 0;

    // ------------------------------------------------------------------ search, chips, sort, view (layout spec §4.1)

    /// <summary>Category chips with counts (All · Gaming · Privacy · Interface · Network · Power · Doesn't help · Expert).</summary>
    public IReadOnlyList<ChipItem> Chips { get; }
    public TweakChip SelectedChip => _chip;
    /// <summary>The Tweaks tab's sections for Cards view (List view uses <see cref="Rows"/>).</summary>
    public ObservableCollection<TweakSection> Sections { get; } = new();
    public IReadOnlyList<string> SortOptions { get; } = Enum.GetValues<TweakSort>().Select(TweakFilters.SortTitle).ToList();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private int _viewIndex;
    /// <summary>"7 of 31 on": counted from the scan's read-back (same rule as the rail badge).</summary>
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private string _counterAutomationName = "";
    [ObservableProperty] private PresetPreview? _presetPreview;
    [ObservableProperty] private bool _isPresetPopoverOpen;

    public TweakSort Sort => (TweakSort)Math.Clamp(SortIndex, 0, 2);
    public bool IsCardView => ViewIndex == 1;
    public bool ShowCards => HasScanned && IsTweaksTab && IsCardView;
    public bool ShowList => HasScanned && !ShowCards;
    public bool HasCounter => CounterText.Length > 0 && IsTweaksTab;

    partial void OnSearchTextChanged(string value) => RefreshTweaks();
    partial void OnSortIndexChanged(int value) => RefreshTweaks();

    partial void OnViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCardView));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ViewChoice));
    }

    /// <summary>The List | Cards switch: a user pick (remembered); <see cref="SetWindowWidth"/> changes <see cref="ViewIndex"/> only.</summary>
    public int ViewChoice
    {
        get => ViewIndex;
        set { if (value != ViewIndex) ChooseView(value); }
    }

    partial void OnCounterTextChanged(string value) => OnPropertyChanged(nameof(HasCounter));

    /// <summary>The user picked List or Cards: remember it for next time (stored in settings, which the shell saves).</summary>
    public void ChooseView(int index)
    {
        _viewChosen = true;
        ViewIndex = index == 1 ? 1 : 0;
        try { _settings.SetFeature(PrefsKey, new ViewPrefs { View = IsCardView ? "Cards" : "List" }); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { /* kept in memory for this session */ }
    }

    /// <summary>Called with the window width: until the user picks a view, Cards on wide windows and List below ~1100 px.</summary>
    public void SetWindowWidth(double width)
    {
        if (_viewChosen || width <= 0 || double.IsNaN(width)) return;
        ViewIndex = width >= CardsMinWidth ? 1 : 0;
    }

    private void SelectChip(TweakChip chip)
    {
        _chip = chip;
        foreach (var c in Chips) c.SetSelectedQuietly(c.Chip == chip);
        OnPropertyChanged(nameof(SelectedChip));
        RefreshTweaks();
    }

    /// <summary>Picks a chip (tests and keyboard shortcuts).</summary>
    public void SelectChipCommandless(TweakChip chip) => SelectChip(chip);

    /// <summary>Rebuilds the Tweaks tab's list and cards from the current chip, search and sort. Changes nothing on the PC.</summary>
    private void RefreshTweaks()
    {
        var counts = TweakFilters.Counts(_tweakRows, SearchText);
        foreach (var c in Chips) c.Count = counts[c.Chip];
        if (Tab == TweaksTab) ShowTab();
    }

    private void UpdateCounter()
    {
        var count = TweakCounter.Count(_tweakRows.Where(r => r.Tweak is not null && r.State is not null).Select(r => (r.Tweak!, r.State!)));
        CounterText = count.Text ?? "";
        CounterAutomationName = count.AutomationText;
    }

    // ------------------------------------------------------------------ preset popover

    /// <summary>Opens the preset's "what will change" popover. Nothing is ticked until <see cref="ConfirmPreset"/>.</summary>
    [RelayCommand]
    private void PreviewPreset(PresetChoice? choice)
    {
        if (choice is null) return;
        PresetPreview = PresetPreview.Build(TweakPresets.Info(choice.Preset), _tweakRows);
        IsPresetPopoverOpen = true;
    }

    [RelayCommand]
    private void ConfirmPreset()
    {
        if (PresetPreview is { } p) ApplyPreset(Presets.First(c => c.Preset == p.Preset));
        IsPresetPopoverOpen = false;
    }

    [RelayCommand]
    private void CancelPreset() => IsPresetPopoverOpen = false;

    /// <summary>Expert mode (Plan §8): unlocks risky-but-not-security rows. Stored in the shared settings (the shell saves them).</summary>
    public bool ExpertMode
    {
        get => _settings.ExpertMode;
        set
        {
            if (_settings.ExpertMode == value) return;
            if (value && ConfirmExpertMode is { } confirm)
            {
                // Same question as Settings before anything risky is unlocked. The switch stays off until the user says yes.
                _ = TurnOnAfterConfirmAsync(confirm);
                return;
            }
            ApplyExpertMode(value);
        }
    }

    /// <summary>
    /// Asks "Turn on Expert mode?" (set by the page, which owns the dialog). Null in tests and screenshots without a
    /// window: the switch then turns on directly, as before.
    /// </summary>
    public Func<Task<bool>>? ConfirmExpertMode { get; set; }

    private async Task TurnOnAfterConfirmAsync(Func<Task<bool>> confirm)
    {
        // Put the switch back to "off" while the question is open (the binding already flipped it).
        _ui?.Post(_ => OnPropertyChanged(nameof(ExpertMode)), null);
        bool yes;
        try { yes = await confirm(); }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException) { yes = false; }
        if (yes) ApplyExpertMode(true);
        else OnPropertyChanged(nameof(ExpertMode));
    }

    private void ApplyExpertMode(bool value)
    {
        _settings.ExpertMode = value;
        OnPropertyChanged(nameof(ExpertMode));
        OnPropertyChanged(nameof(ShowExpertWarning));
        // The list is rebuilt for the new mode; the user's ticks are kept (expert rows that can't be picked any more drop out).
        if (IsScanning) _rescanTweaks = true; // picked up when the running scan ends
        else if (HasScanned) _ = ScanAsync(onlyTweaks: true);
    }

    partial void OnTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsTweaksTab));
        OnPropertyChanged(nameof(ShowExpertWarning));
        ShowTab();
    }

    // ------------------------------------------------------------------ scan

    [RelayCommand]
    private Task Scan() => ScanAsync(onlyTweaks: false);

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    public async Task ScanAsync(bool onlyTweaks)
    {
        if (IsScanning) return;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ScanMessage = "Reading your current settings…";
        Step = 1;
        StepCaptions = "Reading…|Pick what changes|One last check|";
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var progress = new Progress<ScanProgress>(p => ScanMessage = p.Message);
            // A rescan for Expert mode keeps what the user ticked (rows that can't be picked any more simply stay unticked).
            var keepTicks = onlyTweaks ? _tweakRows.Where(r => r.IsChecked).Select(r => r.Item.Id).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
            var outcome = await _module.TweaksScanner.ScanRowsAsync(ctx, progress, ct);
            _tweakRows = outcome.Rows.Select(r => TweakItemRow.ForTweak(r, OpenLink, UpdateSelection)).ToList();
            foreach (var r in _tweakRows.Where(r => keepTicks.Contains(r.Item.Id))) r.SetCheckedQuietly(true);
            var counts = TweakFilters.Counts(_tweakRows, SearchText);
            foreach (var c in Chips) c.Count = counts[c.Chip];
            UpdateCounter();
            _tweakNotes = outcome.Notes;

            if (!onlyTweaks || !HasScanned)
            {
                ScanMessage = "Looking for bad tweaks…";
                var finder = await _module.BadTweakScanner.ScanAsync(ctx, progress, ct);
                _finderSections = finder.Groups
                    .Select(g => (new TweakHeaderRow(g.Title, g.Description ?? "", g.Items.Count), g.Items.Select(i => TweakItemRow.ForFinding(i, OpenLink, UpdateSelection)).ToList()))
                    .ToList();
                _finderRows = _finderSections.SelectMany(s => s.Rows).ToList();
                _finderNotes = finder.Notes;
            }
            HasScanned = true;
            Subtitle = $"Checked at {DateTime.Now:t} · {_tweakRows.Count(r => r.IsSelectable)} tweaks available · {_finderRows.Count} bad tweak{(_finderRows.Count == 1 ? "" : "s")} found";
            Step = 2;
            StepCaptions = $"{Settings(_tweakRows.Count + _finderRows.Count)}|Pick what changes|One last check|";
        }
        catch (OperationCanceledException)
        {
            ScanMessage = "Stopped.";
        }
        catch (Exception ex)
        {
            Flow?.ShowMessage("Scan didn't finish", "Something went wrong while reading your settings: " + ex.Message + " Nothing was changed.", isError: true);
        }
        finally
        {
            IsScanning = false;
            ShowTab();
            UpdateSelection();
        }
        if (_rescanTweaks)
        {
            _rescanTweaks = false;
            await ScanAsync(onlyTweaks: true);
        }
    }

    private static string Settings(int n) => n == 1 ? "1 setting" : $"{n} settings";

    private void ShowTab()
    {
        Rows.Clear();
        Sections.Clear();
        if (Tab == FinderTab)
        {
            foreach (var (header, rows) in _finderSections)
            {
                Rows.Add(header);
                foreach (var r in rows) Rows.Add(r);
            }
        }
        else
        {
            // Tweaks tab: chip, search and sort decide what shows; the same sections feed List (Rows) and Cards (Sections).
            foreach (var section in TweakFilters.Sections(_tweakRows, _chip, SearchText, Sort))
            {
                Sections.Add(section);
                Rows.Add(section.Header);
                foreach (var r in section.Rows) Rows.Add(r);
            }
        }
        Notes.Clear();
        foreach (var n in Tab == FinderTab ? _finderNotes : _tweakNotes) Notes.Add(n);
        EmptyText = Tab == FinderTab ? "No bad tweaks found. Nothing needs fixing."
            : _tweakRows.Count == 0 ? "No tweaks apply to this PC."
            : "Nothing matches. Try another word or pick All.";
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(HasCounter));
    }

    // ------------------------------------------------------------------ presets and selection

    /// <summary>Ticks exactly the proven, non-opt-in rows of a preset (Tweaks.md "Recommended presets"); unticks the rest of the tab.</summary>
    [RelayCommand]
    private void ApplyPreset(PresetChoice? choice)
    {
        if (choice is null) return;
        Tab = TweaksTab;
        foreach (var r in _tweakRows) r.SetCheckedQuietly(r.Preset == choice.Preset && r.Item.DefaultSelected);
        UpdateSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in _tweakRows.Concat(_finderRows)) r.SetCheckedQuietly(false);
        UpdateSelection();
    }

    private IEnumerable<TweakItemRow> Checked => _tweakRows.Concat(_finderRows).Where(r => r.IsChecked && r.IsSelectable);

    private void UpdateSelection()
    {
        var picked = Checked.ToList();
        var actions = picked.SelectMany(r => r.Item.Actions).ToList();
        HasSelection = actions.Count > 0;
        SelectionText = picked.Count == 0 ? "Nothing selected" : $"Selected {picked.Count} change{(picked.Count == 1 ? "" : "s")}";
        var extras = new List<string>();
        var admin = actions.Count(a => a.NeedsAdmin);
        if (admin > 0) extras.Add($"{admin} step{(admin == 1 ? "" : "s")} need admin");
        if (actions.Any(a => a.IsSystemChange)) extras.Add("restore point first");
        if (picked.Any(r => r.Item.NeedsRestart)) extras.Add("restart needed afterwards");
        var permanent = actions.Count(a => !a.Reversible);
        if (permanent > 0) extras.Add($"{permanent} can't be undone by PC Tidy");
        SelectionCaption = extras.Count > 0 ? string.Join(" · ", extras) : "Every change can be undone from History";
        PreviewCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>One plan per section with ticks (tweaks, then repairs).</summary>
    public IReadOnlyList<ActionPlan> BuildPlans()
    {
        var plans = new List<ActionPlan>();
        var tweaks = _tweakRows.Where(r => r.IsChecked && r.IsSelectable).Select(r => r.Item).ToList();
        if (tweaks.Count > 0) plans.Add(PlanBuilder.FromSelection(TweakPlanFeatures.Tweaks, "Tweaks", tweaks));
        var repairs = _finderRows.Where(r => r.IsChecked && r.IsSelectable).Select(r => r.Item).ToList();
        if (repairs.Count > 0) plans.Add(PlanBuilder.FromSelection(TweakPlanFeatures.Finder, "Find bad tweaks", repairs));
        return plans.Where(p => p.Items.Count > 0).ToList();
    }

    // ------------------------------------------------------------------ run

    private bool CanRun() => HasSelection && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Preview()
    {
        var plans = BuildPlans();
        if (plans.Count == 0) return;
        Step = 3;
        StepCaptions = $"{Settings(_tweakRows.Count + _finderRows.Count)}|{Checked.Count()} picked|One last check|";
        var ran = false;
        try
        {
            if (Shell is { } shell)
            {
                var outcome = await shell.ReviewAndRunAsync(plans, new FlowOptions
                {
                    Title = "Ready to apply these changes?", PrimaryVerb = "Apply", ShowResult = true,
                    RestartItemIds = Checked.Where(r => r.Item.NeedsRestart).SelectMany(r => r.Item.Actions).Select(a => a.Id).ToHashSet(),
                }, CancellationToken.None);
                ran = outcome.Kind == FlowOutcomeKind.Ran;
            }
            else if (Flow is { } flow)
            {
                foreach (var plan in plans)
                    if (await flow.ReviewAndRunAsync(plan, CancellationToken.None) is not null) ran = true;
            }
        }
        catch (Exception ex)
        {
            Flow?.ShowMessage("That didn't work", ex.Message + " Anything that already ran is in History, where you can undo it.", isError: true);
        }
        if (ran)
        {
            Step = 4;
            await ScanAsync(onlyTweaks: false); // show the new state (and that the repairs are gone)
            Step = 4;
            StepCaptions = "Done|Applied|Restore point made|Undo from History";
        }
        else Step = 2;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Simulate()
    {
        var plans = BuildPlans();
        if (plans.Count == 0) return;
        try
        {
            if (Shell is { } shell) await shell.SimulateAsync(plans, new FlowOptions { Title = "Tweaks", ShowResult = true }, CancellationToken.None);
            else if (Flow is { } flow) foreach (var p in plans) await flow.SimulateAsync(p, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Flow?.ShowMessage("Simulation didn't finish", ex.Message, isError: true);
        }
    }

    // ------------------------------------------------------------------ links

    /// <summary>Opens a source or Settings link through the shell (an OpenExternal step: safety-checked, journaled, harmless).</summary>
    private void OpenLink(string target)
    {
        if (Flow is not { } flow) return;
        _ = LinkOpener.OpenAsync(flow, target);
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        PreviewCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasScannedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(ShowList));
    }

    /// <summary>All rows of both tabs (tests and screenshots).</summary>
    public IReadOnlyList<TweakItemRow> AllRows => _tweakRows.Concat(_finderRows).ToList();
}

/// <summary>Opens a link as a one-step OpenExternal plan, so it goes through the safety guard and the shell (no Process.Start here).</summary>
public static class LinkOpener
{
    public static async Task OpenAsync(IFlowHost flow, string target)
    {
        var plan = new ActionPlan
        {
            FeatureId = TweakPlanFeatures.Tweaks,
            Title = "Open a page",
            Items = new[]
            {
                new PlanItem
                {
                    Kind = ActionKind.OpenExternal, FeatureId = TweakPlanFeatures.Tweaks, Description = "Open " + target,
                    Args = new Dictionary<string, string> { ["target"] = target }, Reversible = true,
                },
            },
        };
        try { await flow.RunApprovedAsync(plan, CancellationToken.None); }
        catch (Exception ex) { flow.ShowMessage("Couldn't open the page", ex.Message); }
    }
}
