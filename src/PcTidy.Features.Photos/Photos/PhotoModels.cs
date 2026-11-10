using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Photos.Photos;

/// <summary>What the photo organizer remembers. Stored with <c>AppSettings.SetFeature("photos", …)</c>; the shell saves.</summary>
public sealed class PhotoSettings
{
    public const string FeatureKey = "photos";

    /// <summary>Folders the user ticked. Null = the default (Pictures).</summary>
    public List<string>? Folders { get; set; }
    /// <summary>Folders the user added with "Add folder" (Pictures, Videos and Downloads are always offered).</summary>
    public List<string> ExtraFolders { get; set; } = new();
    /// <summary>Also sort photos inside subfolders (albums would be emptied into date folders). Off by default.</summary>
    public bool IncludeSubfolders { get; set; }
    public bool FindBlurry { get; set; } = true;
    public bool FindBursts { get; set; } = true;

    public IReadOnlyList<string> FoldersOrDefault(IKnownLocations locations)
        => Folders is { Count: > 0 } chosen ? chosen : new[] { locations.Pictures };

    public static PhotoSettings Load(AppSettings settings)
    {
        PhotoSettings? stored = null;
        try { stored = settings.GetFeature<PhotoSettings>(FeatureKey); }
        catch (System.Text.Json.JsonException) { /* corrupt blob: defaults, never a crash */ }
        if (stored is null) return new PhotoSettings();
        stored.ExtraFolders ??= new();
        return stored;
    }

    /// <summary>Puts the settings into <paramref name="settings"/>. Saving to disk is the shell's job.</summary>
    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}

/// <summary>What to look at and which checks to run.</summary>
public sealed record PhotoRequest
{
    public required IReadOnlyList<string> Roots { get; init; }
    public bool IncludeSubfolders { get; init; }
    public bool FindBlurry { get; init; } = true;
    public bool FindBursts { get; init; } = true;
    public ScanDepth Depth { get; init; } = ScanDepth.Quick;
    /// <summary>Restart Manager check for files open in an app (off only in pure planning tests).</summary>
    public bool CheckInUse { get; init; } = true;

    public static PhotoRequest From(PhotoSettings s, IReadOnlyList<string> roots, ScanDepth depth) => new()
    {
        Roots = roots, IncludeSubfolders = s.IncludeSubfolders, FindBlurry = s.FindBlurry, FindBursts = s.FindBursts, Depth = depth,
    };
}

/// <summary>One photo or video moving into its date folder.</summary>
public sealed record PhotoMove
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string Root { get; init; }
    public required MediaDate Date { get; init; }
    public bool IsVideo { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public bool Renumbered { get; init; }
    public required string Reason { get; init; }
    /// <summary>Shown so you know, but can't be ticked yet (the OneDrive folder isn't allowed).</summary>
    public string? DisabledReason { get; init; }
    /// <summary>CreateDirectory for missing date folders (parents first), then the MovePath.</summary>
    public required IReadOnlyList<PlanItem> Items { get; init; }

    public string Name => Path.GetFileName(SourcePath);
    /// <summary>"2025-06": the month folder it lands in.</summary>
    public string Month => Path.GetFileName(Path.GetDirectoryName(DestinationPath)!);
    public string Year => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(DestinationPath)!)!);
}

public enum PhotoFlagKind
{
    /// <summary>Looks blurry (heuristic).</summary>
    Blurry,
    /// <summary>Part of a burst, and not the sharpest shot.</summary>
    BurstExtra,
}

/// <summary>A photo PC Tidy thinks you may not want. Always "Check" and unticked; quarantine is restorable.</summary>
public sealed record PhotoFlag
{
    public required PhotoFlagKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public required string Root { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public required ImageMetrics Metrics { get; init; }
    /// <summary>Burst only: the shot PC Tidy keeps, and how many shots the burst has.</summary>
    public string? KeptPath { get; init; }
    public int BurstSize { get; init; }
    public required string Reason { get; init; }
    public string? DisabledReason { get; init; }
    public required PlanItem Item { get; init; }

    public string Name => Path.GetFileName(SourcePath);
}

/// <summary>The full "what would happen" of one photo scan. Read-only: nothing has moved.</summary>
public sealed class PhotoPreview
{
    public required IReadOnlyList<RootSummary> Roots { get; init; }
    public required IReadOnlyList<PhotoMove> Moves { get; init; }
    public required IReadOnlyList<PhotoFlag> Flags { get; init; }
    public required IReadOnlyList<SkippedEntry> Skipped { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>Roots in OneDrive the user hasn't allowed yet (everything in them is shown, nothing can be ticked).</summary>
    public required IReadOnlyList<string> NeedsPermission { get; init; }
    /// <summary>Photos measured for blur and bursts.</summary>
    public int Checked { get; init; }
    public ScanDepth Depth { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The chosen moves and quarantines as Organize's preview, so the shared before → after tree can draw them.
    /// </summary>
    public OrganizePreview ToTreePreview(IEnumerable<PhotoMove> moves, IEnumerable<PhotoFlag> quarantined)
        => new()
        {
            Roots = Roots,
            Actions = moves.Select(m => new PlannedAction
                {
                    Kind = PlannedActionKind.Move, SourcePath = m.SourcePath, DestinationPath = m.DestinationPath, Root = m.Root,
                    RuleId = "photos.date", RuleName = "Date taken", Group = m.Year, Size = m.Size, LastWriteUtc = m.LastWriteUtc,
                    Reason = m.Reason, Items = m.Items,
                })
                .Concat(quarantined.Select(f => new PlannedAction
                {
                    Kind = PlannedActionKind.Quarantine, SourcePath = f.SourcePath, Root = f.Root, RuleId = "photos." + f.Kind,
                    RuleName = f.Kind.ToString(), Group = "Quarantine", Size = f.Size, LastWriteUtc = f.LastWriteUtc, Reason = f.Reason,
                    Items = new[] { f.Item },
                }))
                .ToList(),
            Skipped = Skipped,
            Notes = Notes,
            Problems = Array.Empty<PcTidy.Features.Organize.Rules.RuleProblem>(),
            Depth = Depth,
            Duration = Duration,
        };
}
