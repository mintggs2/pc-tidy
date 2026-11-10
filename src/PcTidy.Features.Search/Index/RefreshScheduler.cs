namespace PcTidy.Features.Search.Index;

/// <summary>Timing rules for keeping the own index fresh.</summary>
public sealed record RefreshPolicy
{
    /// <summary>Wait this long after the last change hint before re-scanning (a busy folder settles first).</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>...but never longer than this after the first hint (a folder that never goes quiet still refreshes).</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>A root is never re-scanned for hints more often than this (a folder that changes all day costs one walk per interval).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Full re-scan of every root this often, hints or not: change notifications can be lost.</summary>
    public TimeSpan PeriodicRescan { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Decides when each root is re-scanned. Change notifications (FileSystemWatcher) are only HINTS: Windows drops them
/// when busy, so a lost-events signal makes a root due straight away and every root is re-scanned periodically anyway.
/// Pure logic over an injected clock, so the debounce can be tested without waiting. Thread-safe.
/// </summary>
public sealed class RefreshScheduler
{
    private sealed class RootState
    {
        public DateTimeOffset LastScan;
        /// <summary>Last walk of the whole root (the periodic rule counts from here, not from small patches).</summary>
        public DateTimeOffset LastFull;
        public DateTimeOffset? FirstHint;
        public DateTimeOffset? LastHint;
        public bool Overflowed;
        /// <summary>Folders whose listing changed (null = unknown: walk the whole root).</summary>
        public HashSet<string>? Folders = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>More changed folders than this in one round, and the whole root is walked instead (cheaper by then).</summary>
    public const int MaxChangedFolders = 256;

    private readonly TimeProvider _clock;
    private readonly RefreshPolicy _policy;
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public RefreshScheduler(TimeProvider clock, RefreshPolicy? policy = null)
    {
        _clock = clock;
        _policy = policy ?? new RefreshPolicy();
    }

    public RefreshPolicy Policy => _policy;

    /// <summary>Starts tracking a root whose index was last built at <paramref name="lastScanUtc"/>.</summary>
    public void Register(string root, DateTimeOffset lastScanUtc)
    {
        lock (_gate) _roots[root] = new RootState { LastScan = lastScanUtc, LastFull = lastScanUtc };
    }

    public void Unregister(string root)
    {
        lock (_gate) _roots.Remove(root);
    }

    /// <summary>
    /// A change notification arrived for <paramref name="changedPath"/> under <paramref name="root"/> (null = somewhere
    /// in it). The folder that holds the changed item is remembered, so only that folder is listed again.
    /// </summary>
    public void Hint(string root, string? changedPath = null)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_roots.TryGetValue(root, out var s)) return;
            s.FirstHint ??= now;
            s.LastHint = now;
            var folder = changedPath is null ? null : Path.GetDirectoryName(changedPath);
            if (folder is null || s.Folders is null) s.Folders = null;
            else if (s.Folders.Add(folder) && s.Folders.Count > MaxChangedFolders) s.Folders = null;
        }
    }

    /// <summary>The watcher lost events (buffer overflow, error): the root is re-scanned at the next check.</summary>
    public void Overflow(string root)
    {
        lock (_gate)
        {
            if (_roots.TryGetValue(root, out var s)) { s.Overflowed = true; s.Folders = null; }
        }
    }

    /// <summary>
    /// Like <see cref="TakeDue"/>, with what changed: the folders to list again, or null for a full walk of the root
    /// (lost events, too many changes, the periodic re-scan, or a root that was never built).
    /// </summary>
    public IReadOnlyList<(string Root, IReadOnlyCollection<string>? Folders)> TakeDueChanges()
    {
        var now = _clock.GetUtcNow();
        var due = new List<(string, IReadOnlyCollection<string>?)>();
        lock (_gate)
        {
            foreach (var (root, s) in _roots)
            {
                if (!IsDue(s, now, out bool periodic)) continue;
                IReadOnlyCollection<string>? folders = periodic || s.Overflowed || s.Folders is not { Count: > 0 } ? null : s.Folders.ToList();
                due.Add((root, folders));
                Reset(s, now);
                if (folders is null) s.LastFull = now;
            }
        }
        return due;
    }

    private bool IsDue(RootState s, DateTimeOffset now, out bool periodic)
    {
        bool hinted = s.LastHint is { } last && s.FirstHint is { } first
                      && (now - last >= _policy.QuietPeriod || now - first >= _policy.MaxDelay);
        bool rested = now - s.LastScan >= _policy.MinInterval;
        periodic = now - s.LastFull >= _policy.PeriodicRescan;
        return periodic || rested && (hinted || s.Overflowed);
    }

    private static void Reset(RootState s, DateTimeOffset now)
    {
        s.FirstHint = s.LastHint = null;
        s.Overflowed = false;
        s.Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Not due again by the periodic rule while this scan runs.
        s.LastScan = now;
    }

    /// <summary>
    /// Roots that should be re-scanned now. Their hints are cleared as they're handed out, so a change that arrives
    /// while the re-scan runs makes the root due again afterwards (nothing is lost). Call <see cref="Completed"/> when done.
    /// </summary>
    public IReadOnlyList<string> TakeDue()
    {
        var now = _clock.GetUtcNow();
        var due = new List<string>();
        lock (_gate)
        {
            foreach (var (root, s) in _roots)
            {
                if (!IsDue(s, now, out _)) continue;
                due.Add(root);
                Reset(s, now);
                s.LastFull = now;
            }
        }
        return due;
    }

    /// <summary>
    /// Records that the re-scan of <paramref name="root"/> finished. After a full walk the periodic timer restarts from
    /// now; after a patch of a few folders it doesn't (lost events are still caught by the periodic full walk).
    /// </summary>
    public void Completed(string root, bool full = true)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_roots.TryGetValue(root, out var s)) return;
            s.LastScan = now;
            if (full) s.LastFull = now;
        }
    }

    /// <summary>True when a root has hints waiting (shown as "Updating soon").</summary>
    public bool HasPending(string root)
    {
        lock (_gate) return _roots.TryGetValue(root, out var s) && (s.LastHint is not null || s.Overflowed);
    }
}
