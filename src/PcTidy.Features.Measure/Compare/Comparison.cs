using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Stats;

namespace PcTidy.Features.Measure.Compare;

/// <summary>The verdict chip.</summary>
public enum Verdict { NeedMoreRuns, Better, Worse, NoMeasurableChange, TooNoisy }

/// <summary>Direction of one metric.</summary>
public enum MetricVerdict { NoChange, Better, Worse }

/// <summary>One imported run, as kept with a comparison (numbers only; the frame times themselves aren't stored).</summary>
public sealed record RunSummary
{
    /// <summary>File name only (never the folder).</summary>
    public string FileName { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public RecordingFormat Format { get; init; }
    /// <summary>Stats from the time between Present calls (every file has it).</summary>
    public RunStats Presents { get; init; } = new();
    /// <summary>Stats from the time between frames shown on screen, when the file has it.</summary>
    public RunStats? Display { get; init; }
    public int DroppedFrames { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public RunStats StatsFor(FrameTimeBasis basis) => basis == FrameTimeBasis.Display && Display is not null ? Display : Presents;

    public static RunSummary From(string fileName, RecordingFormat format, ProcessCapture capture) => new()
    {
        FileName = Path.GetFileName(fileName),
        ProcessName = capture.ProcessName,
        Format = format,
        Presents = FrameStats.Compute(capture.PresentMs),
        Display = capture.DisplayMs is { } d ? FrameStats.Compute(d) : null,
        DroppedFrames = capture.DroppedFrames,
        Notes = capture.Notes,
    };
}

/// <summary>Before vs After for one metric.</summary>
public sealed record MetricComparison
{
    public string Name { get; init; } = "";
    public double BeforeMean { get; init; }
    public double AfterMean { get; init; }
    public double Difference => AfterMean - BeforeMean;
    public double DifferencePercent => BeforeMean > 0 ? Difference / BeforeMean * 100.0 : 0;
    /// <summary>The change must be bigger than this (FPS) to count: max(2 × max(pooled run-to-run SD, 1.5% of Before; 4% for lows), floor % of Before).</summary>
    public double Margin { get; init; }
    public double FloorPercent { get; init; }
    public double PooledSd { get; init; }
    public double BeforeSpreadPercent { get; init; }
    public double AfterSpreadPercent { get; init; }
    public bool SameDirectionInEveryPair { get; init; }
    public MetricVerdict Verdict { get; init; }
}

/// <summary>Everything the Compare step shows.</summary>
public sealed record ComparisonResult
{
    public Verdict Verdict { get; init; }
    /// <summary>One plain line with the numbers ("No measurable change: average FPS +0.8 (+0.6%), margin ±2.1 FPS").</summary>
    public string Headline { get; init; } = "";
    /// <summary>Why the verdict is what it is (e.g. which rule stopped a Better/Worse).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    /// <summary>Runs that aren't alike (length, process, tool). Shown as warnings.</summary>
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();
    /// <summary>A/B/A note when a "Before again" set was given, else null.</summary>
    public string? DriftNote { get; init; }
    public FrameTimeBasis Basis { get; init; }
    public MetricComparison? Average { get; init; }
    public MetricComparison? OnePercentLow { get; init; }
    /// <summary>Text-only next step, pointing to History. The page never undoes anything itself.</summary>
    public string Suggestion { get; init; } = "";

    public static string ChipText(Verdict v) => v switch
    {
        Verdict.Better => "Better",
        Verdict.Worse => "Worse",
        Verdict.NoMeasurableChange => "No measurable change",
        Verdict.TooNoisy => "Too noisy to say",
        _ => "Need at least 3 runs each",
    };
}
