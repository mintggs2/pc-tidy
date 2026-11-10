using PcTidy.Features.Learning.Learners;

namespace PcTidy.Features.Learning.Model;

/// <summary>What the user did with a group of suggestions in a confirm step.</summary>
public enum LearningDecision
{
    /// <summary>At least one item of the group was ticked and run.</summary>
    Accept = 0,
    /// <summary>The group was offered and left unticked.</summary>
    Reject = 1,
    /// <summary>The user undid a run that included this group. Counts as two rejections.</summary>
    Undo = 2,
}

/// <summary>One recorded decision (raw event, kept 90 days).</summary>
/// <param name="GroupTitle">The group's title as the user saw it ("Browser caches"), for the learned page.</param>
/// <param name="Example">One item of the group ("Chrome cache"), for the 2–3 examples on the learned page.</param>
public sealed record DecisionEvent(string FeatureId, string GroupId, LearningDecision Decision, DateTime Utc, string? RunId = null, string? Example = null, string? GroupTitle = null);

/// <summary>Totals of decisions older than 90 days, plus the newest of their votes (see <see cref="Learners.SkipCounter"/>).</summary>
public sealed record DecisionTotals(string FeatureId, string GroupId, int Accepts, int Rejects, int Undos, DateTime FirstUtc, DateTime LastUtc, string RecentVotes, string Examples, string? GroupTitle);

/// <summary>One file PC Tidy moved, read from its own run journal (raw event, kept 90 days).</summary>
public sealed record MoveEvent(string RunId, DateTime Utc, string SourcePath, string FinalPath, long Size, string? Host)
{
    /// <summary>Row id in the store (0 before it is saved).</summary>
    public long Id { get; init; }
    public string SourceFolder => Path.GetDirectoryName(SourcePath) ?? "";
    public string DestinationFolder => Path.GetDirectoryName(FinalPath) ?? "";
}

/// <summary>Weighted totals of moves older than 90 days for one feature value and destination.</summary>
public sealed record MoveTotals(MoveFeature Feature, string Value, string Destination, double Weight, int Days, DateTime FirstUtc, DateTime LastUtc, string Examples, string? CommonSource);

/// <summary>Free space on one drive on one day.</summary>
public sealed record FreeSpaceSample(string Drive, DateTime Utc, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>A PC Tidy run that may have changed free space (so its jump is ignored by the fill trend).</summary>
/// <param name="Drives">Drive roots the run touched; "*" = all drives (system tools, Recycle Bin).</param>
public sealed record RunMark(string RunId, DateTime Utc, IReadOnlyList<string> Drives)
{
    public bool Touches(string drive) => Drives.Any(d => d == "*" || d.Equals(drive, StringComparison.OrdinalIgnoreCase));
}

public enum VisitSource { PcTidy = 0, Move = 1, WindowsRecent = 2 }

/// <summary>A folder the user opened or used through PC Tidy (raw event, kept 90 days).</summary>
public sealed record FolderVisit(string Folder, DateTime Utc, VisitSource Source);

/// <summary>Visits older than 90 days folded into one fading score, valid at <see cref="ScoreUtc"/>.</summary>
public sealed record FolderTotals(string Folder, double Score, DateTime ScoreUtc, int Count, DateTime FirstUtc, DateTime LastUtc);

public enum AppSource { UserAssist = 0, Steam = 1, Installed = 2 }

/// <summary>What PC Tidy has seen of one app while watching. No raw events: one row per app, updated in place.</summary>
/// <param name="Key">Stable id: program path, Store app id or "steam:&lt;appid&gt;".</param>
/// <param name="LastRunUtc">Newest launch Windows or Steam reports (may predate watching).</param>
/// <param name="LastCountChangeUtc">When PC Tidy saw the launch count go up.</param>
public sealed record AppObservation(string Key, string Name, AppSource Source, DateTime FirstSeenUtc, int LastCount, DateTime? LastRunUtc, DateTime? LastCountChangeUtc);

/// <summary>An app as a source reports it right now.</summary>
public sealed record AppUsage(string Key, string Name, AppSource Source, int RunCount, DateTime? LastRunUtc);

/// <summary>An installed app another section (Apps) asks PC Tidy to watch, so never-opened apps can be noticed too.</summary>
/// <param name="InstallFolder">Launches of programs inside this folder count as using the app.</param>
public sealed record WatchedApp(string Key, string Name, string? InstallFolder);

/// <summary>Per learned item settings the user changed on the "What PC Tidy has learned" page.</summary>
public sealed record LearnedItemState
{
    public required string ItemId { get; init; }
    /// <summary>"Turn off": kept, but no longer changes defaults or shows suggestions.</summary>
    public bool Off { get; init; }
    /// <summary>Deleted suggestion: never suggested again from the same evidence.</summary>
    public bool Dismissed { get; init; }
    /// <summary>The suggestion was handed to Organize as a rule.</summary>
    public bool MadeRule { get; init; }
    /// <summary>User edits to a suggested rule.</summary>
    public string? EditedName { get; init; }
    public string? EditedDestination { get; init; }
    public DateTime Utc { get; init; }
}

/// <summary>How often the PC was idle in one hour of the day.</summary>
public sealed record IdleHourCounts(int Hour, int IdleSamples, int TotalSamples);
