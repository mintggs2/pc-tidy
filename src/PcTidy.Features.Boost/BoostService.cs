using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Games;
using PcTidy.Features.Boost.Planning;
using PcTidy.Features.Boost.State;

namespace PcTidy.Features.Boost;

public enum BoostPhase { Off, Watching, Active }

/// <summary>What the page shows in its status bar.</summary>
public sealed record BoostStatus(BoostPhase Phase, string Text, string? GameName = null)
{
    public static readonly BoostStatus Off = new(BoostPhase.Off, "Game boost is off. Nothing is watched or changed.");
}

/// <summary>Everything the boost loop uses. The real app builds it with <see cref="BoostService.Create"/>; tests pass fakes.</summary>
public sealed record BoostDependencies
{
    public required IFlowHost Flow { get; init; }
    public required IProcessQuery Processes { get; init; }
    public required ICommandLineReader CommandLines { get; init; }
    public required ISteamStatus Steam { get; init; }
    public required IBoostGameSource Games { get; init; }
    public required BoostStateStore Store { get; init; }
    public required Func<BoostSettings> Settings { get; init; }
    /// <summary>Apps inside the Windows folder are never closed.</summary>
    public required string WindowsDir { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Polls in a row without the game before it counts as closed (launchers restart the game's exe).</summary>
    public int ExitGracePolls { get; init; } = 2;
    /// <summary>The game list is re-read from the launchers' manifests this often.</summary>
    public TimeSpan GameListRefresh { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Game boost mode (optional, off by default). Reads the process list every 3–5 s with limited query rights only.
/// When a game the user switched on starts, it runs ONE pre-approved plan through
/// <see cref="IFlowHost.RunApprovedAsync"/>: pause OneDrive (if ticked) and politely close the apps the user picked.
/// When the game exits it runs the restore plan: start OneDrive, reopen the apps unelevated. What was changed is
/// saved to &lt;DataRoot&gt;\boost\state.json before anything changes, so a crash or restart is put right at the next start.
/// Windows Update is never touched. Games, launchers, anti-cheat and Windows are never closed, suspended or re-prioritised.
/// Every boost and restore is a normal run in History.
/// </summary>
public sealed class BoostService : IAsyncDisposable
{
    private const int MaxRestoreAttempts = 3;

    private readonly BoostDependencies _d;
    private readonly ProcessPoller _poller;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private BoostState _state;
    private IReadOnlyList<BoostGame> _games = Array.Empty<BoostGame>();
    private DateTimeOffset _gamesReadAt = DateTimeOffset.MinValue;
    private int _missedPolls;
    private int _restoreAttempts;

    public BoostService(BoostDependencies dependencies)
    {
        _d = dependencies;
        _poller = new ProcessPoller(dependencies.Processes);
        _state = dependencies.Store.Load();
        Status = _state.Active ? ActiveStatus(_state) : BoostStatus.Off;
    }

    /// <summary>The real PC (or the --sandbox practice PC: processes are still only read).</summary>
    public static BoostService Create(IServiceProvider services, Func<BoostSettings> settings)
    {
        var locations = services.Get<IKnownLocations>();
        var appSettings = services.Get<AppSettings>();
        return new BoostService(new BoostDependencies
        {
            Flow = services.Get<IFlowHost>(),
            Processes = new WindowsProcessQuery(),
            CommandLines = locations.IsSandbox ? NoCommandLineReader.Instance : new WmiCommandLineReader(),
            Steam = locations.IsSandbox ? NoSteamStatus.Instance : new RegistrySteamStatus(),
            Games = new LauncherGameSource(locations),
            Store = new BoostStateStore(appSettings.DataRoot),
            Settings = settings,
            WindowsDir = locations.WindowsDir,
        });
    }

    public bool IsRunning { get { lock (_gate) return _loop is { IsCompleted: false }; } }
    public BoostStatus Status { get; private set; }
    /// <summary>A copy of what's in effect now.</summary>
    public BoostState State { get { lock (_gate) return Clone(_state); } }
    public string? LastProblem { get; private set; }

    /// <summary>Raised on a background thread. UI subscribers must marshal to their dispatcher.</summary>
    public event EventHandler<BoostStatus>? StatusChanged;
    /// <summary>Raised on a background thread after each boost or restore run.</summary>
    public event EventHandler<RunRecord>? RunFinished;

    /// <summary>
    /// Starts the background loop. It first puts back anything a previous session left boosted (crash, restart),
    /// then, if Game boost is switched on, keeps watching. Returns immediately.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    /// <summary>Stops watching. A boost in effect stays recorded and is put back at the next start (or by the page).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
        }
        if (!_state.Active) SetStatus(BoostStatus.Off);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await RecoverAtStartAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { LastProblem = ex.Message; }

        while (!ct.IsCancellationRequested)
        {
            var settings = SafeSettings();
            if (!settings.Enabled && !_state.Active) { SetStatus(BoostStatus.Off); return; }
            try { await PollOnceAsync(ct).ConfigureAwait(false); LastProblem = null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // One bad poll never stops the loop.
                LastProblem = ex.Message;
            }
            try { await Task.Delay(BoostSettings.ClampPoll(settings.PollSeconds), _d.Time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// At app start: if the saved state says a boost was in effect and its game isn't running any more, put
    /// everything back now. If the game is still running, the boost simply continues.
    /// </summary>
    public async Task<RunRecord?> RecoverAtStartAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Active) return null;
            var snapshot = _poller.Snapshot();
            if (SafeSettings().Enabled && IsActiveGameRunning(snapshot)) { SetStatus(ActiveStatus(_state)); return null; }
            return await RestoreCoreAsync(snapshot, ct).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    /// <summary>Puts everything back right now (the user switched boost off, or chose "Put things back").</summary>
    public async Task<RunRecord?> RestoreNowAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.Active ? await RestoreCoreAsync(_poller.Snapshot(), ct).ConfigureAwait(false) : null; }
        finally { _cycleGate.Release(); }
    }

    /// <summary>One look at the running processes. Public so tests drive it without timers.</summary>
    public async Task<RunRecord?> PollOnceAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = SafeSettings();
            var snapshot = _poller.Snapshot();
            if (_state.Active)
            {
                if (settings.Enabled && IsActiveGameRunning(snapshot)) { _missedPolls = 0; return null; }
                // Switched off mid-game: put things back at once. Otherwise wait a poll or two (launchers restart the exe).
                if (settings.Enabled && ++_missedPolls < Math.Max(1, _d.ExitGracePolls)) return null;
                return await RestoreCoreAsync(snapshot, ct).ConfigureAwait(false);
            }

            if (!settings.Enabled) { SetStatus(BoostStatus.Off); return null; }
            var enabled = EnabledGames(settings);
            var match = GameMatcher.Match(enabled, snapshot, SafeSteam());
            if (match is null)
            {
                SetStatus(WatchingStatus(enabled.Count));
                return null;
            }
            return await BeginBoostAsync(match, settings, snapshot, ct).ConfigureAwait(false);
        }
        finally { _cycleGate.Release(); }
    }

    private async Task<RunRecord?> BeginBoostAsync(GameMatch match, BoostSettings settings, IReadOnlyList<ObservedProcess> snapshot, CancellationToken ct)
    {
        IReadOnlySet<int> windows;
        try { windows = _d.Processes.PidsWithWindows(); } catch (Exception) { windows = new HashSet<int>(); }
        var planned = BoostPlanner.PlanBoost(match, settings, snapshot, windows, _d.CommandLines, _d.WindowsDir, _d.Time.GetUtcNow().UtcDateTime);
        // Saved BEFORE anything changes: a crash from here on is put right at the next start.
        planned.State.ReopenOffers = _state.ReopenOffers.ToList();
        Save(planned.State);
        _missedPolls = 0;
        _restoreAttempts = 0;
        if (planned.Plan is null) { SetStatus(ActiveStatus(_state)); return null; }

        RunRecord? run = null;
        bool failed = false;
        try { run = await _d.Flow.RunApprovedAsync(planned.Plan, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { failed = true; LastProblem = $"The boost couldn't run: {ex.Message}"; }
        // After a failure the intended state stays saved: the restore skips what isn't needed (OneDrive already
        // running, apps still open), so putting back "too much" is harmless.
        if (!failed) Save(BoostPlanner.AfterBoostRun(planned.State, run));
        SetStatus(ActiveStatus(_state));
        if (run is not null) RunFinished?.Invoke(this, run);
        return run;
    }

    private async Task<RunRecord?> RestoreCoreAsync(IReadOnlyList<ObservedProcess> snapshot, CancellationToken ct)
    {
        var state = _state;
        var restore = BoostPlanner.PlanRestore(state, snapshot, SafeSettings().ReopenAppsAfter, _d.Time.GetUtcNow().UtcDateTime);
        RunRecord? run = null;
        if (restore.Plan is not null)
        {
            try { run = await _d.Flow.RunApprovedAsync(restore.Plan, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LastProblem = $"Putting things back didn't work yet: {ex.Message}"; }
            if (run is null && ++_restoreAttempts < MaxRestoreAttempts)
            {
                // Try again on the next poll (e.g. another run was in progress). The state stays saved.
                SetStatus(new BoostStatus(BoostPhase.Active, $"{state.GameName ?? "The game"} closed. Putting things back…", state.GameName));
                return null;
            }
        }
        var after = BoostPlanner.AfterRestoreRun(state, restore, run);
        if (run is null && restore.Plan is not null)
            // Gave up retrying: offer the apps as buttons so nothing is lost.
            after.ReopenOffers = after.ReopenOffers.Concat(state.ClosedApps).GroupBy(a => a.ExePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        Save(after);
        _missedPolls = 0;
        _restoreAttempts = 0;
        var settings = SafeSettings();
        SetStatus(settings.Enabled
            ? new BoostStatus(BoostPhase.Watching, $"{state.GameName ?? "The game"} closed. Everything was put back. Watching for your games.")
            : BoostStatus.Off);
        if (run is not null) RunFinished?.Invoke(this, run);
        return run;
    }

    /// <summary>The user ran a "Reopen" button: that app is no longer offered.</summary>
    public void ForgetOffer(string exePath)
    {
        lock (_gate)
        {
            var copy = Clone(_state);
            copy.ReopenOffers.RemoveAll(a => a.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
            Save(copy);
        }
    }

    private bool IsActiveGameRunning(IReadOnlyList<ObservedProcess> snapshot)
    {
        var game = AllGames().FirstOrDefault(g => g.Key.Equals(_state.GameKey, StringComparison.OrdinalIgnoreCase));
        return game is not null && GameMatcher.Match(new[] { game }, snapshot, SafeSteam()) is not null;
    }

    private IReadOnlyList<BoostGame> EnabledGames(BoostSettings settings)
    {
        var keys = settings.EnabledGameKeys();
        if (keys.Count == 0) return Array.Empty<BoostGame>();
        // Keep the user's order of choices so the first enabled game wins when two run at once.
        var all = AllGames();
        return settings.Games.Where(c => c.Enabled)
            .Select(c => all.FirstOrDefault(g => g.Key.Equals(c.Key, StringComparison.OrdinalIgnoreCase)))
            .OfType<BoostGame>().ToList();
    }

    private IReadOnlyList<BoostGame> AllGames()
    {
        var now = _d.Time.GetUtcNow();
        if (now - _gamesReadAt < _d.GameListRefresh && _games.Count > 0) return _games;
        try { _games = _d.Games.Games(); }
        catch (Exception) { /* keep the last list */ }
        _gamesReadAt = now;
        return _games;
    }

    private void Save(BoostState state)
    {
        lock (_gate) _state = state;
        if (!_d.Store.Save(state)) LastProblem = _d.Store.LastProblem;
    }

    private BoostSettings SafeSettings()
    {
        try { return _d.Settings() ?? new BoostSettings(); } catch (Exception) { return new BoostSettings(); }
    }

    private int? SafeSteam()
    {
        try { return _d.Steam.RunningAppId(); } catch (Exception) { return null; }
    }

    private void SetStatus(BoostStatus status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static BoostStatus WatchingStatus(int games) => games == 0
        ? new BoostStatus(BoostPhase.Watching, "On, but no game is switched on yet. Pick the games to boost below.")
        : new BoostStatus(BoostPhase.Watching, games == 1
            ? "On. Watching for 1 game. Nothing changes until it starts."
            : $"On. Watching for {games} games. Nothing changes until one starts.");

    internal static BoostStatus ActiveStatus(BoostState state)
    {
        var parts = new List<string>();
        if (state.ClosedApps.Count > 0)
            parts.Add(state.ClosedApps.Count == 1 ? $"{state.ClosedApps[0].DisplayName} closed" : $"{state.ClosedApps.Count} apps closed");
        if (state.OneDrivePaused) parts.Add("OneDrive paused");
        var what = parts.Count == 0 ? "Nothing needed closing." : string.Join(", ", parts) + ". Everything comes back when the game closes.";
        return new BoostStatus(BoostPhase.Active, $"Boost active for {state.GameName ?? "your game"}. {what}", state.GameName);
    }

    private static BoostState Clone(BoostState s) => new()
    {
        Version = s.Version, Active = s.Active, GameKey = s.GameKey, GameName = s.GameName, StartedUtc = s.StartedUtc,
        OneDrivePaused = s.OneDrivePaused, BoostRunId = s.BoostRunId,
        ClosedApps = s.ClosedApps.Select(Copy).ToList(), ReopenOffers = s.ReopenOffers.Select(Copy).ToList(),
    };

    private static ClosedApp Copy(ClosedApp a) => new() { ProcessName = a.ProcessName, DisplayName = a.DisplayName, ExePath = a.ExePath, Arguments = a.Arguments };
}
