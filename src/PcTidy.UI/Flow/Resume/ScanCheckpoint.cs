using System.IO;
using System.Text.Json.Serialization;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow.Resume;

/// <summary>
/// What a scan checkpoint belongs to. A saved checkpoint is only ever reused for exactly the same scan: the same flow,
/// the same scanners in the same order, the same depth, preset and folders, the same data folder (or practice PC) and
/// the same PC Tidy version. Anything else and it is thrown away (<see cref="CheckpointRules"/>).
/// </summary>
public sealed record ScanCheckpointKey
{
    /// <summary>"combined" for Scan everything, else the section's module id ("cleanup", "privacy").</summary>
    public required string Flow { get; init; }
    /// <summary>The scanners' FeatureIds in run order (several scanners may share one id).</summary>
    public required IReadOnlyList<string> ScannerIds { get; init; }
    public ScanDepth Depth { get; init; }
    public string? Preset { get; init; }
    /// <summary>Folders the scan was limited to (normalized), empty for the section's usual places.</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public required string DataRoot { get; init; }
    public bool Sandbox { get; init; }
    public required string AppVersion { get; init; }

    public static ScanCheckpointKey Create(string flow, IReadOnlyList<IFeatureScanner> scanners, ScanDepth depth, string? preset,
        IReadOnlyList<string>? roots, IKnownLocations locations, string appVersion) => new()
    {
        Flow = flow,
        ScannerIds = scanners.Select(s => s.FeatureId).ToList(),
        Depth = depth,
        Preset = preset,
        Roots = (roots ?? Array.Empty<string>()).Select(NormalizeRoot).ToList(),
        DataRoot = NormalizeRoot(locations.DataRoot),
        Sandbox = locations.IsSandbox,
        AppVersion = appVersion,
    };

    private static string NormalizeRoot(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path.TrimEnd('\\', '/'); }
    }

    /// <summary>Same scan options (everything but the app version, which <see cref="CheckpointRules"/> reports on its own).</summary>
    public bool SameOptions(ScanCheckpointKey other)
        => string.Equals(Flow, other.Flow, StringComparison.OrdinalIgnoreCase)
           && ScannerIds.SequenceEqual(other.ScannerIds, StringComparer.OrdinalIgnoreCase)
           && Depth == other.Depth
           && string.Equals(Preset ?? "", other.Preset ?? "", StringComparison.OrdinalIgnoreCase)
           && Roots.SequenceEqual(other.Roots, StringComparer.OrdinalIgnoreCase)
           && string.Equals(DataRoot, other.DataRoot, StringComparison.OrdinalIgnoreCase)
           && Sandbox == other.Sandbox;
}

/// <summary>
/// One file (or folder) a finished section's items point at, as it was when the section finished (for the "folders
/// changed a lot" rule). A folder is compared by its last-write time only (it changes when something directly inside
/// it is added, removed or renamed).
/// </summary>
public sealed record FileSample(string Path, long Size, DateTime LastWriteUtc)
{
    public bool IsFolder { get; init; }
}

/// <summary>One finished section of a scan that was saved part way.</summary>
public sealed record CheckpointSection
{
    /// <summary>Position in <see cref="ScanCheckpointKey.ScannerIds"/>.</summary>
    public required int Index { get; init; }
    public required string FeatureId { get; init; }
    public required string Title { get; init; }
    public required DateTime FinishedUtc { get; init; }
    /// <summary>
    /// The section's results; null when they were too big to save (that section is scanned again), and in a header
    /// read without results (<see cref="IScanCheckpointStore.LoadHeader"/>). Never written into the header itself.
    /// </summary>
    public ScanResult? Result { get; init; }
    /// <summary>The file in the flow's section folder that holds <see cref="Result"/> ("3-1a2b3c4d.json"), null when not saved.</summary>
    public string? ResultFile { get; init; }
    /// <summary>Size of <see cref="ResultFile"/> in bytes (for the checkpoint's overall cap).</summary>
    public long ResultBytes { get; init; }
    /// <summary>Up to <see cref="CheckpointRules.SampleSize"/> files from the results, with size and last-write time.</summary>
    public IReadOnlyList<FileSample> Samples { get; init; } = Array.Empty<FileSample>();
    /// <summary>Why <see cref="Result"/> wasn't saved ("too big to save"), else null.</summary>
    public string? NotSavedReason { get; init; }
}

/// <summary>
/// A scan that was saved after each finished section, so it can carry on after a restart, a crash or closing PC Tidy.
/// Lives in &lt;data folder&gt;\scan-checkpoints\ (the practice PC's data folder in practice mode) and nowhere else.
/// </summary>
public sealed record ScanCheckpoint
{
    /// <summary>2 (3.1.1): a header plus one file per section. Format 1 files (results inside) are ignored and removed.</summary>
    public const int CurrentFormat = 2;
    public int Format { get; init; } = CurrentFormat;
    public required ScanCheckpointKey Key { get; init; }
    /// <summary>How the scan is named on Home ("Scan everything", "Clean up").</summary>
    public required string Title { get; init; }
    /// <summary>The shell page that runs this flow ("optimize", or a module id).</summary>
    public required string NavigateTo { get; init; }
    public required DateTime StartedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    /// <summary>Windows build and update revision when the scan started ("CurrentBuild.UBR"), null if it couldn't be read.</summary>
    public string? WindowsBuild { get; init; }
    public required int TotalSections { get; init; }
    /// <summary>True when the user pressed Stop (rather than PC Tidy closing or the PC restarting).</summary>
    public bool StoppedByUser { get; init; }
    public IReadOnlyList<CheckpointSection> Sections { get; init; } = Array.Empty<CheckpointSection>();

    /// <summary>"after 7 of 12 sections"</summary>
    [JsonIgnore] public string ProgressText => $"{Sections.Count} of {TotalSections} section{(TotalSections == 1 ? "" : "s")}";

    /// <summary>When the last section finished (or the scan started, if none did).</summary>
    [JsonIgnore] public DateTime LastProgressUtc => Sections is { Count: > 0 } ? Sections.Where(s => s is not null).Select(s => s.FinishedUtc).DefaultIfEmpty(StartedUtc).Max() : StartedUtc;

    /// <summary>True when some section's results are in their own file but weren't read (a header-only load).</summary>
    [JsonIgnore] public bool NeedsResults => Sections.Any(s => s.Result is null && s.ResultFile is not null);
}
