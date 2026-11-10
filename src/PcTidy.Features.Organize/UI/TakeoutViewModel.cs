using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Takeout;

namespace PcTidy.Features.Organize.UI;

/// <summary>
/// The "Google Takeout" tab: pick the unpacked export and where the photos go → scan (read-only) → preview by year or
/// album with ticks → the shell's confirm → run → Undo from History. Nothing moves before the confirm.
/// "Sort into" starts in Pictures only when Pictures is a local folder; in OneDrive (common on Windows 11) the user
/// picks one, because photos can't go into a cloud folder without their OK and the tab would otherwise sort nothing.
/// </summary>
public sealed partial class TakeoutViewModel : ObservableObject
{
    private readonly IFlowHost _flow;
    private readonly TakeoutPlanner _planner;
    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _safety;
    private readonly AppSettings _appSettings;
    private readonly bool _picturesInCloud;
    private CancellationTokenSource? _cts;
    private TakeoutPreview? _preview;

    public TakeoutViewModel(IFlowHost flow, TakeoutPlanner planner, IKnownLocations locations, ISafetyGuard safety, AppSettings appSettings)
    {
        _flow = flow;
        _planner = planner;
        _locations = locations;
        _safety = safety;
        _appSettings = appSettings;
        _destinationFolder = TakeoutPlanner.SuggestedDestination(locations, safety, appSettings);
        _picturesInCloud = _destinationFolder is null;
        Clear();
    }

    public ObservableCollection<ActionGroupViewModel> Groups { get; } = new();
    /// <summary>Group headers and rows in one flat list, so a big export still virtualizes.</summary>
    [ObservableProperty] private IReadOnlyList<object> _rows = Array.Empty<object>();
    [ObservableProperty] private IReadOnlyList<SkipRow> _skippedRows = Array.Empty<SkipRow>();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private string? _sourceFolder;
    [ObservableProperty] private string? _destinationFolder;
    [ObservableProperty] private bool _keepAlbums;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _summaryDetail = "";
    [ObservableProperty] private string _sortButtonText = "Sort photos";
    [ObservableProperty] private string _skippedTitle = "";
    /// <summary>1 Scan, 2 Preview, 3 Confirm, 4 Done (the shared step bar).</summary>
    [ObservableProperty] private int _step = 1;

    public string SourceText => SourceFolder ?? "Not chosen yet";
    public string DestinationText => DestinationFolder ?? "Not chosen yet";

    /// <summary>Why the photos can't be sorted into "Sort into" yet, or null when they can.</summary>
    public string? DestinationProblem
        => DestinationFolder is null
            ? _picturesInCloud
                ? "Your Pictures folder is synced by OneDrive, so PC Tidy doesn't pick it for you. Choose a folder on this PC for the sorted photos."
                : "Choose where the sorted photos go."
            : TakeoutPlanner.DestinationNeedsPermission(DestinationFolder, _locations, _safety, _appSettings)
                ? "This folder is in OneDrive. PC Tidy only moves files into OneDrive with your OK: pick a folder on this PC, or allow this one in Settings (OneDrive folders PC Tidy may organize)."
                : null;

    /// <summary>The empty preview's title and text: what to do next, never a blank card.</summary>
    // "Sort into" comes first when it can't be used: the page can't pick one, and the card's button chooses it.
    public string EmptyTitle => DestinationProblem is not null ? "Choose where the photos go" : SourceFolder is null ? "Pick your unpacked Takeout folder" : "See where every photo would go first";
    public string EmptyMessage => DestinationProblem
        ?? "Scanning only reads. Edited copies are kept next to their originals; photos Google exported twice are offered for quarantine, unticked.";

    /// <summary>Captions under the shared step bar ("a|b|c|d").</summary>
    public string StepCaptions => string.Join("|",
        Step >= 2 && _preview is { } p ? $"{p.Moves.Count():N0} photos found" : "Looks only",
        "Check the moves",
        Step >= 4 ? "Sorted" : "Nothing moved yet",
        "+ Undo");

    partial void OnSourceFolderChanged(string? value) { OnPropertyChanged(nameof(SourceText)); Clear(); }
    partial void OnDestinationFolderChanged(string? value) { OnPropertyChanged(nameof(DestinationText)); Clear(); }
    partial void OnKeepAlbumsChanged(bool value) => Clear();
    partial void OnStepChanged(int value) => OnPropertyChanged(nameof(StepCaptions));
    partial void OnIsScanningChanged(bool value) { ScanCommand.NotifyCanExecuteChanged(); SortCommand.NotifyCanExecuteChanged(); SimulateCommand.NotifyCanExecuteChanged(); }

    [RelayCommand]
    private void PickSource()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder you unpacked your Google Takeout download into" };
        if (dialog.ShowDialog() == true) SourceFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void PickDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where the sorted photos go" };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    private void Clear()
    {
        _preview = null;
        HasPreview = false;
        Step = 1;
        Groups.Clear();
        Rows = Array.Empty<object>();
        SkippedRows = Array.Empty<SkipRow>();
        Notes.Clear();
        Summary = SourceFolder is null ? "Pick your unpacked Takeout folder to start"
            : DestinationProblem is not null ? "Choose where the sorted photos go"
            : "Scan to see where every photo would go";
        SummaryDetail = "Nothing moves until you confirm.";
        SortButtonText = "Sort photos";
        OnPropertyChanged(nameof(DestinationProblem));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        ScanCommand.NotifyCanExecuteChanged();
        SortCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Scanning needs both folders, and a destination photos may actually go into (never a silent "Sort 0").</summary>
    private bool CanScan() => SourceFolder is not null && DestinationFolder is not null && DestinationProblem is null && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (SourceFolder is not { } source || DestinationFolder is not { } destination) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressText = "Reading your Takeout folder…";
        try
        {
            var preview = await _planner.PlanAsync(new TakeoutRequest { SourceFolder = source, DestinationFolder = destination, KeepAlbums = KeepAlbums },
                new Progress<ScanProgress>(p => ProgressText = p.Message), _cts.Token);
            Show(preview);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _flow.ShowMessage("Couldn't read the Takeout folder", $"PC Tidy couldn't look at that folder: {ex.Message}. Nothing was changed.", isError: true);
        }
        finally
        {
            IsScanning = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    /// <summary>Fills the preview from a finished scan (also used by tests).</summary>
    public void Show(TakeoutPreview preview)
    {
        Clear();
        _preview = preview;
        var roots = new[] { preview.DestinationFolder, preview.SourceFolder };
        var rows = new List<object>();
        foreach (var g in preview.Actions.GroupBy(a => a.Group).OrderBy(g => g.Key == "Duplicates" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var description = g.Key == "Duplicates"
                ? "Google exported these twice (album and year folder). Your call: quarantine the extra copy."
                : g.Key.StartsWith(TakeoutPlanner.AlbumsFolder + "\\", StringComparison.Ordinal) ? "Album, kept together as its own folder." : "By the month each photo was taken.";
            var group = new ActionGroupViewModel(g.Key, description, g.Select(a => new ActionRowViewModel(a, roots, UpdateTotals)));
            Groups.Add(group);
            rows.Add(group);
            rows.AddRange(group.Items);
        }
        Rows = rows;
        SkippedRows = preview.Skipped.Select(s => new SkipRow(s.Name, s.Reason, PreviewTree.IconFor(s.Name))).ToList();
        SkippedTitle = StayTitle(preview.Skipped);
        foreach (var n in preview.Notes) Notes.Add(n);
        HasPreview = true;
        Step = 2;
        UpdateTotals();
    }

    /// <summary>"15 photos and 2 info files stay where they are": says what stays, not just how many "items".</summary>
    public static string StayTitle(IReadOnlyCollection<SkippedEntry> skipped)
    {
        int info = skipped.Count(s => !s.IsDirectory && s.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        int media = skipped.Count(s => !s.IsDirectory && TakeoutLayout.IsMedia(s.Name));
        int other = skipped.Count - info - media;
        var parts = new List<string>();
        if (media > 0) parts.Add(Count(media, "photo"));
        if (info > 0) parts.Add(Count(info, "info file"));
        if (other > 0) parts.Add(Count(other, parts.Count > 0 ? "other item" : "item"));
        if (parts.Count == 0) return "";
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return list + (skipped.Count == 1 ? " stays where it is" : " stay where they are");
    }

    private IEnumerable<PlannedAction> Selected() => Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Action);

    private void UpdateTotals()
    {
        var selected = Selected().ToList();
        int photos = selected.Count(a => a.Kind == PlannedActionKind.Move);
        // Each sorted photo's item is: missing folders, the photo, then its info file when it has one.
        int sidecars = selected.Where(a => a.Kind == PlannedActionKind.Move).Sum(a => a.Items.Count(i => i.Kind == ActionKind.MovePath) - 1);
        int stay = SkippedRows.Count;
        if (_preview is { } p && !p.Actions.Any())
        {
            // Nothing can be sorted: say why in the bar instead of offering "Sort 0 photos".
            Summary = "No photos can be sorted from this folder";
            SummaryDetail = stay == 0
                ? "No photos were found in the Google Photos part of this export."
                : $"{SkippedTitle}. Each one says why in the list.";
        }
        else
        {
            Summary = selected.Count == 0 ? "Nothing ticked" : $"{Count(photos, "photo")} will be sorted · {Sizes.Format(selected.Sum(a => a.Size))}";
            var along = sidecars == 1 ? "1 info file moves along with its photo" : $"{sidecars:N0} info files move along with their photos";
            SummaryDetail = $"{along} · {stay:N0} stay where they are · put back any time from History";
        }
        SortButtonText = selected.Count == 0 ? "Sort photos" : photos == selected.Count ? $"Sort {Count(photos, "photo")}" : $"Tidy {Count(selected.Count, "item")}";
        SortCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n:N0} {what}s";

    private bool CanRun() => HasPreview && !IsScanning && Selected().Any();

    /// <summary>ONE plan with every ticked photo, its info file and the folders they need (folders first).</summary>
    public ActionPlan BuildPlan() => PlanAssembler.Build("Sort Google Takeout photos", Selected());

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SortAsync()
    {
        Step = 3;
        var run = await _flow.ReviewAndRunAsync(BuildPlan(), CancellationToken.None);
        if (run is null) { Step = 2; return; }
        OrganizeLedger.RecordInto(_appSettings, run, DateTime.UtcNow);
        await ScanAsync();
        Step = 4;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SimulateAsync() => await _flow.SimulateAsync(BuildPlan(), CancellationToken.None);
}
