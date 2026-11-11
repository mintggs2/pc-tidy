using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Stats;
using PcTidy.Features.Measure.Storage;
using PcTidy.UI.Controls;
using PcTidy.UI.Flow;
using RunSummary = PcTidy.Features.Measure.Compare.RunSummary;

namespace PcTidy.Features.Measure.UI;

public enum RunSet { Before = 0, After = 1, BeforeAgain = 2 }

/// <summary>A label and a value in a Before / After column.</summary>
public sealed record StatLine(string Label, string Value);

/// <summary>One imported run in Step 2.</summary>
public sealed partial class RunRow : ObservableObject
{
    private readonly RecordingFile? _file;
    private readonly Action<RunRow> _changed;
    private readonly bool _ready;

    /// <summary>From an imported file; <paramref name="processIndex"/> picks the process when the file has several.</summary>
    public RunRow(RecordingFile file, int processIndex, Action<RunRow> changed, Action<RunRow> remove)
    {
        _file = file;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
        ProcessChoices = file.RunsAreSeparate
            ? Array.Empty<string>()
            : file.Processes.Select(p => $"{p.ProcessName} ({p.PresentMs.Length:N0} frames)").ToList();
        _selectedProcessIndex = Math.Clamp(processIndex, 0, file.Processes.Count - 1);
        Summary = RunSummary.From(file.FileName, file.Format, file.Processes[_selectedProcessIndex]);
        _ready = true;
    }

    /// <summary>From a saved comparison (numbers only, no file).</summary>
    public RunRow(RunSummary summary, Action<RunRow> changed, Action<RunRow> remove)
    {
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
        ProcessChoices = Array.Empty<string>();
        Summary = summary;
        _ready = true;
    }

    [ObservableProperty] private RunSummary _summary;
    [ObservableProperty] private int _selectedProcessIndex;

    public IReadOnlyList<string> ProcessChoices { get; }
    public bool HasProcessChoice => ProcessChoices.Count > 1;
    public IRelayCommand RemoveCommand { get; }

    public string FileName => Summary.FileName;
    public string ProcessLine => $"{Summary.ProcessName} · {RecordingImporter.LabelFor(Summary.Format)}";
    /// <summary>The timing the page compares on (the verdict's): set by the page for every run after each change.</summary>
    public FrameTimeBasis Basis { get; private set; } = FrameTimeBasis.Presents;
    /// <summary>This run's numbers on the same basis as the verdict and the columns, with that basis named.</summary>
    public string StatsLine => Format(Summary.StatsFor(Basis), Basis);
    public string NotesLine => string.Join(" ", Summary.Notes);
    public bool HasNotes => Summary.Notes.Count > 0;
    public string RemoveName => "Remove run " + FileName;
    public string ProcessPickerName => "Process to use from " + FileName;

    partial void OnSelectedProcessIndexChanged(int value)
    {
        if (_file is null || value < 0 || value >= _file.Processes.Count) return;
        Summary = RunSummary.From(_file.FileName, _file.Format, _file.Processes[value]);
    }

    partial void OnSummaryChanged(RunSummary value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ProcessLine));
        OnPropertyChanged(nameof(StatsLine));
        OnPropertyChanged(nameof(NotesLine));
        OnPropertyChanged(nameof(HasNotes));
        if (_ready) _changed(this);
    }

    internal void UseBasis(FrameTimeBasis basis)
    {
        if (Basis == basis) return;
        Basis = basis;
        OnPropertyChanged(nameof(Basis));
        OnPropertyChanged(nameof(StatsLine));
    }

    internal static string Format(RunStats s, FrameTimeBasis basis)
    {
        var inv = CultureInfo.InvariantCulture;
        var low01 = s.PointOnePercentLowIsReliable ? $" · 0.1% low {s.PointOnePercentLowFps.ToString("0.0", inv)}" : "";
        return $"{s.AverageFps.ToString("0.0", inv)} FPS average · 1% low {s.OnePercentLowFps.ToString("0.0", inv)}{low01} · " +
               $"P99 {s.P99FrameMs.ToString("0.0", inv)} ms · {s.Stutters:N0} stutters · {s.Seconds.ToString("0", inv)} s · {s.Frames:N0} frames" +
               (basis == FrameTimeBasis.Display ? " (on-screen timing)" : " (Present timing)");
    }
}

/// <summary>One condition row (checkbox + free text).</summary>
public sealed partial class ConditionRow : ObservableObject
{
    private readonly Action _changed;

    public ConditionRow(ConditionEntry entry, string hint, Action changed)
    {
        Entry = entry;
        Hint = hint;
        _changed = changed;
        _same = entry.SameForAllRuns;
        _note = entry.Note;
    }

    public ConditionEntry Entry { get; }
    public string Label => Entry.Label;
    public string Hint { get; }
    public string CheckName => Entry.Label + ": same for every run";
    public string NoteName => Entry.Label + ": notes";

    [ObservableProperty] private bool _same;
    [ObservableProperty] private string _note;

    partial void OnSameChanged(bool value) { Entry.SameForAllRuns = value; _changed(); }
    partial void OnNoteChanged(string value) { Entry.Note = (value ?? "").Length > 200 ? value![..200] : value ?? ""; _changed(); }
}

/// <summary>A saved comparison in the list.</summary>
public sealed record SavedRow(SavedComparison Comparison, string Title, string Line, string OpenName);

/// <summary>
/// The Measure page's state. Reads the files the user picks or drops (on a background thread), keeps the runs in memory,
/// compares on every change, and saves comparisons as JSON through <see cref="ComparisonStore"/>. Changes nothing on the PC:
/// the only plan it ever builds is a one-step OpenExternal for a tool's web page when the user clicks the link.
/// </summary>
public sealed partial class MeasureViewModel : ObservableObject
{
    public const int MaxRunsPerSet = 10;
    public const int MaxFilesPerImport = 20;

    private readonly IServiceProvider _services;
    private readonly ComparisonStore _store;
    private readonly ImportLimits _limits;
    private string? _loadedId;

    public MeasureViewModel(IServiceProvider services, ComparisonStore store, ImportLimits? limits = null)
    {
        _services = services;
        _store = store;
        _limits = limits ?? ImportLimits.Default;
        foreach (var (key, label, hint) in Storage.Conditions.Known)
            Conditions.Add(new ConditionRow(new ConditionEntry { Key = key, Label = label }, hint, Recompare));
        OpenToolCommand = new AsyncRelayCommand<MeasureText.Tool?>(OpenToolAsync);
        OpenHistoryCommand = new RelayCommand(OpenHistory);
        SaveCommand = new RelayCommand(Save);
        NewComparisonCommand = new RelayCommand(NewComparison);
        OpenSavedCommand = new RelayCommand<SavedRow?>(OpenSaved);
        RefreshSaved();
        Recompare();
    }

    public ObservableCollection<RunRow> BeforeRuns { get; } = new();
    public ObservableCollection<RunRow> AfterRuns { get; } = new();
    public ObservableCollection<RunRow> AgainRuns { get; } = new();
    public ObservableCollection<ConditionRow> Conditions { get; } = new();
    public ObservableCollection<SavedRow> Saved { get; } = new();

    public IReadOnlyList<MeasureText.Tool> Tools => MeasureText.Tools;
    public IReadOnlyList<string> RecordSteps => MeasureText.RecordSteps;
    public IReadOnlyList<string> DoesNotProve => MeasureText.DoesNotProve;
    public IReadOnlyList<string> Definitions { get; } = new[]
    {
        FrameStats.Definitions.AverageFps, FrameStats.Definitions.OnePercentLow, FrameStats.Definitions.PointOnePercentLow,
        FrameStats.Definitions.P99, FrameStats.Definitions.Stutter, FrameStats.Definitions.Spread, Comparer.RuleText,
    };
    public string FirstVersionNote => Comparer.FirstVersionNote;

    public IAsyncRelayCommand<MeasureText.Tool?> OpenToolCommand { get; }
    public IRelayCommand OpenHistoryCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand NewComparisonCommand { get; }
    public IRelayCommand<SavedRow?> OpenSavedCommand { get; }

    /// <summary>Where imported files go: 0 Before, 1 After, 2 Before again.</summary>
    [ObservableProperty] private int _targetIndex;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _linkedRunId;
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private ComparisonResult _result = Comparer.Compare(Array.Empty<RunSummary>(), Array.Empty<RunSummary>());

    public bool HasLinkedRun => !string.IsNullOrWhiteSpace(LinkedRunId);
    public string LinkedRunText => HasLinkedRun
        ? $"This comparison checks History run {LinkedRunId}. {MeasureText.KeepUndoNote}"
        : "";
    public bool HasImportStatus => ImportStatus.Length > 0;
    /// <summary>Keep / Undo: History opens on the checked run (selected, with its Undo) when there is one.</summary>
    public string HistoryButtonText => HasLinkedRun ? "Open this run in History" : "Open History";
    public bool HasSaved => Saved.Count > 0;
    public bool HasAgain => AgainRuns.Count > 0;

    public string VerdictText => ComparisonResult.ChipText(Result.Verdict);
    public PillTone VerdictTone => Result.Verdict switch
    {
        Verdict.Better => PillTone.Success,
        Verdict.Worse => PillTone.Critical,
        Verdict.TooNoisy => PillTone.Caution,
        _ => PillTone.Neutral,
    };
    public string Headline => Result.Headline;
    public string Suggestion => Result.Suggestion;
    public string? DriftNote => Result.DriftNote;
    public bool HasDriftNote => !string.IsNullOrEmpty(Result.DriftNote);
    /// <summary>The timing everything on the page uses: the verdict, the Before / After columns and each run's line.</summary>
    public FrameTimeBasis Basis => Result.Basis;

    /// <summary>Names the timing the verdict, the columns and the run lines all use (shown once there are runs).</summary>
    public string BasisText => BeforeRuns.Count + AfterRuns.Count + AgainRuns.Count == 0 ? ""
        : Result.Basis == FrameTimeBasis.Display ? "Verdict, columns and run lines all use on-screen timing (the time between frames shown on screen)."
        : "Verdict, columns and run lines all use Present timing (the time between Present calls: frames the game handed over).";

    /// <summary>"On-screen timing" or "Present timing", for the column headers.</summary>
    public string BasisLabel => Result.Basis == FrameTimeBasis.Display ? "On-screen timing" : "Present timing";

    /// <summary>Flags, reasons and unticked conditions, as one list of warnings.</summary>
    public IReadOnlyList<string> Warnings => Result.Flags.Concat(Result.Reasons)
        .Concat(Storage.Conditions.Warnings(Conditions.Select(c => c.Entry))).ToList();
    public bool HasWarnings => Warnings.Count > 0;

    public IReadOnlyList<StatLine> BeforeColumn => Column(BeforeRuns, Result.Basis, Result.Average?.BeforeSpreadPercent);
    public IReadOnlyList<StatLine> AfterColumn => Column(AfterRuns, Result.Basis, Result.Average?.AfterSpreadPercent);
    public IReadOnlyList<StatLine> AgainColumn => Column(AgainRuns, Result.Basis, null);

    partial void OnLinkedRunIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLinkedRun));
        OnPropertyChanged(nameof(LinkedRunText));
        OnPropertyChanged(nameof(HistoryButtonText));
    }

    partial void OnImportStatusChanged(string value) => OnPropertyChanged(nameof(HasImportStatus));

    partial void OnResultChanged(ComparisonResult value)
    {
        foreach (var n in new[] { nameof(VerdictText), nameof(VerdictTone), nameof(Headline), nameof(Suggestion), nameof(DriftNote), nameof(HasDriftNote),
                     nameof(Basis), nameof(BasisText), nameof(BasisLabel), nameof(Warnings), nameof(HasWarnings), nameof(BeforeColumn), nameof(AfterColumn), nameof(AgainColumn), nameof(HasAgain) })
            OnPropertyChanged(n);
    }

    public ObservableCollection<RunRow> SetFor(RunSet set) => set switch
    {
        RunSet.After => AfterRuns,
        RunSet.BeforeAgain => AgainRuns,
        _ => BeforeRuns,
    };

    /// <summary>Reads the files into the set chosen in <see cref="TargetIndex"/>. Problems are listed, never thrown.</summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (IsImporting || paths.Count == 0) return;
        // The target set and its name are taken together now: switching the target during a slow import changes neither.
        var target = (RunSet)Math.Clamp(TargetIndex, 0, 2);
        var set = SetFor(target);
        var where = NameOf(target);
        var problems = new List<string>();
        int added = 0;
        IsImporting = true;
        try
        {
            foreach (var path in paths.Take(MaxFilesPerImport))
            {
                if (set.Count >= MaxRunsPerSet) { problems.Add($"A set holds at most {MaxRunsPerSet} runs."); break; }
                ImportResult result;
                try { result = await (Importer ?? DefaultImporter)(path, _limits, ct).ConfigureAwait(true); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Anything unexpected (out of memory on a huge file, a reader bug) is this file's problem, shown in the list.
                    problems.Add($"{Path.GetFileName(path)}: PC Tidy couldn't read this file ({(ex is OutOfMemoryException ? "it is too large to read" : ex.Message)}).");
                    continue;
                }
                if (result.File is not { } file)
                {
                    problems.Add($"{Path.GetFileName(path)}: {result.Problem}");
                    continue;
                }
                if (file.RunsAreSeparate)
                {
                    for (int i = 0; i < file.Processes.Count && set.Count < MaxRunsPerSet; i++, added++)
                        set.Add(new RunRow(file, i, _ => Recompare(), Remove));
                }
                else
                {
                    set.Add(new RunRow(file, 0, _ => Recompare(), Remove));
                    added++;
                }
            }
            if (paths.Count > MaxFilesPerImport) problems.Add($"Only the first {MaxFilesPerImport} files were read.");
        }
        catch (OperationCanceledException) { problems.Add("Import stopped."); }
        finally { IsImporting = false; }

        ImportStatus = (added > 0 ? $"Added {added} run(s) to {where}. " : "") + string.Join(" ", problems);
        Recompare();
    }

    /// <summary>The set's name as the page shows it.</summary>
    public static string NameOf(RunSet set) => set switch { RunSet.After => "After", RunSet.BeforeAgain => "Before again", _ => "Before" };

    /// <summary>Reads one file (tests swap it for a fake; the default reads the file on a background thread).</summary>
    internal Func<string, ImportLimits, CancellationToken, Task<ImportResult>>? Importer { get; set; }

    private static Task<ImportResult> DefaultImporter(string path, ImportLimits limits, CancellationToken ct) => RecordingImporter.ImportAsync(path, limits, ct);

    /// <summary>
    /// Starts an import from the page (a file pick or a drop) and observes it: whatever goes wrong ends up in the import
    /// status on screen, never in an unobserved task.
    /// </summary>
    public async Task ImportFromPageAsync(IReadOnlyList<string> paths)
    {
        try { await ImportFilesAsync(paths).ConfigureAwait(true); }
        catch (Exception ex)
        {
            IsImporting = false;
            ImportStatus = "The files couldn't be read: " + (ex is OutOfMemoryException ? "they are too large." : ex.Message);
        }
    }

    /// <summary>Adds an already-read file (tests, and the page's drop handler after reading).</summary>
    public void AddRun(RunSet set, RecordingFile file, int processIndex = 0)
    {
        SetFor(set).Add(new RunRow(file, processIndex, _ => Recompare(), Remove));
        Recompare();
    }

    private void Remove(RunRow row)
    {
        if (!BeforeRuns.Remove(row) && !AfterRuns.Remove(row)) AgainRuns.Remove(row);
        Recompare();
    }

    public void Recompare()
    {
        var result = Comparer.Compare(
            BeforeRuns.Select(r => r.Summary).ToList(),
            AfterRuns.Select(r => r.Summary).ToList(),
            AgainRuns.Count > 0 ? AgainRuns.Select(r => r.Summary).ToList() : null);
        // Every run line uses the verdict's timing (on-screen only when every run has it).
        foreach (var row in BeforeRuns.Concat(AfterRuns).Concat(AgainRuns)) row.UseBasis(result.Basis);
        Result = result;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<StatLine> Column(IReadOnlyCollection<RunRow> runs, FrameTimeBasis basis, double? spread)
    {
        var inv = CultureInfo.InvariantCulture;
        if (runs.Count == 0) return new[] { new StatLine("Runs", "none yet") };
        // The same basis as the verdict for every run, so the columns never contradict the headline.
        var stats = runs.Select(r => r.Summary.StatsFor(basis)).ToList();
        var lines = new List<StatLine>
        {
            new("Runs", runs.Count.ToString(inv)),
            new("Timing", basis == FrameTimeBasis.Display ? "on screen" : "Present calls"),
            new("Average FPS", stats.Average(s => s.AverageFps).ToString("0.0", inv)),
            new("1% low", stats.Average(s => s.OnePercentLowFps).ToString("0.0", inv) + " FPS"),
        };
        if (stats.All(s => s.PointOnePercentLowIsReliable))
            lines.Add(new("0.1% low", stats.Average(s => s.PointOnePercentLowFps).ToString("0.0", inv) + " FPS"));
        lines.Add(new("P99 frame time", stats.Average(s => s.P99FrameMs).ToString("0.0", inv) + " ms"));
        lines.Add(new("Stutters", stats.Average(s => s.StuttersPerMinute).ToString("0.0", inv) + " per minute"));
        lines.Add(new("Run length", stats.Average(s => s.Seconds).ToString("0", inv) + " s"));
        var avgSpread = spread ?? FrameStats.SpreadPercent(stats.Select(s => s.AverageFps).ToList());
        if (runs.Count > 1) lines.Add(new("Spread", avgSpread.ToString("0.0", inv) + "% between runs"));
        return lines;
    }

    // ------------------------------------------------------------------ save / open

    public SavedComparison ToSaved() => new()
    {
        Id = _loadedId ?? Guid.NewGuid().ToString("N"),
        CreatedUtc = DateTime.UtcNow,
        Title = string.IsNullOrWhiteSpace(Title) ? "Comparison" : Title.Trim(),
        LinkedRunId = HasLinkedRun ? LinkedRunId!.Trim() : null,
        Before = BeforeRuns.Select(r => r.Summary).ToList(),
        After = AfterRuns.Select(r => r.Summary).ToList(),
        BeforeAgain = AgainRuns.Select(r => r.Summary).ToList(),
        Conditions = Conditions.Select(c => c.Entry).ToList(),
        VerdictAtSave = Result.Verdict,
        HeadlineAtSave = Result.Headline,
    };

    private void Save()
    {
        if (BeforeRuns.Count == 0 && AfterRuns.Count == 0)
        {
            SaveStatus = "Import some runs first.";
            return;
        }
        // The command runs on the UI thread: nothing may escape it (it would end up in the app-wide error dialog).
        try
        {
            var saved = ToSaved();
            if (_store.Save(saved))
            {
                _loadedId = saved.Id;
                SaveStatus = "Saved.";
                RefreshSaved();
            }
            else SaveStatus = _store.LastProblem ?? "The comparison couldn't be saved.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SaveStatus = "The comparison couldn't be saved (" + ex.Message + ").";
        }
    }

    /// <summary>The saved comparison now open, or null for a new one (tests).</summary>
    public string? OpenComparisonId => _loadedId;

    /// <summary>
    /// "Check if this helped" for a History run. When a saved comparison for another run is open, a new comparison starts
    /// first, so saving never overwrites that one's file with this run's link.
    /// </summary>
    public void CheckRun(string? runId)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();
        if (id is not null && _loadedId is not null && !string.Equals(id, LinkedRunId, StringComparison.Ordinal))
        {
            NewComparison();
            LinkedRunId = null;
        }
        LinkedRunId = id;
    }

    public void RefreshSaved()
    {
        Saved.Clear();
        foreach (var c in _store.List())
        {
            var line = $"{c.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {ComparisonResult.ChipText(c.VerdictAtSave)} · " +
                       $"{c.Before.Count} before, {c.After.Count} after" + (c.LinkedRunId is { } id ? $" · History run {id}" : "");
            Saved.Add(new SavedRow(c, c.Title, line, "Open saved comparison " + c.Title));
        }
        OnPropertyChanged(nameof(HasSaved));
    }

    public void Load(SavedComparison c)
    {
        BeforeRuns.Clear();
        AfterRuns.Clear();
        AgainRuns.Clear();
        foreach (var r in c.Before) BeforeRuns.Add(new RunRow(r, _ => Recompare(), Remove));
        foreach (var r in c.After) AfterRuns.Add(new RunRow(r, _ => Recompare(), Remove));
        foreach (var r in c.BeforeAgain) AgainRuns.Add(new RunRow(r, _ => Recompare(), Remove));
        foreach (var row in Conditions)
        {
            var e = c.Conditions.FirstOrDefault(x => x.Key == row.Entry.Key);
            row.Same = e?.SameForAllRuns ?? false;
            row.Note = e?.Note ?? "";
        }
        Title = c.Title;
        LinkedRunId = c.LinkedRunId;
        _loadedId = c.Id;
        SaveStatus = "";
        ImportStatus = "";
        Recompare();
    }

    private void OpenSaved(SavedRow? row)
    {
        if (row is not null) Load(row.Comparison);
    }

    private void NewComparison()
    {
        BeforeRuns.Clear();
        AfterRuns.Clear();
        AgainRuns.Clear();
        foreach (var row in Conditions) { row.Same = false; row.Note = ""; }
        Title = "";
        _loadedId = null;
        SaveStatus = "";
        ImportStatus = "";
        Recompare();
    }

    // ------------------------------------------------------------------ links (through the shell, never Process.Start)

    private async Task OpenToolAsync(MeasureText.Tool? tool)
    {
        if (tool is null || _services.GetService(typeof(IFlowHost)) is not IFlowHost flow) return;
        var plan = new ActionPlan
        {
            FeatureId = MeasureModule.FeatureId,
            Title = "Open a page",
            Items = new[]
            {
                new PlanItem
                {
                    Kind = ActionKind.OpenExternal, FeatureId = MeasureModule.FeatureId, Description = "Open the " + tool.Name + " page",
                    Args = new Dictionary<string, string> { ["target"] = tool.Url }, Reversible = true,
                },
            },
        };
        try { await flow.RunApprovedAsync(plan, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { flow.ShowMessage("Couldn't open the page", ex.Message); }
    }

    /// <summary>History, opened on the run this comparison checks (selected, with its Undo) when there is one.</summary>
    private void OpenHistory()
    {
        var shell = _services.GetService(typeof(IShellFlowHost)) as IShellFlowHost ?? _services.GetService(typeof(IFlowHost)) as IShellFlowHost;
        if (shell is not null) shell.NavigateTo("history", HasLinkedRun ? new HistoryOpenRequest(LinkedRunId!) : null);
        else (_services.GetService(typeof(IFlowHost)) as IFlowHost)?.ShowMessage("History", "Open History from the menu on the left.");
    }
}
