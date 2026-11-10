using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;

namespace PcTidy.Features.Photos.Shared;

/// <summary>
/// The safety guard grants ask-first permissions per <see cref="PlanItem.FeatureId"/>. Sorting photos and the
/// Desktop are organize moves inside the user's own folders, so their plan items carry Organize's id and get exactly
/// Organize's rights: the user libraries yes; OneDrive folders and code projects only when the user allowed the folder.
/// The run itself (<see cref="ActionPlan.FeatureId"/>) keeps the module's own id, so History names the right section.
/// </summary>
public static class SortPermissions
{
    public const string PlanItemFeatureId = OrganizeModule.FeatureId;
}

/// <summary>Is a folder's content in a cloud-synced folder, and did the user say PC Tidy may sort it?</summary>
public enum FolderAccess
{
    /// <summary>An ordinary local folder.</summary>
    Local,
    /// <summary>In OneDrive (or another sync folder) and the user allowed it.</summary>
    AllowedCloud,
    /// <summary>In OneDrive and not allowed yet: PC Tidy shows what would happen, but nothing can be ticked.</summary>
    NeedsPermission,
}

public static class CloudFolders
{
    /// <summary>
    /// Asks about the folder's CONTENTS (a probe path inside it), never the user folder itself, which the guard
    /// treats as "the folder stays where it is".
    /// </summary>
    public static FolderAccess AccessFor(string folder, ISafetyGuard safety, AppSettings settings)
    {
        if (!safety.IsInCloudRoot(Path.Combine(folder, "pc-tidy-probe.tmp"))) return FolderAccess.Local;
        return IsAllowed(folder, settings) ? FolderAccess.AllowedCloud : FolderAccess.NeedsPermission;
    }

    public static bool IsAllowed(string folder, AppSettings settings)
        => settings.AllowedAskFirstFolders.ToArray().Any(f => PathUtil.IsUnder(folder, f));

    /// <summary>
    /// The user's explicit "yes, sort this OneDrive folder": the same list as Settings → "OneDrive folders PC Tidy may
    /// organize" (the shell saves settings). Moves still stay inside the folder; online-only files are still skipped.
    /// </summary>
    public static void Allow(string folder, AppSettings settings)
    {
        if (!IsAllowed(folder, settings)) settings.AllowedAskFirstFolders.Add(PathUtil.Normalize(folder));
    }

    public const string QuarantineNote = " It's in OneDrive: quarantining removes it from OneDrive on all your devices.";
}

/// <summary>Destination names claimed by one planning pass (case-insensitive, like NTFS), so two files never get the same name.</summary>
public sealed class DestinationNames
{
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The desired path, or "name (2).ext", "name (3).ext"… when it's on disk or already claimed.</summary>
    public string Claim(string desiredPath, out bool renumbered)
    {
        renumbered = false;
        var final = desiredPath;
        if (IsTaken(final))
        {
            var dir = Path.GetDirectoryName(desiredPath)!;
            var stem = Path.GetFileNameWithoutExtension(desiredPath);
            var ext = Path.GetExtension(desiredPath);
            for (int i = 2; ; i++)
            {
                if (i > 100_000) throw new IOException("No free name for " + desiredPath);
                final = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!IsTaken(final)) break;
            }
            renumbered = true;
        }
        _taken.Add(final);
        return final;
    }

    private bool IsTaken(string path) => _taken.Contains(path) || File.Exists(path) || Directory.Exists(path);
}

public static class PlanFolders
{
    /// <summary>CreateDirectory items for every missing level of <paramref name="folder"/>, parents first (undo removes them only if empty).</summary>
    public static IEnumerable<PlanItem> Missing(string folder)
    {
        var missing = new Stack<string>();
        for (var d = folder; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        return missing.Select(d => new PlanItem
        {
            Kind = ActionKind.CreateDirectory,
            FeatureId = SortPermissions.PlanItemFeatureId,
            Description = $"Create folder \"{Path.GetFileName(d)}\"",
            DestinationPath = d,
            IsDirectory = true,
        }).ToList();
    }

    /// <summary>
    /// Folders first (de-duplicated, parents before children), then everything else in order. A file that is both
    /// quarantined and moved keeps only the quarantine: the user's latest, stricter choice.
    /// </summary>
    public static IReadOnlyList<PlanItem> Order(IEnumerable<PlanItem> items)
    {
        var list = items.DistinctBy(i => i.Id).ToList();
        var quarantined = new HashSet<string>(list.Where(i => i.Kind == ActionKind.QuarantinePath && i.SourcePath is not null)
            .Select(i => PathUtil.Normalize(i.SourcePath!)), StringComparer.OrdinalIgnoreCase);
        var others = list.Where(i => i.Kind != ActionKind.CreateDirectory)
            .Where(i => !(i.Kind == ActionKind.MovePath && i.SourcePath is not null && quarantined.Contains(PathUtil.Normalize(i.SourcePath))))
            .ToList();
        // Only folders a remaining move lands in (or below) are still needed.
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in others.Where(i => i.Kind == ActionKind.MovePath && i.DestinationPath is not null))
            for (var d = Path.GetDirectoryName(PathUtil.Normalize(move.DestinationPath!)); d is not null && needed.Add(d); d = Path.GetDirectoryName(d)) { }
        var folders = list.Where(i => i.Kind == ActionKind.CreateDirectory && i.DestinationPath is not null)
            .Where(f => needed.Contains(PathUtil.Normalize(f.DestinationPath!)))
            .DistinctBy(i => PathUtil.Normalize(i.DestinationPath!), StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.DestinationPath!.Length)
            .ThenBy(i => i.DestinationPath, StringComparer.OrdinalIgnoreCase);
        return folders.Concat(others).ToList();
    }
}
