using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Catalog.Apps;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Apps.Inventory;
using PcTidy.Features.Apps.Leftovers;
using PcTidy.Features.Apps.Uninstall;
using PcTidy.Safety;

namespace PcTidy.Features.Apps.UI;

/// <summary>
/// The Apps page: list (search, sort, filters) → details → Uninstall (confirm + restore point through IFlowHost)
/// → leftovers (strict matches, quarantined). The page never changes anything itself.
/// </summary>
public sealed partial class AppsViewModel : ObservableObject
{
    public const int FilterAll = 0, FilterDesktop = 1, FilterStore = 2, FilterGames = 3, FilterPreinstalled = 4;
    public const int SortSize = 0, SortLastUsed = 1, SortInstallDate = 2, SortName = 3;

    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _guard;
    private readonly IFlowHost _flow;
    private readonly IFileWalker _walker;
    private readonly AppInventorySources _sources;
    private readonly AppInventory _inventory;
    private List<AppRow> _all = new();
    private CancellationTokenSource? _loadCts;
    private InstalledApp? _leftoversOf;

    public AppsViewModel(IServiceProvider services, AppInventorySources? sources = null)
    {
        _locations = services.Get<IKnownLocations>();
        _guard = services.Get<ISafetyGuard>();
        _flow = services.Get<IFlowHost>();
        _walker = services.GetService(typeof(IFileWalker)) as IFileWalker ?? new ManagedFileWalker();
        _sources = sources ?? AppInventorySources.For(_locations, _walker);
        _inventory = new AppInventory(_sources);
    }

    public ObservableCollection<AppRow> Rows { get; } = new();
    public ObservableCollection<FactRow> Facts { get; } = new();
    public ObservableCollection<LeftoverRow> Leftovers { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private bool _hideMicrosoft;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(UninstallText), nameof(CanUninstall), nameof(UninstallBlockedReason),
        nameof(UninstallWarning), nameof(CanFindLeftovers), nameof(CanOpenLocation))]
    private AppRow? _selected;

    // ---- leftovers view ----
    [ObservableProperty] private bool _showLeftovers;
    [ObservableProperty] private string _leftoverTitle = "";
    [ObservableProperty] private string _leftoverSubtitle = "";
    [ObservableProperty] private string _leftoverTotals = "";

    public bool HasSelection => Selected is not null;
    private UninstallOption? Option => Selected is null ? null : UninstallPlanner.For(Selected.App);
    public string UninstallText => Option?.ButtonText ?? "Uninstall";
    public bool CanUninstall => Option?.IsAvailable == true && GuardReason(Option.Item!) is null;
    public string? UninstallBlockedReason => Option is { } o ? o.BlockedReason ?? (o.Item is { } i ? GuardReason(i) : null) : null;
    public string? UninstallWarning => Option?.Warning;
    /// <summary>On demand for ghost entries; after a real uninstall the leftover view opens by itself.</summary>
    public bool CanFindLeftovers => Selected?.App is { IsOrphan: true, Source: AppSource.Desktop };
    public bool CanOpenLocation => Selected?.App.InstallLocation is { } l && AppInventory.SafeDirectoryExists(l);

    partial void OnSearchTextChanged(string value) => ApplyView();
    partial void OnFilterIndexChanged(int value) => ApplyView();
    partial void OnSortIndexChanged(int value) => ApplyView();
    partial void OnHideMicrosoftChanged(bool value) => ApplyView();
    partial void OnSelectedChanged(AppRow? value) => FillFacts();

    // ================= loading =================

    [RelayCommand]
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        StatusText = "Reading your installed apps…";
        try
        {
            var apps = await _inventory.LoadAsync(cts.Token);
            var now = DateTime.UtcNow;
            _all = apps.Select(a => new AppRow(a, now)).ToList();
            ApplyView();
            IsLoading = false;
            await MeasureAllAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* a newer load replaced this one */ }
        catch (Exception ex)
        {
            StatusText = "PC Tidy couldn't read your apps right now. Try Scan again. (" + ex.Message + ")";
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    /// <summary>Measures each app's folder in the background, biggest reported first; the list updates as it goes.</summary>
    private async Task MeasureAllAsync(CancellationToken ct)
    {
        var queue = _all.Where(r => r.App.InstallLocation is not null && r.App.Source != AppSource.Store)
            .OrderByDescending(r => r.App.ReportedSizeBytes ?? 0).ToList();
        for (int i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            StatusText = $"{CountText()} · measuring sizes ({i + 1} of {queue.Count})";
            queue[i].MeasuredBytes = await _inventory.MeasureAsync(queue[i].App, ct);
            if (ReferenceEquals(Selected, queue[i])) FillFacts();
        }
        if (SortIndex == SortSize) ApplyView();
        StatusText = CountText();
    }

    // ================= list view =================

    private void ApplyView()
    {
        IEnumerable<AppRow> rows = _all;
        rows = FilterIndex switch
        {
            FilterDesktop => rows.Where(r => r.App.Source == AppSource.Desktop),
            FilterStore => rows.Where(r => r.App.Source == AppSource.Store),
            FilterGames => rows.Where(r => r.App.IsGame),
            FilterPreinstalled => rows.Where(r => r.App.Preinstalled is not null),
            _ => rows,
        };
        if (HideMicrosoft)
            rows = rows.Where(r => !(r.App.Publisher ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                   && !(r.App.PackageName ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));
        var q = SearchText.Trim();
        if (q.Length > 0)
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                   || (r.App.Publisher ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase));
        rows = SortIndex switch
        {
            // Unknown values always sort last.
            SortLastUsed => rows.OrderBy(r => r.App.LastUsedUtc is null).ThenBy(r => r.App.LastUsedUtc),
            SortInstallDate => rows.OrderBy(r => r.App.InstallDate is null).ThenByDescending(r => r.App.InstallDate),
            SortName => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderByDescending(r => r.SizeBytes),
        };

        var keep = Selected;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Selected = keep is not null && Rows.Contains(keep) ? keep : null;
        if (!IsLoading) StatusText = CountText();
    }

    private string CountText()
    {
        var known = Rows.Where(r => r.SizeBytes > 0).Sum(r => r.SizeBytes);
        var n = Rows.Count;
        return n == _all.Count
            ? $"{n:N0} app{(n == 1 ? "" : "s")} · {Sizes.Format(known)}"
            : $"{n:N0} of {_all.Count:N0} apps · {Sizes.Format(known)}";
    }

    private void FillFacts()
    {
        Facts.Clear();
        if (Selected is null) return;
        foreach (var (k, v) in AppFacts.Details(Selected.App, Selected.MeasuredBytes, DateTime.UtcNow)) Facts.Add(new FactRow(k, v));
        if (Selected.App.Preinstalled is { } p) Facts.Add(new FactRow("About", p.Reason));
    }

    private string? GuardReason(PlanItem item)
    {
        var verdict = _guard.Check(item);
        return verdict.Allowed ? null : verdict.Reason;
    }

    // ================= uninstall =================

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (Selected?.App is not { } app || Option is not { Item: { } item }) return;
        var plan = new ActionPlan { FeatureId = AppsFeature.Id, Title = UninstallTitle(app), Items = new[] { item } };
        IsBusy = true;
        try
        {
            var run = await _flow.ReviewAndRunAsync(plan, CancellationToken.None);
            if (run is null) return; // cancelled in the confirm dialog
            await AfterUninstallAsync(app, run);
        }
        finally { IsBusy = false; }
    }

    /// <summary>"What would happen" report for the uninstall: nothing is run.</summary>
    [RelayCommand]
    private async Task SimulateAsync()
    {
        if (Selected?.App is not { } app || Option is not { Item: { } item }) return;
        IsBusy = true;
        try { await _flow.SimulateAsync(new ActionPlan { FeatureId = AppsFeature.Id, Title = UninstallTitle(app), Items = new[] { item } }, CancellationToken.None); }
        finally { IsBusy = false; }
    }

    private static string UninstallTitle(InstalledApp app) => app.IsGame ? $"Uninstall {app.Name} in its launcher" : $"Uninstall {app.Name}";

    private async Task AfterUninstallAsync(InstalledApp app, RunRecord run)
    {
        if (app.IsGame)
        {
            _flow.ShowMessage("Finish in the launcher", $"{app.Name} is removed by its launcher. When it's done, press Scan again here.");
            return;
        }
        if (run.Status is not (RunStatus.Completed or RunStatus.CompletedWithSkips) || run.DoneCount == 0) return;

        // The app's folder was a protected "installed app" place; re-detect so the guard sees it's gone.
        (_locations as WindowsKnownLocations)?.Refresh();
        IReadOnlyList<InstalledApp> now;
        try { now = await _inventory.LoadAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the up-to-date list PC Tidy can't tell which folders other apps still use: no leftover scan.
            _flow.ShowMessage("Couldn't check for leftovers", $"PC Tidy couldn't re-read your apps ({ex.Message}). Press Scan again, then try once more.");
            return;
        }
        // Refresh the list in the background (it keeps measuring sizes); the leftovers don't wait for it.
        _ = LoadAsync();

        if (now.Any(a => a.Id == app.Id && !a.IsOrphan))
        {
            _flow.ShowMessage("Still installed", $"{app.Name} is still listed. Its uninstaller may have been cancelled, or it finishes after a restart.");
            return;
        }
        if (app.Source == AppSource.Desktop) await ShowLeftoversAsync(app, now);
    }

    // ================= leftovers =================

    [RelayCommand]
    private async Task FindLeftoversAsync()
    {
        if (Selected?.App is not { } app) return;
        await ShowLeftoversAsync(app, _all.Select(r => r.App).Where(a => a.Id != app.Id).ToList());
    }

    private async Task ShowLeftoversAsync(InstalledApp app, IReadOnlyList<InstalledApp> stillInstalled)
    {
        IsBusy = true;
        try
        {
            var scanner = new LeftoverScanner(_locations, _guard, _sources.Registry, _walker);
            var findings = await Task.Run(() => scanner.Scan(app, stillInstalled, CancellationToken.None));
            _leftoversOf = app;
            Leftovers.Clear();
            foreach (var f in findings)
            {
                var row = new LeftoverRow(LeftoverItems.ToScanItem(f, app.Name), f.Confidence);
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LeftoverRow.IsSelected)) UpdateLeftoverTotals(); };
                Leftovers.Add(row);
            }
            LeftoverTitle = $"Leftovers of {app.Name}";
            LeftoverSubtitle = findings.Count == 0
                ? "Nothing left behind that PC Tidy could match by exact name. All clean."
                : "Exact-name matches only. Only very good matches are ticked. Everything goes to quarantine, so you can put it back.";
            UpdateLeftoverTotals();
            ShowLeftovers = true;
        }
        finally { IsBusy = false; }
    }

    private void UpdateLeftoverTotals()
    {
        var picked = Leftovers.Where(l => l.IsSelected && l.IsEnabled).ToList();
        LeftoverTotals = picked.Count == 0
            ? "Nothing selected"
            : $"{picked.Count} item{(picked.Count == 1 ? "" : "s")} · {Sizes.Format(picked.Sum(p => p.Item.SizeBytes))} to quarantine";
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveLeftovers() => Leftovers.Any(l => l.IsSelected && l.IsEnabled);

    [RelayCommand(CanExecute = nameof(CanRemoveLeftovers))]
    private async Task RemoveLeftoversAsync()
    {
        if (_leftoversOf is not { } app) return;
        var picked = Leftovers.Where(l => l.IsSelected && l.IsEnabled).Select(l => l.Item).ToList();
        var plan = PlanBuilder.FromSelection(AppsFeature.Id, $"Leftovers of {app.Name}", picked);
        if (plan.Items.Count == 0) return;
        IsBusy = true;
        try
        {
            var run = await _flow.ReviewAndRunAsync(plan, CancellationToken.None);
            if (run is not null) CloseLeftovers();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CloseLeftovers()
    {
        ShowLeftovers = false;
        Leftovers.Clear();
        _leftoversOf = null;
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected?.App.InstallLocation is { } l && AppInventory.SafeDirectoryExists(l)) _flow.OpenInExplorer(l);
    }

    [RelayCommand]
    private void OpenLeftover(LeftoverRow? row)
    {
        if (row?.Item.Detail is { } p && AppInventory.SafeDirectoryExists(p)) _flow.OpenInExplorer(p);
    }

    public void Cancel() => _loadCts?.Cancel();
}
