using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Watch;

namespace PcTidy.Features.Organize.UI;

/// <summary>Watch-folder tab. The service runs in the background; this only starts/stops it and shows what it's doing.</summary>
public sealed partial class WatchViewModel : ObservableObject
{
    private readonly WatchService _service;
    private readonly IFlowHost _flow;
    private readonly OrganizePlanner _planner;
    private readonly Func<OrganizeSettings> _settings;
    private readonly Action _save;
    private readonly IKnownLocations _locations;


    public WatchViewModel(WatchService service, IFlowHost flow, OrganizePlanner planner, Func<OrganizeSettings> settings, Action save, IKnownLocations locations)
    {
        _service = service;
        _flow = flow;
        _planner = planner;
        _settings = settings;
        _save = save;
        _locations = locations;
        foreach (var f in OrganizeModule.WatchFolders(settings(), locations)) Folders.Add(f);
        _isEnabled = service.IsRunning;
        service.Checked += (_, result) => OnUi(() => UpdateStatus(result));
        service.PendingChanged += (_, e) => OnUi(() => UpdatePending(e.Pending));
        UpdatePending(service.PendingActions);
        RefreshAutomaticRules();
        StatusText = service.IsRunning ? "Watching. Checking every minute." : "Off. Nothing is watched.";
    }

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<string> AutomaticRules { get; } = new();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _pendingText = "";

    partial void OnIsEnabledChanged(bool value)
    {

        _settings().Watch.Enabled = value;
        _save();
        if (value)
        {
            _service.Start(Folders.ToList());
            StatusText = "Watching. New files are sorted after 10 quiet minutes.";
        }
        else
        {
            _ = StopAsync();
        }
    }

    private async Task StopAsync()
    {
        await _service.StopAsync();
        StatusText = "Off. Nothing is watched.";
    }

    public void RefreshAutomaticRules()
    {
        AutomaticRules.Clear();
        foreach (var r in _settings().Rules.Where(r => r.Enabled && r.RunAutomatically)) AutomaticRules.Add(r.Name);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to watch" };
        if (dialog.ShowDialog() != true || Folders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;
        Folders.Add(dialog.FolderName);
        SaveFolders();
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder is null) return;
        Folders.Remove(folder);
        SaveFolders();
    }

    private void SaveFolders()
    {
        _settings().Watch.Folders = Folders.ToList();
        _save();
        if (_service.IsRunning)
        {
            // Restart with the new list (in the background, never blocking the page).
            _ = RestartAsync();
        }
    }

    private async Task RestartAsync()
    {
        await _service.StopAsync();
        _service.Start(Folders.ToList());
    }

    private void UpdateStatus(WatchCycleResult result)
    {
        var when = _service.LastCheckUtc?.ToLocalTime().ToString("t") ?? "–";
        StatusText = _service.LastProblem is { } problem
            ? $"Last check {when} had a problem: {problem}. Trying again."
            : $"Watching. Last check {when}" + (result.RanAutomatically > 0 ? $", tidied {result.RanAutomatically} file(s) (undo in History)." : ".");
    }

    private void UpdatePending(IReadOnlyList<PlannedAction> pending)
    {
        PendingCount = pending.Count;
        PendingText = pending.Count switch
        {
            0 => "Nothing waiting for you.",
            1 => "1 file matches a rule that waits for your OK.",
            _ => $"{pending.Count:N0} files match rules that wait for your OK.",
        };
        ReviewPendingCommand.NotifyCanExecuteChanged();
    }

    private bool HasPending() => PendingCount > 0;

    /// <summary>Re-plans the waiting files fresh (they may have changed), then shows the normal confirm.</summary>
    [RelayCommand(CanExecute = nameof(HasPending))]
    private async Task ReviewPendingAsync()
    {
        var paths = _service.PendingActions.Select(a => a.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preview = await _planner.PlanAsync(new OrganizeRequest { Roots = _service.Folders, Settings = _settings().Clone(), OnlyPaths = paths }, null, CancellationToken.None);
        if (preview.Actions.Count == 0) { _flow.ShowMessage("Nothing to do", "Those files have already moved or changed."); return; }
        var run = await _flow.ReviewAndRunAsync(PlanAssembler.Build("Watch folder: files waiting for your OK", preview.Actions.Where(a => a.Badge == Badge.Safe)), CancellationToken.None);
        if (run is not null) _service.ReportRun(run);
        await _service.RunCycleAsync(fullRescan: true, CancellationToken.None);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }
}
