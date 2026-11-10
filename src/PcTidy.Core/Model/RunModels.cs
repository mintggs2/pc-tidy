namespace PcTidy.Core.Model;

/// <summary>Journal record for one step of a run. Written BEFORE (Started) and AFTER (Done/Skipped/Failed) the change.</summary>
public sealed record StepRecord
{
    public required int Index { get; init; }
    public required PlanItem Item { get; init; }
    public StepStatus Status { get; init; } = StepStatus.Pending;
    public string? Message { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    /// <summary>Handler-specific data needed to undo this step (quarantine id, prior registry state, final path...).</summary>
    public Dictionary<string, string>? UndoData { get; init; }
    public RegistryPriorState? RegistryPrior { get; init; }
    /// <summary>Bytes actually freed / moved by this step.</summary>
    public long BytesAffected { get; init; }
}

public sealed record RestorePointInfo(bool Created, bool Reused, string? Description, DateTime? CreatedUtc, string? Problem);

public sealed record RunRecord
{
    public required string RunId { get; init; }
    public string? ParentRunId { get; init; }
    public required string FeatureId { get; init; }
    public required string Title { get; init; }
    public ExecutionMode Mode { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; init; }
    public RunStatus Status { get; init; } = RunStatus.Planned;
    public required IReadOnlyList<StepRecord> Steps { get; init; }
    public RestorePointInfo? RestorePoint { get; init; }
    /// <summary>Before/after numbers (free space, startup count...). Keys like "before.freeC", "after.freeC".</summary>
    public Dictionary<string, double>? Metrics { get; init; }
    public string? Machine { get; init; } = Environment.MachineName;
    public string? AppVersion { get; init; }

    public long BytesFreed => Steps.Where(s => s.Status == StepStatus.Done && s.Item.Kind == ActionKind.QuarantinePath).Sum(s => s.BytesAffected);
    public int DoneCount => Steps.Count(s => s.Status == StepStatus.Done);
    public int SkippedCount => Steps.Count(s => s.Status == StepStatus.Skipped);
    public int FailedCount => Steps.Count(s => s.Status == StepStatus.Failed);
}

public sealed record RunProgress(int StepIndex, int StepCount, string Message);

/// <summary>Result of undoing a run.</summary>
public sealed record UndoReport(string RunId, int Restored, int Skipped, int Failed, IReadOnlyList<string> Messages)
{
    public bool Complete => Failed == 0 && Skipped == 0;
}

/// <summary>Quarantine manifest entry for one file or folder.</summary>
public sealed record QuarantineEntry
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required string OriginalPath { get; init; }
    public required string StoredPath { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    /// <summary>Hex XXH3-64 for files (folders: hash of the sorted content manifest).</summary>
    public string? Hash { get; init; }
    public FileAttributes Attributes { get; init; }
    public DateTime CreationUtc { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime QuarantinedUtc { get; init; } = DateTime.UtcNow;
    public bool Restored { get; init; }
    public bool Purged { get; init; }
}

public sealed record InUseInfo(string Path, bool InUse, IReadOnlyList<string> Apps);

public sealed record SafetyVerdict(bool Allowed, string? Reason, PathProtection Protection = PathProtection.None)
{
    public static readonly SafetyVerdict Ok = new(true, null);
    public static SafetyVerdict Block(string reason, PathProtection p = PathProtection.Never) => new(false, reason, p);
}
