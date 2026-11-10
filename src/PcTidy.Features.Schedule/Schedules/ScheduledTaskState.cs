namespace PcTidy.Features.Schedule.Schedules;

/// <summary>
/// What Windows' Task Scheduler currently has for one of PC Tidy's own tasks (read-only snapshot).
/// Day/Time are in the same invariant form the planner emits, or null when the trigger isn't one PC Tidy would make.
/// </summary>
public sealed record ScheduledTaskState(
    ScheduleKind Kind,
    string Path,
    bool Enabled,
    string? Day,
    string? Time,
    string? Arguments,
    DateTime? NextRunUtc,
    DateTime? LastRunUtc,
    int? LastResult)
{
    /// <summary>True when the task already runs exactly what the user picked (so no change is needed).</summary>
    public bool Matches(string day, string time)
        => Enabled
           && string.Equals(Day, day, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Time, time, StringComparison.Ordinal)
           && string.Equals(Arguments?.Trim(), ScheduleIds.Arguments(Kind), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads PC Tidy's own tasks from the "\PC Tidy\" folder. Never registers, runs or changes a task.</summary>
public interface IScheduledTaskReader
{
    /// <summary>False when Task Scheduler can't be read here (the page then says so instead of guessing).</summary>
    bool IsAvailable { get; }
    IReadOnlyList<ScheduledTaskState> ReadOwnTasks(CancellationToken ct);
}

/// <summary>Practice mode and tests: no real tasks are read.</summary>
public sealed class NoScheduledTasks : IScheduledTaskReader
{
    public static readonly NoScheduledTasks Instance = new();
    public bool IsAvailable => false;
    public IReadOnlyList<ScheduledTaskState> ReadOwnTasks(CancellationToken ct) => Array.Empty<ScheduledTaskState>();
}
