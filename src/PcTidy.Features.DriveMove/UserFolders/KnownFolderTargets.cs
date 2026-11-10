using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.PcFacts;
using PcTidy.Safety.Detection;

namespace PcTidy.Features.DriveMove.UserFolders;

/// <summary>One of the six Windows user folders PC Tidy may move with Windows' own known-folder API.</summary>
public sealed record UserFolder(Guid Id, string Name, string CurrentPath, string DefaultPath);

/// <summary>
/// Where a user folder may go (Research.md §7 "The drive-root disaster"): a NEW, EMPTY subfolder on a fixed drive of this PC;
/// never a drive root, never shared with (inside or around) another user folder, never inside OneDrive or another cloud
/// folder, never a removable or network drive. The safety guard checks the same again on the plan item.
/// </summary>
internal static class KnownFolderTargets
{
    public const string NotAPath = "That isn't a full folder path.";
    public const string DriveRoot = "A user folder can't be a whole drive: Windows would show the drive as that folder, and \"Restore default\" would try to move everything on it. Pick a new folder on the drive instead.";
    public const string NotFixed = "Pick a folder on one of this PC's own drives, not a USB stick, card or network drive.";
    public const string InCloud = "That folder is inside OneDrive or another cloud folder. Moving a user folder there fights with the cloud's own backup, so it isn't offered.";
    public const string NotEmpty = "That folder already has files in it. Pick a new, empty folder.";
    public const string IsFile = "There's a file with that name there. Pick a new folder.";
    public const string SamePlace = "That's where the folder already is.";
    public const string IntoItself = "A folder can't move into itself.";

    public static string Shared(string other) => $"That place is (or holds, or is inside) your {other} folder. Each user folder needs its own folder.";

    /// <summary>The six folders, with where Windows has them now and their usual place (%USERPROFILE%\&lt;Name&gt;).</summary>
    public static IReadOnlyList<UserFolder> All(IKnownLocations k) => new[]
    {
        Folder(KnownFolders.Downloads, "Downloads", k.Downloads, k),
        Folder(KnownFolders.Videos, "Videos", k.Videos, k),
        Folder(KnownFolders.Music, "Music", k.Music, k),
        Folder(KnownFolders.Pictures, "Pictures", k.Pictures, k),
        Folder(KnownFolders.Documents, "Documents", k.Documents, k),
        Folder(KnownFolders.Desktop, "Desktop", k.Desktop, k),
    };

    private static UserFolder Folder(Guid id, string name, string current, IKnownLocations k)
        => new(id, name, PathText.TryNormalize(current) ?? current, PathText.TryNormalize(Path.Combine(k.UserProfile, name)) ?? Path.Combine(k.UserProfile, name));

    /// <summary>The suggested new home: &lt;drive&gt;\Users\&lt;your profile folder name&gt;\&lt;Name&gt;.</summary>
    public static string Suggested(DriveFacts drive, string profileFolderName, string name)
        => Path.Combine(drive.Root, "Users", profileFolderName, name);

    /// <summary>Why <paramref name="target"/> can't be the new home of <paramref name="folder"/>, or null when it can.</summary>
    public static string? Problem(string target, UserFolder folder, IReadOnlyList<UserFolder> all, DriveList drives, ISafetyGuard guard, IReadOnlyList<string> cloudRoots)
    {
        var t = PathText.TryNormalize(target);
        if (t is null) return NotAPath;
        if (t.Length <= 3 || drives.All.Any(d => d.Root.Equals(t, StringComparison.OrdinalIgnoreCase))) return DriveRoot;
        var drive = drives.DriveOf(t);
        if (drive is null) return NotFixed;
        if (drive.IsExternal) return drive.NotReceivingReason;
        if (!drive.CanReceive) return NotFixed;
        if (guard.IsInCloudRoot(t) || cloudRoots.Select(PathText.TryNormalize).OfType<string>().Any(c => PathUtil.IsUnder(t, c) || PathUtil.IsUnder(c, t)))
            return InCloud;
        if (t.Equals(folder.CurrentPath, StringComparison.OrdinalIgnoreCase)) return SamePlace;
        if (PathUtil.IsUnder(t, folder.CurrentPath)) return IntoItself;
        if (PathUtil.IsUnder(folder.CurrentPath, t)) return Shared(folder.Name);
        foreach (var other in all.Where(o => o.Id != folder.Id))
            if (PathUtil.IsUnder(t, other.CurrentPath) || PathUtil.IsUnder(other.CurrentPath, t)) return Shared(other.Name);
        if (FolderMeasure.FileExists(t)) return IsFile;
        if (!FolderMeasure.IsMissingOrEmpty(t)) return NotEmpty;
        return null;
    }
}
