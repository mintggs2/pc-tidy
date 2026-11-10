using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Planning;

/// <summary>What to organize and how. Built by the page, the scanner and watch mode.</summary>
public sealed record OrganizeRequest
{
    public required IReadOnlyList<string> Roots { get; init; }
    public required OrganizeSettings Settings { get; init; }
    public ScanDepth Depth { get; init; } = ScanDepth.Quick;
    /// <summary>Restart Manager check for files held open by an app (skip only in pure rule tests).</summary>
    public bool CheckInUse { get; init; } = true;
    /// <summary>Watch mode: only look at these full paths (null = every loose item).</summary>
    public IReadOnlySet<string>? OnlyPaths { get; init; }
    /// <summary>Watch mode: paths that changed recently according to change notifications.</summary>
    public IReadOnlySet<string>? NotQuietPaths { get; init; }
    /// <summary>Culture for month names in folder names (default: current culture).</summary>
    public CultureInfo? Culture { get; init; }
}

public enum PlannedActionKind { Move, MoveFolder, Quarantine, DuplicateQuarantine, Rename }

/// <summary>One file (or whole folder) that a rule will act on, with the plan items that carry it out.</summary>
public sealed record PlannedAction
{
    public required PlannedActionKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public required string Root { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    /// <summary>Category / group label: "Pictures", "Documents", "Quarantine", "Duplicates"…</summary>
    public required string Group { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>A different file already had the name, so this one gets "name (2).ext".</summary>
    public bool Renumbered { get; init; }
    /// <summary>Path of the identical file already at the destination (duplicates only).</summary>
    public string? DuplicateOf { get; init; }
    /// <summary>Why the rule matched, with real values: "Rule \"Installers\": extension .msi, added 45 days ago."</summary>
    public string Why { get; init; } = "";
    /// <summary>Full explanation for the row: why it matched plus what happens.</summary>
    public required string Reason { get; init; }
    public Badge Badge { get; init; } = Badge.Safe;
    public bool DefaultSelected { get; init; } = true;
    /// <summary>Watch mode may run it without asking (rule switched to automatic, and the action is allowed to be).</summary>
    public bool Automatic { get; init; }
    /// <summary>CreateDirectory items for missing folders (parents first), then the move/rename/quarantine.</summary>
    public required IReadOnlyList<PlanItem> Items { get; init; }

    public string Name => Path.GetFileName(SourcePath);
}

public sealed record SkippedEntry(string Path, string Root, bool IsDirectory, SkipKind Kind, string Reason, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record RootSummary(string Root, string DisplayName, int LooseFiles, int LooseFolders, long LooseBytes, string? SkipReason);

/// <summary>The full "what would happen" for one organize scan. Read-only; nothing has moved.</summary>
public sealed class OrganizePreview
{
    public required IReadOnlyList<RootSummary> Roots { get; init; }
    public required IReadOnlyList<PlannedAction> Actions { get; init; }
    public required IReadOnlyList<SkippedEntry> Skipped { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<RuleProblem> Problems { get; init; }
    public ScanDepth Depth { get; init; }
    public TimeSpan Duration { get; init; }

    public IEnumerable<PlannedAction> Moves => Actions.Where(a => a.Kind is PlannedActionKind.Move or PlannedActionKind.MoveFolder);
    public int MoveCount => Moves.Count();
    public long MoveBytes => Moves.Sum(a => a.Size);
}

/// <summary>Short display paths: "Downloads\Pictures\cat.jpg" instead of the full path.</summary>
public static class DisplayPaths
{
    public static string Short(string path, IEnumerable<string> roots)
    {
        foreach (var root in roots.OrderByDescending(r => r.Length))
        {
            if (!PathUtil.IsUnder(path, root)) continue;
            var name = Path.GetFileName(PathUtil.Normalize(root));
            var rel = Path.GetRelativePath(root, path);
            return rel == "." ? name : Path.Combine(name, rel);
        }
        return path;
    }
}
