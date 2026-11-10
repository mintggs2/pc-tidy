namespace PcTidy.Features.Learning;

/// <summary>Current time, injectable so half-lives, retention and the 30-day rule can be tested deterministically.</summary>
public interface ILearningClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemLearningClock : ILearningClock
{
    public static readonly SystemLearningClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>The thresholds from Plan §8 "Learns from me" and Research §8, in one place.</summary>
public static class LearningRules
{
    /// <summary>Raw events older than this are folded into totals.</summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromDays(90);

    /// <summary>Skip counter: the last N votes must all be skips.</summary>
    public const int SkipWindow = 3;

    /// <summary>Rule suggestions: at least this many (weighted) moves to the same place…</summary>
    public const double MinRuleMoves = 5;
    /// <summary>…at least this share of all moves with the same feature value…</summary>
    public const double MinRuleConfidence = 0.8;
    /// <summary>…on at least this many different days.</summary>
    public const int MinRuleDays = 2;
    /// <summary>A run that moved more than this many files with the same feature is a one-off tidy and counts once.</summary>
    public const int BulkSessionMoves = 10;

    /// <summary>Folder frecency: a visit's weight halves every 14 days.</summary>
    public static readonly TimeSpan FolderHalfLife = TimeSpan.FromDays(14);

    /// <summary>Unused apps: never claimed before PC Tidy has watched this long.</summary>
    public static readonly TimeSpan MinAppWatch = TimeSpan.FromDays(30);
    /// <summary>An app not opened for this long (while watched) is flagged.</summary>
    public static readonly TimeSpan AppUnusedAfter = TimeSpan.FromDays(30);

    /// <summary>Drive forecast: daily samples needed before "full in about N days" is said.</summary>
    public const int MinDriveSamples = 14;
    /// <summary>Fill trend window.</summary>
    public static readonly TimeSpan DriveTrendWindow = TimeSpan.FromDays(90);
}
