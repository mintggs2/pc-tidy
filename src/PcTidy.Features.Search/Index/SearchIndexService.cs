using PcTidy.Core;

namespace PcTidy.Features.Search.Index;

/// <summary>What the page shows about the own index ("812,400 items · 61 MB in memory · updated 3 min ago").</summary>
public sealed record IndexStatus(long Entries, long MemoryBytes, double BytesPerEntry, long SnapshotBytes, DateTime? OldestBuildUtc,
    bool Building, string? Message, int Roots);

/// <summary>
/// Keeps PC Tidy's own index: loads the saved snapshot (warm start), builds missing roots, and keeps every root
/// fresh in the background. Change notifications are only hints (see <see cref="RefreshScheduler"/>); a refresh lists
/// again only the folders the hints named (lost events, too many changes and the periodic re-scan walk the whole root)
/// and swaps in a new immutable segment, so searches never see a half-built index.
/// Builds run one at a time on a below-normal background thread, can be cancelled, and never touch user files.
/// </summary>
public sealed class SearchIndexService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly IndexBuilder _builder;
    private readonly SearchIndexStore _store;
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly TimeProvider _clock;
    private readonly IChangeHintSource? _hints;
    private readonly RefreshScheduler _scheduler;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Dictionary<string, IDisposable> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignoredPrefixes = new();
    private readonly object _gate = new();
    private IndexSnapshot _current = IndexSnapshot.Empty;
    private ITimer? _timer;
    private string? _message;
    private int _ticking;
    private bool _disposed;

    /// <param name="roots">Current roots (re-read on every refresh, so Settings changes apply).</param>
    /// <param name="hints">Change-hint source; null = no watching (periodic re-scans only).</param>
    /// <param name="ignoredPrefixes">Folders whose change hints are ignored (PC Tidy's own data, AppData).</param>
    public SearchIndexService(IndexBuilder builder, SearchIndexStore store, Func<IReadOnlyList<string>> roots, TimeProvider clock,
        RefreshPolicy? policy = null, IChangeHintSource? hints = null, IEnumerable<string>? ignoredPrefixes = null)
    {
        _builder = builder;
        _store = store;
        _roots = roots;
        _clock = clock;
        _hints = hints;
        _scheduler = new RefreshScheduler(clock, policy);
        _ignoredPrefixes.Add(store.DataRoot);
        if (ignoredPrefixes is not null) _ignoredPrefixes.AddRange(ignoredPrefixes);
    }

    public IndexSnapshot Current => Volatile.Read(ref _current);
    public RefreshScheduler Scheduler => _scheduler;
    public bool IsBuilding => _buildGate.CurrentCount == 0;

    /// <summary>Raised (on a background thread) after a new snapshot is swapped in.</summary>
    public event Action<IndexSnapshot>? SnapshotChanged;
    /// <summary>Raised (on a background thread) while building.</summary>
    public event Action<IndexProgress>? Progress;

    public IndexStatus Status
    {
        get
        {
            var s = Current;
            return new IndexStatus(s.EntryCount, s.ArrayBytes, s.BytesPerEntry, _store.SnapshotBytes, s.OldestBuildUtc, IsBuilding, _message, s.Segments.Count);
        }
    }

    /// <summary>
    /// Warm start: loads the saved snapshot (if it matches the current roots), then builds whatever is missing in the
    /// background and starts watching. Returns once the snapshot is loaded; building continues afterwards.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var roots = _roots();
        var saved = await Task.Run(() => _store.TryLoad(ct), ct).ConfigureAwait(false);
        if (saved is not null) Swap(saved.Only(roots));
        foreach (var root in roots)
        {
            var built = Current.SegmentFor(root)?.BuiltUtc;
            // A root with no saved segment is due at once (periodic rule: "last scan" long ago).
            _scheduler.Register(root, built is { } b ? new DateTimeOffset(b, TimeSpan.Zero) : DateTimeOffset.MinValue);
            StartWatching(root);
        }
        _timer = _clock.CreateTimer(_ => TickAsync(_life.Token).Forget(), null, TimeSpan.Zero, TickInterval);
    }

    /// <summary>
    /// Re-scans due roots (hinted and quiet, lost events, or periodic). Called by the timer; public so tests can drive
    /// it with a fake clock. Overlapping ticks are skipped.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            SyncRoots();
            var due = _scheduler.TakeDueChanges();
            if (due.Count > 0) await RefreshAsync(due, ct).ConfigureAwait(false);
            else if (_savePending && _clock.GetUtcNow() - _lastSave >= MinSaveInterval) await SaveNowAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The old index keeps answering; the periodic rule tries again later.
            _message = "The index couldn't be updated just now (" + ex.Message + "). It tries again later.";
        }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>Re-walks the given roots (or all) in full now, then saves the snapshot. One build at a time.</summary>
    public Task RefreshAsync(IEnumerable<string>? roots, CancellationToken ct)
        => RefreshAsync((roots ?? _roots()).Select(r => (r, (IReadOnlyCollection<string>?)null)).ToList(), ct);

    /// <summary>
    /// Brings the given roots up to date: a root with known changed folders has only those listed again and patched in
    /// (<see cref="IndexBuilder.Patch"/>); the others are walked in full. A full build is saved at once; small patches
    /// are saved at most every <see cref="MinSaveInterval"/>, so a folder that changes all day doesn't rewrite the
    /// snapshot every few minutes.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<(string Root, IReadOnlyCollection<string>? Folders)> work, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var token = linked.Token;
        await _buildGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool changed = false, fullBuild = false;
            foreach (var (root, folders) in work)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    _message = $"{IndexBuilder.RootLabel(root)} isn't there right now; its old entries were kept.";
                    continue;
                }
                var old = Current.SegmentFor(root);
                IndexSegment? segment = null;
                if (folders is { Count: > 0 } && old is not null) segment = await PatchInBackground(old, folders, token).ConfigureAwait(false);
                bool full = segment is null;
                segment ??= await BuildInBackground(root, token).ConfigureAwait(false);
                fullBuild |= full;
                Swap(Current.With(segment));
                _scheduler.Completed(root, full);
                changed = true;
            }
            if (changed)
            {
                _message = null;
                _savePending = true;
                if (fullBuild || _clock.GetUtcNow() - _lastSave >= MinSaveInterval) await SaveCoreAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index in memory is fine; only the warm-start copy couldn't be saved.
            _message = "The index works, but its saved copy couldn't be written (" + ex.Message + ").";
        }
        finally { _buildGate.Release(); }
    }

    /// <summary>Small changes are saved at most this often (the snapshot of a big drive is tens of MB).</summary>
    public static readonly TimeSpan MinSaveInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private volatile bool _savePending;

    private async Task SaveNowAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        await _buildGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await SaveCoreAsync(linked.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message = "The index works, but its saved copy couldn't be written (" + ex.Message + ").";
        }
        finally { _buildGate.Release(); }
    }

    /// <summary>Under the build gate.</summary>
    private async Task SaveCoreAsync(CancellationToken ct)
    {
        await _store.SaveAsync(Current, ct).ConfigureAwait(false);
        _lastSave = _clock.GetUtcNow();
        _savePending = false;
    }

    private Task<IndexSegment> BuildInBackground(string root, CancellationToken ct)
        => Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var progress = new SyncProgress(p => Progress?.Invoke(p));
            return _builder.Build(root, _clock.GetUtcNow().UtcDateTime, progress, ct, AddIgnored);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private Task<IndexSegment?> PatchInBackground(IndexSegment old, IReadOnlyCollection<string> folders, CancellationToken ct)
        => Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return _builder.Patch(old, folders, _clock.GetUtcNow().UtcDateTime, ct, AddIgnored);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Roots added in Settings start being tracked; removed ones disappear from results.</summary>
    private void SyncRoots()
    {
        var roots = _roots();
        var trimmed = Current.Only(roots);
        if (!ReferenceEquals(trimmed, Current)) Swap(trimmed);
        lock (_gate)
        {
            foreach (var gone in _watches.Keys.Where(k => !roots.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                _watches[gone].Dispose();
                _watches.Remove(gone);
                _scheduler.Unregister(gone);
            }
        }
        foreach (var root in roots)
        {
            bool known;
            lock (_gate) known = _watches.ContainsKey(root);
            if (known) continue;
            _scheduler.Register(root, DateTimeOffset.MinValue);
            StartWatching(root);
        }
    }

    private void StartWatching(string root)
    {
        lock (_gate)
        {
            if (_watches.ContainsKey(root)) return;
            _watches[root] = _hints?.Watch(root, OnHint, _scheduler.Overflow) ?? NullDisposable.Instance;
        }
    }

    /// <summary>A change inside a folder that isn't indexed (PC Tidy's own snapshot, AppData) is not a reason to re-scan.</summary>
    internal void OnHint(string root, string changedPath)
    {
        lock (_gate)
        {
            if (_ignoredPrefixes.Any(p => PathUtil.IsUnder(changedPath, p))) return;
        }
        _scheduler.Hint(root, changedPath);
    }

    private void AddIgnored(string folder)
    {
        lock (_gate)
        {
            if (_ignoredPrefixes.Count < 10_000 && !_ignoredPrefixes.Contains(folder, StringComparer.OrdinalIgnoreCase)) _ignoredPrefixes.Add(folder);
        }
    }

    private void Swap(IndexSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Changes not saved yet (small patches wait for MinSaveInterval) go into the warm-start copy on the way out,
        // unless a build is running right now (it's cancelled; the next start re-checks anyway).
        if (_savePending && _buildGate.Wait(0))
        {
            try { _store.SaveAsync(Current, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next start walks again */ }
            finally { _buildGate.Release(); }
        }
        _life.Cancel();
        _timer?.Dispose();
        lock (_gate)
        {
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
        _life.Dispose();
    }

    /// <summary>Progress that reports on the building thread (the page marshals to the UI itself).</summary>
    private sealed class SyncProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

internal static class TaskExtensions
{
    /// <summary>Background refreshes report problems through the status line, never as unobserved exceptions.</summary>
    public static async void Forget(this Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* shown via IndexStatus.Message where it matters; a later tick retries */ }
    }
}
