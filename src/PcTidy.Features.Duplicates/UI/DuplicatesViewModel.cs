using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Duplicates.UI;

public enum PageState { Ready, Scanning, Results }

/// <summary>
/// State behind the Duplicates page. Scanning only reads; the plan is built from the ticked rows and handed to the
/// shell's flow host (confirm → run → Done card with Undo). Numbers shown are the same items the plan contains.
/// </summary>
public sealed class DuplicatesViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly DuplicatesModule _module;
    private CancellationTokenSource? _cts;
    private PageState _state = PageState.Ready;
    private string _progressText = "";
    private double? _progress;
    private string _selectionText = "Nothing selected";
    private string _selectionCaption = "";
    private bool _hasSelection;
    private string _compareStatus = "";
    private string? _masterFolder;
    private string? _otherFolder;
    private int _depthIndex;

    public DuplicatesViewModel(IServiceProvider services, DuplicatesModule module)
    {
        _services = services;
        _module = module;
        var settings = Settings;
        _depthIndex = settings?.DefaultDepth == ScanDepth.Deep ? 1 : 0;
        var dup = settings is null ? new DuplicatesSettings() : DuplicatesSettings.From(settings);
        _masterFolder = dup.LastMasterFolder;
        _otherFolder = dup.LastOtherFolder;
        foreach (var f in dup.ReferenceFolders) ReferenceFolders.Add(new ReferenceFolderRow(f, f));
    }

    private AppSettings? Settings => _services.GetService(typeof(AppSettings)) as AppSettings;
    public IFlowHost? Host => _services.GetService(typeof(IFlowHost)) as IFlowHost;

    public ObservableCollection<GroupRow> Groups { get; } = new();
    public ObservableCollection<GroupRow> FolderGroups { get; } = new();
    public ObservableCollection<SimilarGroupRow> Similar { get; } = new();
    public ObservableCollection<SimpleRow> EmptyFolders { get; } = new();
    public ObservableCollection<SimpleRow> ZeroByte { get; } = new();
    public ObservableCollection<SimpleRow> Shortcuts { get; } = new();
    public ObservableCollection<SimpleRow> MasterMatches { get; } = new();
    /// <summary>Huge files that are probably the same (samples only) and the ones "Check fully" confirmed.</summary>
    public ObservableCollection<GroupRow> Probable { get; } = new();
    public ObservableCollection<ExtensionRow> Extensions { get; } = new();
    public ObservableCollection<ReferenceFolderRow> ReferenceFolders { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    public DuplicatesReport? Report { get; private set; }

    public PageState State { get => _state; private set => SetProperty(ref _state, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public double? Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }
    public string SelectionCaption { get => _selectionCaption; private set => SetProperty(ref _selectionCaption, value); }
    public bool HasSelection { get => _hasSelection; private set => SetProperty(ref _hasSelection, value); }
    public string CompareStatus { get => _compareStatus; private set => SetProperty(ref _compareStatus, value); }
    public int DepthIndex { get => _depthIndex; set => SetProperty(ref _depthIndex, value); }
    public ScanDepth Depth => DepthIndex == 1 ? ScanDepth.Deep : ScanDepth.Quick;

    public string? MasterFolder { get => _masterFolder; set { if (SetProperty(ref _masterFolder, value)) SaveSettings(); } }
    public string? OtherFolder { get => _otherFolder; set { if (SetProperty(ref _otherFolder, value)) SaveSettings(); } }

    public string ExactTabTitle => $"Duplicates ({Groups.Count:N0})";
    public string FolderTabTitle => $"Folders ({FolderGroups.Count:N0})";
    public string SimilarTabTitle => $"Look-alike pictures ({Similar.Count:N0})";
    public string ProbableTabTitle => $"Huge files ({Probable.Count:N0})";
    public string ExtensionTabTitle => $"Wrong file type ({Extensions.Count:N0})";
    public string JunkTabTitle => $"Empty and broken ({EmptyFolders.Count + ZeroByte.Count + Shortcuts.Count:N0})";

    public string Summary => Report is null ? ""
        : $"{Report.Stats.FilesSeen:N0} files looked at in {Report.Stats.Elapsed.TotalSeconds:0.#} s. "
          + (Report.ExactGroups.Count + Report.FolderGroups.Count == 0 ? "No duplicates found." : $"Duplicates use up to {Sizes.Format(Report.ReclaimableBytes)} you could free.");

    // ------------------------------------------------------------------ scanning

    public async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        State = PageState.Scanning;
        ProgressText = "Looking through your folders…";
        Progress = null;
        try
        {
            var ctx = ScanRunner.CreateContext(_services, Depth);
            var progress = new Progress<ScanProgress>(p => { ProgressText = p.Message; Progress = p.Fraction; });
            StopChecks();
            var engine = new DuplicatesEngine(_module.CacheFor(ctx.Settings.DataRoot));
            var report = await engine.ScanAsync(ctx, new DuplicateScanOptions(), progress, ct);
            Load(report);
            State = PageState.Results;
            SaveCacheSoon();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            State = Report is null ? PageState.Ready : PageState.Results;
        }
        catch (Exception ex)
        {
            // Callers fire and forget this task, so nothing may escape: anything unexpected still ends the scan with a
            // friendly message instead of leaving the page on "Scanning" for good.
            Host?.ShowMessage("Duplicates", $"The scan couldn't finish ({ex.Message}). Nothing was changed.", isError: true);
            State = Report is null ? PageState.Ready : PageState.Results;
        }
    }

    /// <summary>Saves new hashes in the background, so the next session's scan is quick too (never throws).</summary>
    private void SaveCacheSoon() => _ = Task.Run(() =>
    {
        try { _module.SaveCache(); }
        catch (Exception) { /* the cache is only a speed-up: if it can't be saved, the next scan hashes again */ }
    });

    public void CancelScan()
    {
        _cts?.Cancel();
        StopChecks();
    }

    /// <summary>Stops any running "Check fully" (a new scan replaces those groups anyway).</summary>
    private void StopChecks()
    {
        _checkCts?.Cancel();
        _checkCts = null;
    }

    /// <summary>Fills the lists from a finished scan (also used by tests and screenshots).</summary>
    public void Load(DuplicatesReport report)
    {
        Report = report;
        var f = report.FriendlyPath;
        Groups.Clear(); FolderGroups.Clear(); Similar.Clear(); EmptyFolders.Clear(); ZeroByte.Clear(); Shortcuts.Clear(); Notes.Clear();
        Probable.Clear(); Extensions.Clear();
        foreach (var g in report.ProbableGroups) Probable.Add(new GroupRow(this, g, f));
        foreach (var w in report.WrongExtensions) Extensions.Add(new ExtensionRow(this, w, f));
        foreach (var g in report.ExactGroups) Groups.Add(new GroupRow(this, g, f));
        foreach (var g in report.FolderGroups) FolderGroups.Add(new GroupRow(this, g, f));
        foreach (var g in report.SimilarImages) Similar.Add(new SimilarGroupRow(this, g, f));
        foreach (var j in report.EmptyFolders) EmptyFolders.Add(Junk(j, f));
        foreach (var j in report.ZeroByteFiles) ZeroByte.Add(Junk(j, f));
        foreach (var j in report.BrokenShortcuts) Shortcuts.Add(Junk(j, f));
        foreach (var n in report.Notes) Notes.Add(n);
        OnPropertyChanged(nameof(ExactTabTitle)); OnPropertyChanged(nameof(FolderTabTitle));
        OnPropertyChanged(nameof(SimilarTabTitle)); OnPropertyChanged(nameof(JunkTabTitle)); OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ProbableTabTitle)); OnPropertyChanged(nameof(ExtensionTabTitle));
        SelectionChanged();
    }

    private SimpleRow Junk(JunkItem j, Func<string, string> f)
        => new(this, DuplicatePlanner.Name(j.Path), f(DuplicatePlanner.Folder(j.Path)), j.Reason, j.Size, j.RemovalBlockedReason, DuplicatePlanner.ForJunk(j, f));

    // ------------------------------------------------------------------ selection

    private IEnumerable<IRemovableRow> AllRemovable()
        => Groups.SelectMany(g => g.Copies).Cast<IRemovableRow>()
            .Concat(FolderGroups.SelectMany(g => g.Copies))
            .Concat(Similar.SelectMany(g => g.Images))
            .Concat(EmptyFolders).Concat(ZeroByte).Concat(Shortcuts).Concat(MasterMatches)
            .Concat(Probable.SelectMany(g => g.Copies)).Concat(Extensions);

    /// <summary>The ticked plan items, each path once (a file ticked in two places is removed once).</summary>
    public IReadOnlyList<PlanItem> SelectedItems()
        => AllRemovable().Where(r => r.IsChecked && r.CanRemove).Select(r => r.Action).OfType<PlanItem>()
            .GroupBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public void SelectionChanged()
    {
        var items = SelectedItems();
        HasSelection = items.Count > 0;
        // Renames free nothing: only what goes to quarantine counts towards the size.
        SelectionText = items.Count == 0 ? "Nothing selected" : $"Selected {items.Count:N0} item{PlanFacts.S(items.Count)} · {Sizes.Format(items.Where(PlanFacts.IsQuarantine).Sum(i => i.ExpectedSize))}";
        SelectionCaption = items.Count == 0
            ? "Tick the copies you don't need. One copy of every file is always kept."
            : "Everything goes to quarantine first, so you can undo it. Each copy is checked against the kept one right before it moves.";
    }

    /// <summary>Back to the rules' choice: only the clear cases ticked.</summary>
    public void SelectClearCasesOnly()
    {
        foreach (var g in Groups) g.SelectClearCases();
        foreach (var g in FolderGroups) g.SetAll(false);
        foreach (var g in Probable) g.SetAll(false);
        foreach (var r in Extensions) r.SetChecked(false);
        foreach (var r in AllRemovable().OfType<ImageRow>()) r.IsChecked = false;
        foreach (var r in EmptyFolders.Concat(ZeroByte).Concat(Shortcuts).Concat(MasterMatches)) r.SetChecked(false);
        SelectionChanged();
    }

    public void SetAll(IEnumerable<SimpleRow> rows, bool value)
    {
        foreach (var r in rows) r.SetChecked(value);
        SelectionChanged();
    }

    // ------------------------------------------------------------------ huge files: "Check fully"

    private CancellationTokenSource? _checkCts;

    /// <summary>A copy of a probable group was ticked: run the full check (once) before anything can be removed.</summary>
    internal void RequestFullCheck(GroupRow row)
    {
        if (row.CanCheckFully) _ = CheckFullyAsync(row);
    }

    /// <summary>
    /// "Check fully": hashes every copy of a sampled-only group completely, in the background. Copies whose full hash
    /// matches become a normal duplicate group (plan items then carry that FULL hash as <c>verifyHash</c>); ticks made
    /// while it ran are kept on those copies only. Nothing is ever removed on a sampled match.
    /// </summary>
    public async Task CheckFullyAsync(GroupRow row)
    {
        if (!row.CanCheckFully || Settings is not { } settings) return;
        _checkCts ??= new CancellationTokenSource();
        var ct = _checkCts.Token;
        row.IsChecking = true;
        row.CheckStatus = "Checking fully…";
        try
        {
            var progress = new Progress<double>(p => row.CheckStatus = $"Checking fully… {p:P0}");
            var result = await ProbableVerifier.CheckFullyAsync(row.Group, _module.CacheFor(settings.DataRoot), progress, ct);
            ApplyFullCheck(row, result);
            SaveCacheSoon();
        }
        catch (OperationCanceledException)
        {
            row.CheckStatus = "Check stopped. Nothing was changed.";
        }
        catch (Exception ex) // fire-and-forget caller: nothing may escape
        {
            row.CheckStatus = $"Couldn't read the files ({ex.Message}). Nothing was changed.";
        }
        finally
        {
            row.IsChecking = false;
            SelectionChanged();
        }
    }

    /// <summary>Replaces a probable group by what the full check proved (also used by tests).</summary>
    public void ApplyFullCheck(GroupRow row, ProbableCheck result)
    {
        int index = Probable.IndexOf(row);
        if (index < 0) return;
        var ticked = row.TickedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Probable.RemoveAt(index);
        foreach (var g in result.Confirmed)
        {
            var confirmed = new GroupRow(this, g, row.Friendly);
            foreach (var c in confirmed.Copies.Where(c => ticked.Contains(c.Path))) c.SetState(c.Copy, c.IsKept, ticked: true);
            Probable.Insert(index++, confirmed);
        }
        var f = Report?.FriendlyPath ?? (s => s);
        if (result.Different.Count > 0)
            Notes.Add($"Checked fully: {string.Join(", ", result.Different.Select(p => f(p)))} {(result.Different.Count == 1 ? "is" : "are")} not the same after all, so nothing is offered for {(result.Different.Count == 1 ? "it" : "them")}.");
        if (result.Skipped.Count > 0)
            Notes.Add($"{string.Join(", ", result.Skipped.Select(p => f(p)))} changed since the scan or couldn't be read, so {(result.Skipped.Count == 1 ? "it was" : "they were")} left alone. Scan again to compare.");
        OnPropertyChanged(nameof(ProbableTabTitle));
        SelectionChanged();
    }

    // ------------------------------------------------------------------ run
    public async Task RunAsync(bool simulate)
    {
        // Fire-and-forget caller (the view's buttons): anything unexpected becomes a friendly message, never a lost task.
        try { await RunCoreAsync(simulate); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Host?.ShowMessage("Duplicates", $"That didn't work ({ex.Message}).", isError: true);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunCoreAsync(bool simulate)
    {
        var items = SelectedItems();
        if (items.Count == 0 || Host is not { } host) return;
        var plan = new ActionPlan { FeatureId = DuplicatesModule.FeatureId, Title = "Duplicates and junk", Items = PlanBuilder.Normalize(items) };
        bool ran;
        // The bar, the confirm dialog and the run all say "Remove", not the generic "Clean".
        if ((_services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? host as IShellFlowHost) is { } shell)
        {
            var options = new FlowOptions { Title = plan.Title, ShowResult = true, PrimaryVerb = PrimaryVerb(plan.Items) };
            if (simulate)
            {
                await shell.SimulateAsync(new[] { plan }, options, CancellationToken.None);
                return;
            }
            ran = (await shell.ReviewAndRunAsync(new[] { plan }, options, CancellationToken.None)).Kind == FlowOutcomeKind.Ran;
        }
        else
        {
            if (simulate)
            {
                await host.SimulateAsync(plan, CancellationToken.None);
                return;
            }
            ran = await host.ReviewAndRunAsync(plan, CancellationToken.None) is not null;
        }
        // What's left is easiest to see with a fresh look (hashes are cached, so it's quick).
        if (ran) await ScanAsync();
    }

    /// <summary>"Remove 6.3 GB": the shared wording says "Clean", which reads oddly for copies of your own files.</summary>
    public static string PrimaryVerb(IReadOnlyList<PlanItem> items)
        => PlanFacts.PrimaryAction(items) is var text && text.StartsWith("Clean", StringComparison.Ordinal) ? "Remove" + text["Clean".Length..] : text;

    // ------------------------------------------------------------------ compare to master

    public async Task CompareAsync()
    {
        if (string.IsNullOrWhiteSpace(MasterFolder) || string.IsNullOrWhiteSpace(OtherFolder))
        {
            CompareStatus = "Pick a master folder and a folder to check first.";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CompareStatus = "Comparing…";
        MasterMatches.Clear();
        try
        {
            var ctx = ScanRunner.CreateContext(_services, ScanDepth.Deep);
            var engine = new DuplicatesEngine(_module.CacheFor(ctx.Settings.DataRoot));
            var progress = new Progress<ScanProgress>(p => CompareStatus = p.Message);
            var result = await engine.CompareToMasterAsync(ctx, MasterFolder!, OtherFolder!, progress, _cts.Token);
            var f = Report?.FriendlyPath ?? (s => s);
            foreach (var m in result.Matches)
                MasterMatches.Add(new SimpleRow(this, DuplicatePlanner.Name(m.Copy.Path), f(DuplicatePlanner.Folder(m.Copy.Path)),
                    $"Already in your master folder as {f(m.MasterPath)}.", m.Copy.ReclaimableBytes, DuplicatePlanner.DisabledReasonFor(m.Copy),
                    DuplicatePlanner.ForMasterMatch(m, f)));
            CompareStatus = result.Notes.Count > 0 && result.Matches.Count == 0 ? string.Join(" ", result.Notes)
                : $"{result.Matches.Count:N0} file{PlanFacts.S(result.Matches.Count)} already in the master folder ({Sizes.Format(result.ReclaimableBytes)}). "
                  + $"{result.OnlyInOther:N0} file{PlanFacts.S(result.OnlyInOther)} only in the other folder. " + string.Join(" ", result.Notes);
            SelectionChanged();
            SaveCacheSoon();
        }
        catch (OperationCanceledException) { CompareStatus = "Stopped."; }
        catch (Exception ex) // fire-and-forget caller: nothing may escape
        {
            CompareStatus = $"Couldn't compare those folders ({ex.Message}).";
        }
    }

    // ------------------------------------------------------------------ reference folders

    public void AddReferenceFolder(string folder)
    {
        if (ReferenceFolders.Any(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase))) return;
        ReferenceFolders.Add(new ReferenceFolderRow(folder, folder));
        SaveSettings();
    }

    public void RemoveReferenceFolder(ReferenceFolderRow row)
    {
        ReferenceFolders.Remove(row);
        SaveSettings();
    }

    /// <summary>Stores choices in the settings object; the shell writes settings.json.</summary>
    private void SaveSettings()
    {
        if (Settings is not { } s) return;
        var dup = DuplicatesSettings.From(s);
        dup.ReferenceFolders = ReferenceFolders.Select(r => r.Path).ToList();
        dup.LastMasterFolder = MasterFolder;
        dup.LastOtherFolder = OtherFolder;
        dup.Store(s);
    }
}
