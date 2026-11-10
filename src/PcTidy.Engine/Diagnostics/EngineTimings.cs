using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PcTidy.Engine;

/// <summary>
/// Optional per-phase stopwatch for the engine (perf tests and diagnostics). Off unless an instance is attached to an
/// <see cref="Executor"/>; while a run or undo is in progress it flows to the journal, quarantine and handlers through
/// <see cref="Measure"/>, which costs nothing when no timings are attached.
/// </summary>
internal sealed class EngineTimings
{
    private static readonly AsyncLocal<EngineTimings?> ActiveTimings = new();
    private readonly ConcurrentDictionary<string, (long Ticks, int Count)> _phases = new(StringComparer.Ordinal);

    /// <summary>Times the phase until the returned scope is disposed (no-op when no timings are attached).</summary>
    public static Scope Measure(string phase) => new(ActiveTimings.Value, phase);

    /// <summary>Attaches these timings to the current async flow (the executor does this for a run).</summary>
    public static IDisposable? Attach(EngineTimings? timings)
    {
        if (timings is null) return null;
        var previous = ActiveTimings.Value;
        ActiveTimings.Value = timings;
        return new Restore(previous);
    }

    public void Reset() => _phases.Clear();

    public void Add(string phase, long ticks)
        => _phases.AddOrUpdate(phase, (ticks, 1), (_, v) => (v.Ticks + ticks, v.Count + 1));

    /// <summary>One line per phase: total ms, count, average.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        foreach (var (phase, (ticks, count)) in _phases.OrderByDescending(p => p.Value.Ticks))
        {
            var ms = ticks * 1000.0 / Stopwatch.Frequency;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {phase,-28} {ms,9:0} ms  x{count,-6} avg {ms / Math.Max(1, count),7:0.00} ms"));
        }
        return sb.ToString();
    }

    public readonly struct Scope : IDisposable
    {
        private readonly EngineTimings? _owner;
        private readonly string _phase;
        private readonly long _start;

        public Scope(EngineTimings? owner, string phase)
        {
            _owner = owner;
            _phase = phase;
            _start = owner is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose() => _owner?.Add(_phase, Stopwatch.GetTimestamp() - _start);
    }

    private sealed class Restore(EngineTimings? previous) : IDisposable
    {
        public void Dispose() => ActiveTimings.Value = previous;
    }
}
