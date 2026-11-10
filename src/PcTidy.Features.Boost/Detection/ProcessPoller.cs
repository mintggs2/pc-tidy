namespace PcTidy.Features.Boost.Detection;

/// <summary>
/// One running process as Game boost saw it. <see cref="Inspected"/> is false for anti-cheat, Windows and security
/// processes: only their name is known, because they are never opened.
/// </summary>
public sealed record ObservedProcess(int Pid, string Name, string? ImagePath, DateTime? StartUtc, bool Inspected);

/// <summary>
/// Takes a snapshot of this user's running processes. Each process is opened at most once (results are cached by
/// pid + name), only with <see cref="ProcessAccess.QueryLimitedInformation"/>, and only for its exe path and start
/// time. Deny-listed processes and other sign-in sessions are never opened. Nothing is ever changed.
/// </summary>
public sealed class ProcessPoller
{
    private readonly IProcessQuery _query;
    private readonly Dictionary<int, ObservedProcess> _cache = new();
    private readonly object _gate = new();

    public ProcessPoller(IProcessQuery query) => _query = query;

    public IReadOnlyList<ObservedProcess> Snapshot()
    {
        var session = _query.CurrentSessionId;
        var entries = _query.List();
        lock (_gate)
        {
            var seen = new HashSet<int>();
            var result = new List<ObservedProcess>(entries.Count);
            foreach (var e in entries)
            {
                // Idle/System, and anything in another sign-in session (services, other users): not ours to look at.
                if (e.Pid <= 4 || e.SessionId != session || string.IsNullOrEmpty(e.Name)) continue;
                seen.Add(e.Pid);
                if (_cache.TryGetValue(e.Pid, out var known) && known.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(known);
                    continue;
                }
                var observed = Observe(e);
                _cache[e.Pid] = observed;
                result.Add(observed);
            }
            foreach (var gone in _cache.Keys.Where(pid => !seen.Contains(pid)).ToList()) _cache.Remove(gone);
            return result;
        }
    }

    private ObservedProcess Observe(ProcessEntry e)
    {
        if (BoostDenyList.NeverInspect(e.Name)) return new ObservedProcess(e.Pid, e.Name, null, null, Inspected: false);
        ProcessDetails? details = null;
        try { details = _query.Query(e.Pid, ProcessAccess.QueryLimitedInformation); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Ended or refused: the name alone is still useful.
        }
        return new ObservedProcess(e.Pid, e.Name, details?.ImagePath, details?.StartUtc, Inspected: true);
    }
}
