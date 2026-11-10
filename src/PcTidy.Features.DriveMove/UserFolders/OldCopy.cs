using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;

namespace PcTidy.Features.DriveMove.UserFolders;

/// <summary>The quarantine steps for a user folder's old copy, and what they free.</summary>
/// <param name="LeftBehind">Entries that stay in the old place (links, online-only files, anything the safety rules refuse).</param>
internal sealed record OldCopySteps(IReadOnlyList<PlanItem> Steps, long Bytes, int LeftBehind);

/// <summary>
/// Windows' Location move copies a user folder and keeps the old copy (the handler never asks Windows to delete it).
/// The old copy is then cleared by quarantine steps, one per entry at the top of the old folder, each of which only runs if
/// the move finished (<see cref="PlanItem.DependsOn"/>); the handler only finishes after checking every file arrived.
/// The old folder itself stays (empty), so nothing ever asks to quarantine a user folder itself.
/// </summary>
internal static class OldCopy
{
    public static readonly OldCopySteps None = new(Array.Empty<PlanItem>(), 0, 0);

    public static string SpaceNote(string driveName)
        => $"You can still put it back from History; {driveName} gets the space back for good when quarantine is emptied.";

    public static OldCopySteps Plan(DriveMoveContext c, string oldFolder, string folderName, PlanItem redirect, CancellationToken ct)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(oldFolder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return None; }

        var steps = new List<PlanItem>();
        long bytes = 0;
        int left = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            // Links are never followed or moved (they hold no data here, e.g. Documents' old "My Music" links), and
            // desktop.ini only names the old folder: both just stay. Online-only files are never touched.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (CloudFile.IsPlaceholder(entry.Attributes)) { left++; continue; }
            long size = entry is FileInfo f ? f.Length : FolderMeasure.Measure(c.Walker, entry.FullName, ct).Bytes;
            var step = Plans.QuarantineOldCopy(entry, size, folderName, redirect);
            if (!c.Safety.Check(step).Allowed) { left++; continue; }
            steps.Add(step);
            bytes += size;
        }
        return new OldCopySteps(steps, bytes, left);
    }
}
