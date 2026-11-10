using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Actions;

/// <summary>A fixed drive that can take a move: not the source drive, and has room to spare.</summary>
public sealed record MoveTarget(string DriveRoot, long FreeBytes);

/// <summary>Current size / time of a path, read right before building a plan item (metadata only).</summary>
public interface IPathFacts
{
    (long Size, DateTime LastWriteUtc)? File(string path);
    bool DirectoryExists(string path);
}

public sealed class DiskPathFacts : IPathFacts
{
    public static readonly DiskPathFacts Instance = new();

    public (long Size, DateTime LastWriteUtc)? File(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
}

/// <summary>Outcome of turning a map item into a plan item: the item, or a friendly reason why not.</summary>
public sealed record ActionResult(PlanItem? Item, string? WhyNot)
{
    public bool Ok => Item is not null;
    public static ActionResult No(string why) => new(null, why);
}

/// <summary>
/// Builds the plan items Space map offers (Quarantine, Move to another drive). Never changes anything itself:
/// the items go to IFlowHost.ReviewAndRunAsync, and the engine re-checks every one before it runs.
/// Works on any PC: move targets come only from <see cref="IKnownLocations.FixedDrives"/> that exist and have room.
/// </summary>
public sealed class SpaceActions
{
    public const string FeatureId = "spacemap";
    public const string MovedFolderName = "Moved by PC Tidy";
    /// <summary>A move is only offered if the target keeps at least this much free afterwards.</summary>
    public const long MoveHeadroomBytes = 2L * 1024 * 1024 * 1024;

    private readonly IKnownLocations _locations;
    private readonly ISafetyGuard _safety;
    private readonly IDriveSpaceProbe _drives;
    private readonly IPathFacts _facts;
    private readonly int _quarantineDays;
    private readonly HashSet<string> _knownFolderRoots;

    public SpaceActions(IKnownLocations locations, ISafetyGuard safety, IDriveSpaceProbe drives, int quarantineDays, IPathFacts? facts = null)
    {
        _locations = locations;
        _safety = safety;
        _drives = drives;
        _quarantineDays = quarantineDays;
        _facts = facts ?? DiskPathFacts.Instance;
        _knownFolderRoots = KnownFolderRoots(locations).Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string path) => PathUtil.Normalize(path).TrimEnd('\\');

    /// <summary>The fixed drive (from IKnownLocations) holding <paramref name="path"/>, or null.</summary>
    public string? DriveOf(string path)
    {
        string? best = null;
        foreach (var d in _locations.FixedDrives)
            if (PathUtil.IsUnder(path, d) && (best is null || d.Length > best.Length)) best = d;
        return best;
    }

    public bool IsOnSystemDrive(string path)
    {
        var drive = DriveOf(path);
        return drive is not null && PathUtil.Normalize(drive).Equals(PathUtil.Normalize(_locations.SystemDrive), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Other fixed drives with room for <paramref name="bytes"/> plus headroom, most free space first. Empty on a one-drive PC.</summary>
    public IReadOnlyList<MoveTarget> MoveTargets(string sourcePath, long bytes)
    {
        var source = DriveOf(sourcePath);
        var list = new List<MoveTarget>();
        foreach (var d in _locations.FixedDrives)
        {
            if (source is not null && PathUtil.Normalize(d).Equals(PathUtil.Normalize(source), StringComparison.OrdinalIgnoreCase)) continue;
            if (_drives.ForFixedDrive(d) is not { } space) continue;
            if (space.FreeBytes - bytes >= MoveHeadroomBytes) list.Add(new MoveTarget(d, space.FreeBytes));
        }
        list.Sort((a, b) => b.FreeBytes.CompareTo(a.FreeBytes));
        return list;
    }

    /// <summary>Why this map item can't be acted on at all (lock reason), or null if it can.</summary>
    public string? WhyLocked(SpaceIndex index, int node)
    {
        var flags = index.Flags(node);
        if ((flags & SpaceNodeFlags.Pseudo) != 0)
            return "Space Windows keeps for itself (page file, hibernation, restore points) plus folders PC Tidy can't read. It can't be moved from here.";
        if (node == SpaceIndex.RootNode) return "This is what you scanned. Pick something inside it.";
        var path = index.FullPath(node)!;
        if ((flags & SpaceNodeFlags.Link) != 0) return "This is a link to another place. PC Tidy never follows or moves links.";
        if ((flags & (SpaceNodeFlags.Protected | SpaceNodeFlags.SelfLocked)) != 0)
        {
            string? why = null;
            try { why = _safety.ClassifyPath(path).Reason; } catch (Exception ex) when (ex is not OperationCanceledException) { }
            return why ?? ((flags & SpaceNodeFlags.Protected) != 0
                ? "Protected: Windows, apps and games need this. PC Tidy never touches it."
                : "This folder stays where it is. Move or quarantine what's inside it instead.");
        }
        if ((flags & SpaceNodeFlags.CloudPlaceholder) != 0) return "Online-only OneDrive file: it takes no space on this PC.";
        if (_knownFolderRoots.Contains(Key(path)))
            return "This is a main Windows folder. Move or quarantine what's inside it instead.";
        return null;
    }

    public ActionResult Quarantine(SpaceIndex index, int node)
    {
        if (WhyLocked(index, node) is { } locked) return ActionResult.No(locked);
        var path = index.FullPath(node)!;
        var name = Path.GetFileName(path);
        return Finish(index, node, path, size => new PlanItem
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = FeatureId,
            Description = $"Quarantine '{name}' ({Sizes.Format(size)}). You can restore it for {_quarantineDays} days.",
            SourcePath = path,
        });
    }

    /// <summary>Move into <paramref name="destinationFolder"/> (picked by the user).</summary>
    public ActionResult MoveInto(SpaceIndex index, int node, string destinationFolder)
    {
        if (WhyLocked(index, node) is { } locked) return ActionResult.No(locked);
        var path = index.FullPath(node)!;
        var targets = MoveTargets(path, index.Size(node));
        if (!targets.Any(t => PathUtil.IsUnder(destinationFolder, t.DriveRoot)))
            return ActionResult.No(targets.Count == 0
                ? "No other drive on this PC has room for it. Quarantine is still an option."
                : "Pick a folder on another drive with enough free space.");
        if (PathUtil.IsUnder(destinationFolder, path)) return ActionResult.No("It can't be moved into itself.");
        var destVerdict = SafeClassify(destinationFolder);
        if (destVerdict.Protection == PathProtection.Never)
            return ActionResult.No(destVerdict.Reason ?? "That folder is protected. Pick another one.");
        var name = Path.GetFileName(path);
        var destination = PathUtil.NextFreeName(Path.Combine(destinationFolder, name));
        return Finish(index, node, path, size => new PlanItem
        {
            Kind = ActionKind.MovePath,
            FeatureId = FeatureId,
            Description = $"Move '{name}' ({Sizes.Format(size)}) to {destinationFolder}",
            SourcePath = path,
            DestinationPath = destination,
        });
    }

    /// <summary>Suggested move for "Big and old": same relative path under "&lt;drive&gt;\Moved by PC Tidy\".</summary>
    public ActionResult MoveToDrive(SpaceIndex index, int node, MoveTarget target)
    {
        var path = index.FullPath(node);
        if (path is null || DriveOf(path) is not { } sourceDrive) return ActionResult.No("PC Tidy doesn't know which drive this is on.");
        var relative = Path.GetRelativePath(sourceDrive, Path.GetDirectoryName(path)!);
        var folder = relative == "." ? Path.Combine(target.DriveRoot, MovedFolderName) : Path.Combine(target.DriveRoot, MovedFolderName, relative);
        return MoveInto(index, node, folder);
    }

    private ActionResult Finish(SpaceIndex index, int node, string path, Func<long, PlanItem> make)
    {
        bool isDir = index.IsDirectory(node);
        long size;
        DateTime? lastWrite = null;
        if (isDir)
        {
            if (!_facts.DirectoryExists(path)) return ActionResult.No("It's not there any more. Rescan to refresh the map.");
            size = index.Size(node);
        }
        else
        {
            if (_facts.File(path) is not { } facts) return ActionResult.No("It's not there any more. Rescan to refresh the map.");
            (size, lastWrite) = (facts.Size, facts.LastWriteUtc);
        }
        var item = make(size) with { IsDirectory = isDir, ExpectedSize = size, ExpectedLastWriteUtc = lastWrite };
        SafetyVerdict verdict;
        try { verdict = _safety.Check(item); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ActionResult.No("PC Tidy couldn't check this item, so it leaves it alone."); }
        if (!verdict.Allowed)
            return ActionResult.No(verdict.Reason ?? "PC Tidy's safety rules don't allow this here.");
        return new ActionResult(item, null);
    }

    private SafetyVerdict SafeClassify(string path)
    {
        try { return _safety.ClassifyPath(path); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return SafetyVerdict.Block("PC Tidy couldn't check that folder."); }
    }

    /// <summary>Folders that must never be moved/quarantined as a whole (moving their contents is fine).</summary>
    private static IEnumerable<string> KnownFolderRoots(IKnownLocations l)
    {
        yield return l.UserProfile; yield return l.Desktop; yield return l.Documents; yield return l.Downloads;
        yield return l.Pictures; yield return l.Videos; yield return l.Music; yield return l.LocalAppData;
        yield return l.RoamingAppData; yield return l.ProgramData; yield return l.WindowsDir; yield return l.ProgramFiles;
        yield return l.ProgramFilesX86; yield return l.UserTemp; yield return l.DataRoot;
        yield return Path.GetDirectoryName(PathUtil.Normalize(l.UserProfile)) ?? l.UserProfile; // ...\Users
        foreach (var c in l.CloudRoots) yield return c;
        foreach (var d in l.FixedDrives) yield return d;
    }
}
