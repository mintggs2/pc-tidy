namespace PcTidy.Features.Organize.Watch;

/// <summary>
/// Remembers when change notifications last mentioned each file. A file is "quiet" once nothing has
/// happened to it for the quiet period (10 minutes). Notifications are only hints: the planner still
/// checks the file's own times, and full re-scans catch anything a dropped notification missed.
/// </summary>
public sealed class QuietPeriodTracker
{
    private readonly Dictionary<string, DateTime> _lastActivity = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public QuietPeriodTracker(TimeSpan quietPeriod) => QuietPeriod = quietPeriod;

    public TimeSpan QuietPeriod { get; }

    public int Count { get { lock (_gate) return _lastActivity.Count; } }

    public void Touch(string path, DateTime nowUtc)
    {
        lock (_gate) _lastActivity[path] = nowUtc;
    }

    public void Forget(string path)
    {
        lock (_gate) _lastActivity.Remove(path);
    }

    public bool IsQuiet(string path, DateTime nowUtc)
    {
        lock (_gate) return !_lastActivity.TryGetValue(path, out var t) || nowUtc - t >= QuietPeriod;
    }

    /// <summary>Tracked files whose quiet period has passed: ready for a closer look.</summary>
    public IReadOnlySet<string> Ready(DateTime nowUtc)
    {
        lock (_gate)
            return _lastActivity.Where(kv => nowUtc - kv.Value >= QuietPeriod).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tracked files that changed too recently to touch.</summary>
    public IReadOnlySet<string> NotQuiet(DateTime nowUtc)
    {
        lock (_gate)
            return _lastActivity.Where(kv => nowUtc - kv.Value < QuietPeriod).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
