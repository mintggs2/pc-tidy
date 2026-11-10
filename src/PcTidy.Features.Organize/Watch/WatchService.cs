using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Organize.Watch;

public sealed record WatchTiming
{
    /// <summary>A file must be untouched this long before anything happens to it (Plan §9).</summary>
    public TimeSpan QuietPeriod { get; init; } = SkipRules.QuietPeriod;
    /// <summary>How often the loop looks at files that became quiet.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Full re-check of every folder (notifications drop events when Windows is busy). Clamped to 15–30 minutes.</summary>
    public TimeSpan Rescan { get; init; } = TimeSpan.FromMinutes(20);

    public static TimeSpan ClampRescan(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 15, 30));
}

public sealed record WatchCycleResult(bool FullRescan, int Looked, int RanAutomatically, int Pending, IReadOnlyList<RunRecord> Runs)
{
    public static readonly WatchCycleResult Idle = new(false, 0, 0, 0, Array.Empty<RunRecord>());
}

/// <summary>Raised when files match rules that aren't automatic: the user reviews them (notification-style).</summary>
public sealed class WatchPendingEventArgs(IReadOnlyList<PlannedAction> pending) : EventArgs
{
    public IReadOnlyList<PlannedAction> Pending { get; } = pending;
}

/// <summary>
/// Watch-folder mode (optional, off by default). Applies rules to new files after they've been quiet for
/// 10 minutes. Change notifications are only a hint; folders are fully re-checked on watcher errors and every
/// 15–30 minutes, and every condition is re-checked right before acting. Only rules the user switched to
/// automatic run, through <see cref="IFlowHost.RunApprovedAsync"/>; each batch is its own run with its own Undo.
/// Everything runs on a background task: Start/Stop never block the caller (the UI thread).
/// </summary>
public sealed class WatchService : IAsyncDisposable
{
    private readonly OrganizePlanner _planner;
    private readonly IFlowHost _flow;
    private readonly Func<OrganizeSettings> _settings;
    private readonly IClock _clock;
    private readonly WatchTiming _timing;
    private readonly QuietPeriodTracker _tracker;
    /// <summary>Records finished runs (the module adds them to the processed-files ledger).</summary>
    private readonly Action<RunRecord>? _runFinished;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, PlannedAction> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _gate = new();

    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _rescanRequested;

    public WatchService(OrganizePlanner planner, IFlowHost flow, Func<OrganizeSettings> settings, IClock? clock = null, WatchTiming? timing = null, Action<RunRecord>? runFinished = null)
    {
        _planner = planner;
        _flow = flow;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _timing = timing ?? new WatchTiming();
        _tracker = new QuietPeriodTracker(_timing.QuietPeriod);
        _runFinished = runFinished;
    }

    public bool IsRunning { get { lock (_gate) return _loop is { IsCompleted: false }; } }
    public IReadOnlyList<string> Folders => _folders;
    public DateTime? LastCheckUtc { get; private set; }
    public string? LastProblem { get; private set; }
    public int AutomaticRuns { get; private set; }
    public QuietPeriodTracker Tracker => _tracker;

    public IReadOnlyList<PlannedAction> PendingActions { get { lock (_pending) return _pending.Values.ToList(); } }

    /// <summary>Raised on a background thread. UI subscribers must marshal to their dispatcher.</summary>
    public event EventHandler<WatchPendingEventArgs>? PendingChanged;
    /// <summary>Raised on a background thread after every check.</summary>
    public event EventHandler<WatchCycleResult>? Checked;

    public void Start(IReadOnlyList<string> folders)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _folders = folders.Where(Directory.Exists).Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var folder in _folders) _watchers.Add(CreateWatcher(folder));
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            _watchers.Clear();
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Change notification (or test hint): the file was just touched; its quiet period restarts.</summary>
    public void NotifyChanged(string path) => _tracker.Touch(path, _clock.UtcNow);

    /// <summary>Forces the next cycle to re-check every file (used after watcher errors).</summary>
    public void RequestRescan() => _rescanRequested = true;

    /// <summary>A run the user confirmed for watched files (e.g. "Review waiting files"): recorded like automatic ones.</summary>
    public void ReportRun(RunRecord run) => _runFinished?.Invoke(run);

    private FileSystemWatcher CreateWatcher(string folder)
    {
        var w = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        w.Created += (_, e) => NotifyChanged(e.FullPath);
        w.Changed += (_, e) => NotifyChanged(e.FullPath);
        w.Renamed += (_, e) => { _tracker.Forget(e.OldFullPath); NotifyChanged(e.FullPath); };
        w.Deleted += (_, e) => _tracker.Forget(e.FullPath);
        w.Error += (_, _) =>
        {
            // Buffer overflow or the folder went away: events were lost, so look at everything again.
            RequestRescan();
            try { w.EnableRaisingEvents = false; w.EnableRaisingEvents = Directory.Exists(folder); } catch (Exception ex) when (ex is IOException or ArgumentException) { }
        };
        w.EnableRaisingEvents = true;
        return w;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var nextFull = _clock.UtcNow; // first pass is a full check
        using var timer = new PeriodicTimer(_timing.Tick);
        do
        {
            var minutes = SafeSettings().Watch.RescanMinutes;
            var rescanEvery = minutes > 0 ? WatchTiming.ClampRescan(minutes) : _timing.Rescan;
            bool full = _rescanRequested || _clock.UtcNow >= nextFull;
            if (full) { _rescanRequested = false; nextFull = _clock.UtcNow + rescanEvery; }
            try
            {
                await RunCycleAsync(full, ct).ConfigureAwait(false);
                LastProblem = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // One bad cycle must never stop watching; try again with a full check.
                LastProblem = ex.Message;
                _rescanRequested = true;
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private OrganizeSettings SafeSettings()
    {
        try { return _settings(); } catch (Exception) { return new OrganizeSettings(); }
    }

    /// <summary>
    /// One check. <paramref name="fullRescan"/> looks at every loose file; otherwise only files whose
    /// quiet period just ended. Public so tests can drive it with an injected clock.
    /// </summary>
    public async Task<WatchCycleResult> RunCycleAsync(bool fullRescan, CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            IReadOnlySet<string>? only = null;
            if (!fullRescan)
            {
                only = _tracker.Ready(now);
                if (only.Count == 0) return WatchCycleResult.Idle;
            }

            var settings = SafeSettings();
            var preview = await _planner.PlanAsync(new OrganizeRequest
            {
                Roots = _folders,
                Settings = settings,
                Depth = ScanDepth.Quick,
                OnlyPaths = only,
                NotQuietPaths = _tracker.NotQuiet(now),
            }, null, ct).ConfigureAwait(false);
            LastCheckUtc = now;

            // Handled files leave the tracker; ones that still look busy stay until they're quiet.
            var stillBusy = preview.Skipped.Where(s => s.Kind == SkipKind.RecentlyChanged).Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in (only ?? (IEnumerable<string>)preview.Actions.Select(a => a.SourcePath)).Where(p => !stillBusy.Contains(p)))
                _tracker.Forget(path);

            var runs = new List<RunRecord>();
            int automatic = 0;
            foreach (var batch in preview.Actions.Where(a => a.Automatic).GroupBy(a => a.Root, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                // Re-check right before acting: the file must still be exactly what was planned.
                var ready = batch.Where(StillAsPlanned).ToList();
                if (ready.Count == 0) continue;
                var plan = PlanAssembler.Build($"Watch folder: tidied {Count(ready.Count)} in {Path.GetFileName(batch.Key)}", ready);
                var run = await _flow.RunApprovedAsync(plan, ct).ConfigureAwait(false);
                if (run is not null) { runs.Add(run); AutomaticRuns++; automatic += ready.Count; _runFinished?.Invoke(run); }
            }

            UpdatePending(preview.Actions.Where(a => !a.Automatic).ToList(), fullRescan, only);
            var result = new WatchCycleResult(fullRescan, preview.Actions.Count + preview.Skipped.Count, automatic, PendingActions.Count, runs);
            Checked?.Invoke(this, result);
            return result;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private void UpdatePending(IReadOnlyList<PlannedAction> found, bool fullRescan, IReadOnlySet<string>? looked)
    {
        bool changed;
        lock (_pending)
        {
            var before = _pending.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fullRescan) _pending.Clear();
            else if (looked is not null) foreach (var p in looked) _pending.Remove(p);
            foreach (var a in found) _pending[a.SourcePath] = a;
            // Drop anything the user already handled (moved, renamed or deleted it).
            foreach (var gone in _pending.Keys.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()) _pending.Remove(gone);
            changed = !before.SetEquals(_pending.Keys);
        }
        if (changed) PendingChanged?.Invoke(this, new WatchPendingEventArgs(PendingActions));
    }

    private static bool StillAsPlanned(PlannedAction a)
    {
        try
        {
            if (a.IsDirectory) return Directory.Exists(a.SourcePath);
            var info = new FileInfo(a.SourcePath);
            return info.Exists && info.Length == a.Size && info.LastWriteTimeUtc == a.LastWriteUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Count(int n) => n == 1 ? "1 file" : $"{n} files";
}
