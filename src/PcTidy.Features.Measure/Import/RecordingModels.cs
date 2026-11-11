namespace PcTidy.Features.Measure.Import;

/// <summary>Which tool's file layout a recording was read as.</summary>
public enum RecordingFormat
{
    /// <summary>PresentMon 1.x columns (TimeInSeconds, MsBetweenPresents, MsBetweenDisplayChange, Dropped). OCAT, FrameView and
    /// CapFrameX CSV files use the same PresentMon-based layout; PresentMon 2.x with --v1_metrics writes it too.</summary>
    PresentMonV1Csv,
    /// <summary>PresentMon 2.4+ console (CPUStartTime, MsBetweenPresents, MsBetweenDisplayChange, MsCPUBusy...).</summary>
    PresentMonV2Csv,
    /// <summary>PresentMon 2.0-2.3 console (CPUStartTime, FrameTime, CPUBusy, CPUWait, DisplayedTime with NA for dropped frames).</summary>
    PresentMonV2EarlyCsv,
    /// <summary>CapFrameX session file (.json): Info.ProcessName and Runs[].CaptureData arrays.</summary>
    CapFrameXJson,
}

/// <summary>What the frame times measure.</summary>
public enum FrameTimeBasis
{
    /// <summary>Time between Present calls (MsBetweenPresents / FrameTime): the game's own pacing. Every tool writes it.</summary>
    Presents,
    /// <summary>Time between frames shown on screen (MsBetweenDisplayChange / DisplayedTime): what the eye sees. Not every file has it.</summary>
    Display,
}

/// <summary>Frames of one process (one swap chain) read from a recording file.</summary>
public sealed class ProcessCapture
{
    public required string ProcessName { get; init; }
    /// <summary>Present-to-present frame times in ms (positive values only).</summary>
    public required float[] PresentMs { get; init; }
    /// <summary>Time on screen per displayed frame in ms, or null when the file has no display timing.</summary>
    public float[]? DisplayMs { get; init; }
    /// <summary>Frames the tool marked as dropped (never shown).</summary>
    public int DroppedFrames { get; init; }
    /// <summary>Plain notes for the user (other swap chains ignored, rows skipped, long gaps...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>A file that was read successfully: one entry per process (the user picks one when there are several).</summary>
public sealed class RecordingFile
{
    public required string FileName { get; init; }
    public required RecordingFormat Format { get; init; }
    /// <summary>Most frames first (CSV), or in session order (CapFrameX).</summary>
    public required IReadOnlyList<ProcessCapture> Processes { get; init; }
    /// <summary>
    /// True when every entry is its own run of the same game (a CapFrameX session with several runs): all are added.
    /// False when the entries are different processes in one capture: the user picks one.
    /// </summary>
    public bool RunsAreSeparate { get; init; }
    /// <summary>Rows that couldn't be used (not a number, "NA", zero), across all processes.</summary>
    public long SkippedRows { get; init; }

    public string FormatLabel => RecordingImporter.LabelFor(Format);
}

/// <summary>Result of reading a file: a recording, or a clear message why not.</summary>
public sealed record ImportResult(RecordingFile? File, string? Problem)
{
    public bool Ok => File is not null;
    public static ImportResult Fail(string problem) => new(null, problem);
}

/// <summary>Size, row and time limits for one import. A file over any of them is refused, never half-read silently.</summary>
public sealed record ImportLimits
{
    /// <summary>Largest file read (a 200,000-frame PresentMon 2.x CSV with every column is about 100 MB).</summary>
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>Most frame rows, all processes together.</summary>
    public long MaxRows { get; init; } = 3_000_000;
    /// <summary>Longest single line (a real row is well under 1 KB).</summary>
    public int MaxLineChars { get; init; } = 64 * 1024;
    /// <summary>Most columns in the header.</summary>
    public int MaxColumns { get; init; } = 1024;
    /// <summary>Most distinct processes kept (the rest are counted as skipped).</summary>
    public int MaxProcesses { get; init; } = 64;
    /// <summary>Wall-clock time limit for one import.</summary>
    public TimeSpan MaxTime { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Fewest frames a process needs to be offered as a run.</summary>
    public int MinFrames { get; init; } = 200;

    public static readonly ImportLimits Default = new();
}
