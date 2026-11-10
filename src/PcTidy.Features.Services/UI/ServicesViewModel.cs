using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Services.Model;
using PcTidy.Features.Services.Scanning;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Services.UI;

/// <summary>A profile card: what it is and what it would change here.</summary>
public sealed record ProfileCard(ServiceProfileState State)
{
    public string Title => State.Profile.Title;
    public string Description => State.Profile.Description;
    public string Status => State.Status;
    public bool CanApply => State.Changes.Count > 0;
    public string AutomationName => $"Tick the {Title} profile. {Status}";
    /// <summary>Compact chip for short windows: "Privacy (1)", or just the name when there is nothing to tick.</summary>
    public string ChipText => CanApply ? $"{Title} ({State.Changes.Count})" : Title;
    public string ChipTip => $"{Description} {Status}";
}

/// <summary>
/// The Services page: scan (read-only, off the UI thread, cancellable), filter (All / Can change / Third-party /
/// Flagged / Protected), search, a details pane with dependencies, profile cards, the bad-services repair, Expert
/// mode, and a sticky bar that hands the ticked changes to the shell's confirm (restore point, run, Done card with
/// Undo) or its simulation. Nothing on this page changes the PC by itself.
/// </summary>
public sealed partial class ServicesViewModel : ObservableObject
{
    public const int FilterAll = 0, FilterCanChange = 1, FilterThirdParty = 2, FilterFlagged = 3, FilterProtected = 4;

    private readonly IServiceProvider _services;
    private readonly ServicesModule _module;
    private readonly AppSettings _settings;
    private List<ServiceRowViewModel> _all = new();
    private CancellationTokenSource? _scanCts;
    private bool _rescanAfter;

    public ServicesViewModel(IServiceProvider services, ServicesModule module)
    {
        _services = services;
        _module = module;
        _settings = services.Get<AppSettings>();
        Notes.CollectionChanged += (_, _) => NotesChanged();
        UpdateSelection();
    }

    private void NotesChanged()
    {
        OnPropertyChanged(nameof(VisibleNotes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasMoreNotes));
        OnPropertyChanged(nameof(NotesToggleText));
        OnPropertyChanged(nameof(NotesToggleName));
    }

    partial void OnShowAllNotesChanged(bool value) => NotesChanged();

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProfileCards));
        OnPropertyChanged(nameof(ShowProfileChips));
    }

    [RelayCommand]
    private void ToggleNotes() => ShowAllNotes = !ShowAllNotes;

    private IFlowHost? Flow => _services.GetService(typeof(IFlowHost)) as IFlowHost;
    private IShellFlowHost? Shell => _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? Flow as IShellFlowHost;

    /// <summary>The visible rows (after filter, search and the drivers switch).</summary>
    public ObservableCollection<ServiceRowViewModel> Rows { get; } = new();
    public ObservableCollection<ProfileCard> Profiles { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _scanMessage = "";
    [ObservableProperty] private string _subtitle = "What each service does, who made it, and whether it's safe to change. Scanning only reads; nothing changes.";
    [ObservableProperty] private int _filter;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showDrivers;
    [ObservableProperty] private ServiceRowViewModel? _selectedRow;
    [ObservableProperty] private string _selectionText = "Nothing selected";
    [ObservableProperty] private string _selectionCaption = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private int _repairCount;
    [ObservableProperty] private string _filterItems = "All|Can change|Third-party|Flagged|Protected";
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string _stepCaptions = ReadyCaptions;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _showAllNotes;

    private const string ReadyCaptions = "Reads, changes nothing|Pick what changes|One last check|";

    /// <summary>The notes shown: the first one, or all of them after "More".</summary>
    public IReadOnlyList<string> VisibleNotes => ShowAllNotes ? Notes.ToList() : Notes.Take(1).ToList();
    public bool HasNotes => Notes.Count > 0;
    public bool HasMoreNotes => Notes.Count > 1;
    public string NotesToggleText => ShowAllNotes ? "Less" : $"More ({Notes.Count - 1})";
    public string NotesToggleName => ShowAllNotes ? "Show only the first note" : $"Show {Notes.Count - 1} more note{(Notes.Count == 2 ? "" : "s")}";
    /// <summary>Short windows (the page sets <see cref="IsCompact"/>) show the profiles as one row of chips.</summary>
    public bool ShowProfileCards => HasScanned && !IsCompact;
    public bool ShowProfileChips => HasScanned && IsCompact;

    public bool ShowExpertWarning => ExpertMode;
    public bool ShowEmpty => HasScanned && !IsScanning && Rows.Count == 0;
    public bool HasDetails => SelectedRow is not null;
    public bool HasRepairs => RepairCount > 0;
    public string RepairText => RepairCount == 1
        ? "1 Windows service was switched off by something. Putting back its Windows default fixes what that broke."
        : $"{RepairCount} Windows services were switched off by something. Putting back their Windows defaults fixes what that broke.";
    public string EmptyText => _all.Count == 0 ? "No services were found." : "No services match. Try another filter or search.";

    /// <summary>Expert mode (shared setting, saved by the shell): unlocks Situational services. Rescans to rebuild the verdicts.</summary>
    public bool ExpertMode
    {
        get => _settings.ExpertMode;
        set
        {
            if (_settings.ExpertMode == value) return;
            if (value && ConfirmExpertMode is { } confirm) _ = TurnOnAfterConfirmAsync(confirm);
            else SetExpertMode(value);
        }
    }

    /// <summary>Set by the page: Settings' "Turn on Expert mode?" question (true = turn on).</summary>
    public Func<Task<bool>>? ConfirmExpertMode { get; set; }

    private async Task TurnOnAfterConfirmAsync(Func<Task<bool>> confirm)
    {
        OnPropertyChanged(nameof(ExpertMode)); // the switch stays off while the question is open
        if (await confirm()) SetExpertMode(true);
    }

    private void SetExpertMode(bool value)
    {
        _settings.ExpertMode = value;
        OnPropertyChanged(nameof(ExpertMode));
        OnPropertyChanged(nameof(ShowExpertWarning));
        if (IsScanning) _rescanAfter = true;
        else if (HasScanned) _ = ScanAsync();
    }

    partial void OnFilterChanged(int value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowDriversChanged(bool value) => ApplyFilter();
    partial void OnSelectedRowChanged(ServiceRowViewModel? value) => OnPropertyChanged(nameof(HasDetails));
    partial void OnRepairCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasRepairs));
        OnPropertyChanged(nameof(RepairText));
    }

    // ------------------------------------------------------------------ scan

    [RelayCommand]
    private Task Scan() => ScanAsync();

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        ScanMessage = "Listing services…";
        Step = 1;
        StepCaptions = "Reading…|Pick what changes|One last check|";
        var keepTicked = _all.Where(r => r.IsChecked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Quick);
            var progress = new Progress<ScanProgress>(p => ScanMessage = p.Message);
            var inventory = await _module.Scanner.InventoryAsync(ctx, progress, ct);
            var repairs = ServiceRepairScanner.Find(inventory.Services).ToDictionary(r => r.Service.Name, StringComparer.OrdinalIgnoreCase);
            _all = inventory.All.Select(s => new ServiceRowViewModel(s, repairs.GetValueOrDefault(s.Name), UpdateSelection)).ToList();
            foreach (var r in _all.Where(r => keepTicked.Contains(r.Name))) r.SetCheckedQuietly(true);
            RepairCount = repairs.Count;
            Profiles.Clear();
            foreach (var p in ServiceProfileBuilder.All) Profiles.Add(new ProfileCard(ServiceProfileBuilder.State(p, inventory.Services, ctx.System)));
            Notes.Clear();
            foreach (var n in inventory.Notes) Notes.Add(n);
            if (!ExpertMode && inventory.Services.Any(s => s.Assessment.Verdict == ServiceVerdictKind.Situational && s.Entry?.Recommended is not null))
                Notes.Add("Some services only help in certain setups. They need Expert mode and are shown greyed until then.");
            var changeable = inventory.Services.Count(s => s.Assessment.CanChange) + repairs.Count(r => !r.Value.Service.Assessment.CanChange);
            Subtitle = $"Checked at {DateTime.Now:t} · {inventory.Services.Count} services · {changeable} can change · {inventory.Drivers.Count} driver{(inventory.Drivers.Count == 1 ? "" : "s")} (hidden unless you show them)";
            HasScanned = true;
            Step = 2;
            StepCaptions = ScannedCaptions(inventory.Services.Count, picked: null);
        }
        catch (OperationCanceledException)
        {
            ScanMessage = "Stopped.";
        }
        catch (Exception ex)
        {
            Flow?.ShowMessage("Scan didn't finish", "Something went wrong while reading your services: " + ex.Message + " Nothing was changed.", isError: true);
        }
        finally
        {
            IsScanning = false;
            // Stopped or failed: back to where the steps were before this scan.
            if (!HasScanned) { Step = 1; StepCaptions = ReadyCaptions; }
            else if (Step == 1) { Step = 2; StepCaptions = ScannedCaptions(_all.Count(r => !r.Info.IsDriver), picked: null); }
            ApplyFilter();
            UpdateSelection();
        }
        if (_rescanAfter)
        {
            _rescanAfter = false;
            await ScanAsync();
        }
    }

    /// <summary>All rows, drivers included (tests and screenshots).</summary>
    public IReadOnlyList<ServiceRowViewModel> AllRows => _all;

    private void ApplyFilter()
    {
        var search = SearchText?.Trim() ?? "";
        var visible = _all.Where(r => ShowDrivers || !r.Info.IsDriver).Where(r => Filter switch
        {
            FilterCanChange => r.CanChange,
            FilterThirdParty => r.IsThirdParty,
            FilterFlagged => r.IsFlagged,
            FilterProtected => r.IsProtected,
            _ => true,
        }).Where(r => search.Length == 0
            || r.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || r.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || r.WhatItDoes.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            // Changeable and flagged first, so the list starts with what matters.
            .OrderBy(r => r.Repair is not null ? 0 : r.CanChange ? 1 : r.IsFlagged ? 2 : 3)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var r in visible) Rows.Add(r);
        SelectedRow = selected is not null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        FilterItems = $"All ({Count(_ => true)})|Can change ({Count(r => r.CanChange)})|Third-party ({Count(r => r.IsThirdParty)})|Flagged ({Count(r => r.IsFlagged)})|Protected ({Count(r => r.IsProtected)})";
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private int Count(Func<ServiceRowViewModel, bool> where) => _all.Count(r => (ShowDrivers || !r.Info.IsDriver) && where(r));

    // ------------------------------------------------------------------ profiles, repair, selection

    /// <summary>Ticks exactly the profile's proven changes (with their recommended start type); leaves other ticks alone.</summary>
    [RelayCommand]
    private void ApplyProfile(ProfileCard? card)
    {
        if (card is null) return;
        var names = card.State.Changes.ToDictionary(c => c.Service.Name, c => c.Option.Target, StringComparer.OrdinalIgnoreCase);
        foreach (var r in _all.Where(r => names.ContainsKey(r.Name)))
            if (r.Select(ServicesIds.Module, names[r.Name])) r.SetCheckedQuietly(true);
        UpdateSelection();
    }

    [RelayCommand]
    private void TickRepairs()
    {
        foreach (var r in _all.Where(r => r.Repair is not null))
            if (r.Select(ServicesIds.Repair, r.Repair!.Target)) r.SetCheckedQuietly(true);
        Filter = FilterFlagged;
        UpdateSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in _all) r.SetCheckedQuietly(false);
        UpdateSelection();
    }

    private IEnumerable<ServiceRowViewModel> Checked => _all.Where(r => r.IsChecked && r.CanChange);

    private void UpdateSelection()
    {
        var picked = Checked.ToList();
        // A new tick after a finished run starts the next round at "Pick what changes".
        if (Step == 4 && picked.Count > 0) { Step = 2; StepCaptions = ScannedCaptions(_all.Count(r => !r.Info.IsDriver), picked: null); }
        HasSelection = picked.Count > 0;
        SelectionText = picked.Count == 0 ? "Nothing selected" : $"Selected {picked.Count} service{(picked.Count == 1 ? "" : "s")}";
        var disabled = picked.Count(r => r.SelectedOption?.Target == ServiceStart.Disabled);
        var breaks = picked.Count(r => r.HasBreaks && r.SelectedOption?.Target is ServiceStart.Manual or ServiceStart.Disabled);
        var extras = new List<string>();
        if (picked.Count > 0) extras.Add("needs admin · restore point first");
        if (disabled > 0) extras.Add($"{disabled} set to Disabled");
        if (breaks > 0) extras.Add(breaks == 1 ? "1 says what might stop working" : $"{breaks} say what might stop working");
        SelectionCaption = picked.Count > 0 ? string.Join(" · ", extras) + " · Undo from History puts them back exactly" : "Tick a service, a profile or the repairs to start.";
        PreviewCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>One plan per feature: section changes ("services"), then repairs ("services.repair").</summary>
    public IReadOnlyList<ActionPlan> BuildPlans()
    {
        var items = Checked.Select(r => r.PlanItem()).Where(i => i is not null).Select(i => i!).ToList();
        var plans = new List<ActionPlan>();
        var section = items.Where(i => i.FeatureId == ServicesIds.Module).ToList();
        if (section.Count > 0) plans.Add(new ActionPlan { FeatureId = ServicesIds.Module, Title = "Services", Items = section });
        var repairs = items.Where(i => i.FeatureId == ServicesIds.Repair).ToList();
        if (repairs.Count > 0) plans.Add(new ActionPlan { FeatureId = ServicesIds.Repair, Title = "Bad services repair", Items = repairs });
        return plans;
    }

    // ------------------------------------------------------------------ run

    private bool CanRun() => HasSelection && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Preview()
    {
        var plans = BuildPlans();
        if (plans.Count == 0) return;
        var ran = false;
        int services = _all.Count(r => !r.Info.IsDriver);
        Step = 3;
        StepCaptions = ScannedCaptions(services, picked: Checked.Count());
        try
        {
            if (Shell is { } shell)
            {
                var outcome = await shell.ReviewAndRunAsync(plans, new FlowOptions { Title = "Ready to change these services?", PrimaryVerb = "Apply", ShowResult = true }, CancellationToken.None);
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
            ClearSelection();
            await ScanAsync(); // show the new state
            Step = 4;
            StepCaptions = "Done|Applied|Restore point made|Undo from History";
        }
        else
        {
            Step = 2;
            StepCaptions = ScannedCaptions(services, picked: null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Simulate()
    {
        var plans = BuildPlans();
        if (plans.Count == 0) return;
        try
        {
            if (Shell is { } shell) await shell.SimulateAsync(plans, new FlowOptions { Title = "Services", ShowResult = true }, CancellationToken.None);
            else if (Flow is { } flow) foreach (var p in plans) await flow.SimulateAsync(p, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Flow?.ShowMessage("Simulation didn't finish", ex.Message, isError: true);
        }
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
        OnPropertyChanged(nameof(ShowProfileCards));
        OnPropertyChanged(nameof(ShowProfileChips));
    }

    private static string ScannedCaptions(int services, int? picked)
        => $"{services} service{(services == 1 ? "" : "s")}|{(picked is { } p ? $"{p} picked" : "Pick what changes")}|One last check|";
}
