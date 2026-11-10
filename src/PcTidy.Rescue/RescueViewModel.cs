using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PcTidy.Core.Model;

namespace PcTidy.Rescue;

/// <summary>What the window needs from the user (confirm, pick a folder, show a message); faked in tests.</summary>
public interface IRescueDialogs
{
    bool Confirm(string title, string message);
    string? PickFolder(string? startIn);
    void Show(string title, string message, bool isError);
    void OpenFolder(string path);
}

/// <summary>State and commands of the Rescue window. All logic lives in <see cref="RescueService"/>.</summary>
public sealed class RescueViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRescueDialogs _dialogs;
    private readonly Func<RescueTarget, RescueService> _open;
    private RescueService? _service;
    private RunEntry? _selected;
    private bool _busy;
    private string _status = "";
    private string _progress = "";
    private string _dataRoot = "";
    private string _howFound = "";

    public RescueViewModel(RescueTarget initial, IRescueDialogs dialogs, Func<RescueTarget, RescueService>? open = null)
    {
        _dialogs = dialogs;
        _open = open ?? RescueService.Open;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy && _service is not null);
        BrowseCommand = new AsyncCommand(BrowseAsync, () => !IsBusy);
        UndoCommand = new AsyncCommand(UndoAsync, () => !IsBusy && Selected?.CanUndo == true);
        ResumeCommand = new AsyncCommand(ResumeAsync, () => !IsBusy && Selected?.CanResume == true);
        RollbackCommand = new AsyncCommand(RollbackAsync, () => !IsBusy && Selected?.CanRollback == true);
        RestoreQuarantineCommand = new AsyncCommand(RestoreQuarantineAsync, () => !IsBusy && Selected?.CanRestoreQuarantine == true);
        OpenLogsCommand = new AsyncCommand(() => { OpenLogs(); return Task.CompletedTask; }, () => RescueLog.FilePath is not null);
        Initial = initial;
    }

    /// <summary>Where the window starts (opened by <see cref="LoadAsync"/>).</summary>
    public RescueTarget Initial { get; }

    public ObservableCollection<RunEntry> Runs { get; } = new();

    public RunEntry? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedSteps)); CommandsChanged(); } }
    }

    public bool HasSelection => Selected is not null;
    public IReadOnlyList<StepLine> SelectedSteps => Selected?.Steps ?? Array.Empty<StepLine>();
    public bool HasRuns => Runs.Count > 0;

    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandsChanged(); }
    }

    public string StatusMessage { get => _status; private set => Set(ref _status, value); }
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }
    public string DataRoot { get => _dataRoot; private set => Set(ref _dataRoot, value); }
    public string HowFound { get => _howFound; private set => Set(ref _howFound, value); }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand UndoCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand RollbackCommand { get; }
    public AsyncCommand RestoreQuarantineCommand { get; }
    public AsyncCommand OpenLogsCommand { get; }

    /// <summary>Opens the starting data folder and lists its runs.</summary>
    public Task LoadAsync() => SwitchToAsync(Initial);

    private async Task SwitchToAsync(RescueTarget target)
    {
        RescueService next;
        try
        {
            next = _open(target);
        }
        catch (Exception ex)
        {
            RescueLog.Error("Opening " + target.DataRoot, ex);
            _dialogs.Show("Couldn't open that folder", ex.Message, isError: true);
            return;
        }
        _service?.Dispose();
        _service = next;
        RescueLog.Use(target.DataRoot);
        DataRoot = target.DataRoot + (target.IsSandbox ? "  (practice folder)" : "");
        HowFound = target.HowFound;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_service is null) return;
        var keep = Selected?.RunId;
        await BusyAsync("Reading the run history…", async () =>
        {
            IReadOnlyList<RunEntry> runs;
            try { runs = await Task.Run(() => _service.ListRunsAsync()); }
            catch (Exception ex)
            {
                RescueLog.Error("Listing runs", ex);
                StatusMessage = "Couldn't read the run history: " + ex.Message;
                return;
            }
            Runs.Clear();
            foreach (var r in runs) Runs.Add(r);
            OnPropertyChanged(nameof(HasRuns));
            Selected = Runs.FirstOrDefault(r => r.RunId == keep) ?? Runs.FirstOrDefault();
            StatusMessage = runs.Count == 0
                ? "No PC Tidy runs in this folder. If PC Tidy keeps its data somewhere else, use Browse…"
                : $"{runs.Count} run(s). Pick one to see what it changed.";
        });
    }

    private async Task BrowseAsync()
    {
        var folder = _dialogs.PickFolder(_service?.DataRoot);
        if (folder is null) return;
        if (!RescueTarget.LooksLikeDataFolder(folder) &&
            !_dialogs.Confirm("Use this folder?", $"{folder} doesn't look like a PC Tidy data folder (there's no \"runs\" or \"Quarantine\" folder in it). Use it anyway?"))
            return;
        await SwitchToAsync(RescueTarget.ForDataFolder(folder));
    }

    private Task UndoAsync() => ActAsync(
        "Undo this run?",
        e => $"\"{e.Title}\" from {e.When}\n\nPC Tidy will put back {e.UndoableSteps} change(s): files come back from quarantine, moved files go back where they were, settings go back to how they were. Nothing is overwritten.",
        (s, id, p) => s.UndoAsync(id, p));

    private Task ResumeAsync() => ActAsync(
        "Finish this run?",
        e => $"\"{e.Title}\" was interrupted (for example by a crash or power cut).\n\nResume makes the remaining changes it had planned. You can still undo it afterwards.",
        (s, id, p) => s.ResumeAsync(id, p));

    private Task RollbackAsync() => ActAsync(
        "Roll back this run?",
        e => $"\"{e.Title}\" was interrupted.\n\nRoll back puts back everything it had already changed, as if it never ran.",
        (s, id, p) => s.RollbackAsync(id, p));

    private Task RestoreQuarantineAsync() => ActAsync(
        "Put back everything in quarantine for this run?",
        e => $"{e.QuarantineText}\n\nEach item goes back to where it came from. If something with the same name is there now, it's kept and the restored item is named \"(restored)\".\n\nUse this when Undo can't finish.",
        (s, id, p) => s.RestoreQuarantineAsync(id, p));

    private async Task ActAsync(string title, Func<RunEntry, string> explain, Func<RescueService, string, IProgress<RunProgress>, Task<RescueResult>> action)
    {
        if (_service is not { } service || Selected is not { } entry) return;
        if (!_dialogs.Confirm(title, explain(entry))) return;
        RescueResult? result = null;
        await BusyAsync("Working…", async () =>
        {
            var progress = new Progress<RunProgress>(p => ProgressText = p.Message);
            result = await Task.Run(() => action(service, entry.RunId, progress));
        });
        if (result is null) return;
        StatusMessage = result.Message;
        var details = result.Details.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Details.Take(15))
            + (result.Details.Count > 15 ? $"\n…and {result.Details.Count - 15} more (see the log)." : "");
        _dialogs.Show(result.Ok ? "Done" : "Not everything worked", result.Message + details, isError: !result.Ok);
        await RefreshAsync();
    }

    private async Task BusyAsync(string message, Func<Task> work)
    {
        IsBusy = true;
        ProgressText = message;
        try { await work(); }
        catch (Exception ex)
        {
            RescueLog.Error(message, ex);
            StatusMessage = "Something went wrong: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    private void OpenLogs()
    {
        if (RescueLog.FilePath is { } log && Path.GetDirectoryName(log) is { } dir) _dialogs.OpenFolder(dir);
    }

    private void CommandsChanged()
    {
        foreach (var c in new[] { RefreshCommand, BrowseCommand, UndoCommand, ResumeCommand, RollbackCommand, RestoreQuarantineCommand, OpenLogsCommand })
            c.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public void Dispose() => _service?.Dispose();
}

/// <summary>A tiny async ICommand (no MVVM package needed): disabled while it runs, never throws to the dispatcher.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool> _canRun;
    private bool _running;

    public AsyncCommand(Func<Task> run, Func<bool> canRun)
    {
        _run = run;
        _canRun = canRun;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && _canRun();

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _run(); }
        catch (Exception ex) { RescueLog.Error("Command", ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
