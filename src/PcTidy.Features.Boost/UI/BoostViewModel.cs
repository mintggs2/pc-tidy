using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Games;
using PcTidy.Features.Boost.Planning;
using PcTidy.Features.Boost.State;
using Wpf.Ui.Controls;

namespace PcTidy.Features.Boost.UI;

/// <summary>
/// The Game boost page: master switch, games to boost, apps to close, OneDrive toggle, live status and "Reopen"
/// buttons. It only edits settings and shows the service's state; every change to the PC goes through IFlowHost.
/// </summary>
public sealed partial class BoostViewModel : ObservableObject
{
    private readonly AppSettings _appSettings;
    private readonly IFlowHost _flow;
    private readonly IKnownLocations _locations;
    private readonly BoostService _service;
    private readonly BoostSettings _settings;
    private bool _loading = true;

    public BoostViewModel(IServiceProvider services, BoostModule module)
    {
        _appSettings = services.Get<AppSettings>();
        _flow = services.Get<IFlowHost>();
        _locations = services.Get<IKnownLocations>();
        _service = module.GetService(services);
        _settings = BoostSettings.Load(_appSettings);

        _isEnabled = _settings.Enabled;
        _pauseOneDrive = _settings.PauseOneDrive;
        _reopenAppsAfter = _settings.ReopenAppsAfter;
        foreach (var a in _settings.AppsToClose) Apps.Add(new AppRow(a.ProcessName, a.DisplayName));
        OneDriveAvailable = _locations.CloudRoots.Any(r => r.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
        PowerNote = BoostPlanner.PowerNote(_locations.IsSandbox ? Catalog.Tweaks.CpuKind.None : Catalog.Tweaks.CpuKinds.Classify(CpuBrandReader.Read()));

        _service.StatusChanged += (_, s) => OnUi(() => ShowStatus(s));
        _service.RunFinished += (_, _) => OnUi(RefreshOffers);
        ShowStatus(_service.Status);
        RefreshOffers();
        _loading = false;

        _ = LoadGamesAsync();
        _ = RefreshRunningAppsAsync();
    }

    public const string WindowsUpdateNote = BoostPlanner.WindowsUpdateNote;

    /// <summary>Why the power plan is left alone (on dual-CCD Ryzen X3D: keep Balanced, Game Mode, Game Bar and the chipset driver).</summary>
    public string PowerNote { get; }

    /// <summary>
    /// Text-only "Good to know" cards (C12 closing background apps first, then C1 cap your FPS and C7 Reflex / Anti-Lag),
    /// each with measured numbers, hardware and source. They describe; Game boost's behaviour doesn't change.
    /// </summary>
    public IReadOnlyList<TipCard> Tips { get; } = TipCard.ForBoost();

    public ObservableCollection<GameRow> Games { get; } = new();
    public ObservableCollection<AppRow> Apps { get; } = new();
    public ObservableCollection<RunningAppOption> RunningApps { get; } = new();
    public ObservableCollection<OfferRow> Offers { get; } = new();

    public bool OneDriveAvailable { get; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _pauseOneDrive;
    [ObservableProperty] private bool _reopenAppsAfter;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _isBoostActive;
    [ObservableProperty] private string _gamesNote = "Looking for your games…";
    [ObservableProperty] private bool _hasGames;
    [ObservableProperty] private bool _hasApps;
    [ObservableProperty] private bool _hasOffers;
    [ObservableProperty] private RunningAppOption? _selectedRunningApp;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.Enabled = value;
        Save();
        if (value) _service.Start();
        else _ = SwitchOffAsync();
    }

    partial void OnPauseOneDriveChanged(bool value) { if (!_loading) { _settings.PauseOneDrive = value; Save(); } }
    partial void OnReopenAppsAfterChanged(bool value) { if (!_loading) { _settings.ReopenAppsAfter = value; Save(); } }
    partial void OnSelectedRunningAppChanged(RunningAppOption? value)
    {
        AddAppCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RunningAppHint));
    }

    /// <summary>Placeholder in the empty app picker, so it never looks like a blank box.</summary>
    public string RunningAppHint => SelectedRunningApp is not null ? ""
        : RunningApps.Count == 0 ? "No other apps are running (press refresh)" : "Pick a running app";

    /// <summary>Switched off: anything still boosted is put back now, then watching stops.</summary>
    private async Task SwitchOffAsync()
    {
        try
        {
            await _service.RestoreNowAsync(CancellationToken.None);
            await _service.StopAsync();
        }
        catch (Exception ex) { _flow.ShowMessage("Game boost", "Game boost couldn't put everything back yet: " + ex.Message, isError: true); }
        RefreshOffers();
    }

    private async Task LoadGamesAsync()
    {
        IReadOnlyList<BoostGame> games;
        try { games = await Task.Run(() => new LauncherGameSource(_locations).Games()); }
        catch (Exception) { games = Array.Empty<BoostGame>(); }
        Games.Clear();
        foreach (var g in games)
            Games.Add(new GameRow(g.Key, g.Name, g.Launcher, _settings.IsGameEnabled(g.Key), OnGameToggled));
        HasGames = Games.Count > 0;
        GamesNote = Games.Count == 0
            ? "No Steam or Epic Games were found on this PC, so there's nothing to boost yet."
            : "Switch on the games you want boosted. Nothing happens for any other game.";
    }

    private void OnGameToggled(GameRow row)
    {
        _settings.SetGame(row.Key, row.Name, row.IsEnabled);
        Save();
    }

    /// <summary>Apps with a window that are running now (read with limited rights; Windows, launchers and anti-cheat left out).</summary>
    [RelayCommand]
    private async Task RefreshRunningAppsAsync()
    {
        List<RunningAppOption> options;
        try { options = await Task.Run(ReadRunningApps); }
        catch (Exception) { options = new(); }
        RunningApps.Clear();
        foreach (var o in options.Where(o => !Apps.Any(a => a.ProcessName.Equals(o.ProcessName, StringComparison.OrdinalIgnoreCase))))
            RunningApps.Add(o);
        OnPropertyChanged(nameof(RunningAppHint));
    }

    private List<RunningAppOption> ReadRunningApps()
    {
        var query = new WindowsProcessQuery();
        var windows = query.PidsWithWindows();
        var gameFolders = _locations.GameInstallDirs.Concat(_locations.SteamLibraries).ToList();
        return new ProcessPoller(query).Snapshot()
            .Where(p => p.Inspected && p.ImagePath is not null && windows.Contains(p.Pid) && !BoostDenyList.NeverClose(p.Name))
            .Where(p => !GameMatcher.IsUnder(p.ImagePath!, _locations.WindowsDir) && !gameFolders.Any(f => GameMatcher.IsUnder(p.ImagePath!, f)))
            .Where(p => p.Pid != Environment.ProcessId)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RunningAppOption(g.Key, FriendlyName(g.First().ImagePath!, g.Key)))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The exe's own description ("Discord"), read from its version info (read-only), else the process name.</summary>
    private static string FriendlyName(string exePath, string processName)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) || description.Length > 60 ? processName : description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return processName; }
    }

    private bool CanAddApp() => SelectedRunningApp is not null;

    [RelayCommand(CanExecute = nameof(CanAddApp))]
    private void AddApp()
    {
        if (SelectedRunningApp is not { } pick) return;
        if (BoostDenyList.NeverClose(pick.ProcessName)) return;
        if (!Apps.Any(a => a.ProcessName.Equals(pick.ProcessName, StringComparison.OrdinalIgnoreCase)))
            Apps.Add(new AppRow(pick.ProcessName, pick.DisplayName));
        RunningApps.Remove(pick);
        SelectedRunningApp = null;
        OnPropertyChanged(nameof(RunningAppHint));
        SaveApps();
    }

    [RelayCommand]
    private void RemoveApp(AppRow? row)
    {
        if (row is null) return;
        Apps.Remove(row);
        SaveApps();
        _ = RefreshRunningAppsAsync();
    }

    private void SaveApps()
    {
        _settings.AppsToClose = Apps.Select(a => new BoostAppChoice { ProcessName = a.ProcessName, DisplayName = a.DisplayName }).ToList();
        HasApps = Apps.Count > 0;
        Save();
    }

    /// <summary>"Put things back now" while a boost is active.</summary>
    [RelayCommand]
    private async Task PutBackNowAsync()
    {
        await _service.RestoreNowAsync(CancellationToken.None);
        RefreshOffers();
    }

    /// <summary>"Reopen Discord": a one-item plan through the normal confirm (it's a History run like any other).</summary>
    [RelayCommand]
    private async Task ReopenAsync(OfferRow? offer)
    {
        if (offer is null) return;
        var run = await _flow.ReviewAndRunAsync(BoostPlanner.PlanReopen(offer.App), CancellationToken.None);
        if (run is not null) _service.ForgetOffer(offer.App.ExePath);
        RefreshOffers();
    }

    private void RefreshOffers()
    {
        Offers.Clear();
        foreach (var app in _service.State.ReopenOffers) Offers.Add(new OfferRow(app));
        HasOffers = Offers.Count > 0;
        HasApps = Apps.Count > 0;
    }

    private void ShowStatus(BoostStatus status)
    {
        StatusText = _service.LastProblem is { } problem ? $"{status.Text} ({problem})" : status.Text;
        IsBoostActive = status.Phase == BoostPhase.Active;
        StatusSeverity = status.Phase switch
        {
            BoostPhase.Active => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational,
        };
    }

    private void Save() => _settings.Store(_appSettings);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }
}

/// <summary>One game in the list, with its boost switch.</summary>
public sealed partial class GameRow : ObservableObject
{
    private readonly Action<GameRow> _changed;

    public GameRow(string key, string name, string launcher, bool enabled, Action<GameRow> changed)
    {
        Key = key; Name = name; Launcher = launcher; _isEnabled = enabled; _changed = changed;
    }

    public string Key { get; }
    public string Name { get; }
    public string Launcher { get; }
    public string AutomationName => $"Boost {Name}";

    [ObservableProperty] private bool _isEnabled;
    partial void OnIsEnabledChanged(bool value) => _changed(this);
}

public sealed record AppRow(string ProcessName, string DisplayName)
{
    public string RemoveName => $"Stop closing {DisplayName}";
}

public sealed record RunningAppOption(string ProcessName, string DisplayName)
{
    public override string ToString() => DisplayName == ProcessName ? DisplayName : $"{DisplayName} ({ProcessName})";
}

public sealed record OfferRow(ClosedApp App)
{
    public string Label => $"Reopen {App.DisplayName}";
}
