namespace PcTidy.Features.Schedule.Storage;

/// <summary>When a metrics sample was taken.</summary>
public enum SampleKind { Before, After, Daily }

/// <summary>One before/after/daily sample in the history graph.</summary>
public sealed record MetricsHistoryEntry
{
    public required DateTime TimeUtc { get; init; }
    public required SampleKind Kind { get; init; }
    /// <summary>The run it belongs to (Before/After), else null.</summary>
    public string? RunId { get; init; }
    public required Dictionary<string, double> Values { get; init; }
}

/// <summary>What one scheduled scan or checkup found (for "what built up since last time").</summary>
public sealed record ScanSummary
{
    public required DateTime TimeUtc { get; init; }
    public bool Checkup { get; init; }
    /// <summary>Bytes the Safe default selection would clean (same counting as the bottom bar).</summary>
    public long CleanBytes { get; init; }
    /// <summary>Clean bytes per section (feature id → bytes).</summary>
    public Dictionary<string, long> BySection { get; init; } = new();
    public int DriftCount { get; init; }
    public string? ReportPath { get; init; }
}

public enum ReportKind { Run, Scan, Checkup }

/// <summary>A saved report in &lt;DataRoot&gt;\reports.</summary>
public sealed record ReportInfo(string MarkdownPath, string? HtmlPath, DateTime TimeUtc, string Title, ReportKind Kind);
