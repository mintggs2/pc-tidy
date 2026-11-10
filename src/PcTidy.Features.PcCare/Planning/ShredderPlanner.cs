using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Scanning;

namespace PcTidy.Features.PcCare.Planning;

/// <summary>
/// Builds shredder rows for files the user picked. Each file is checked read-only (exists, a plain file, not a link, not an
/// online-only OneDrive file) and against the real SafetyGuard with the exact ShredFile item that would run, so a
/// protected file shows greyed out with the guard's reason. Every offered row is Permanent and never ticked.
/// </summary>
public static class ShredderPlanner
{
    public const string TabNote =
        "Shredding overwrites a file, then deletes it. There's no quarantine and no undo. On SSDs Windows and the drive may keep " +
        "copies elsewhere, so overwriting isn't a guarantee there: BitLocker device encryption is the honest protection.";

    public const string SsdExplanation =
        "SSDs already erase deleted data in the background (TRIM), and they move data around, so overwriting free space can't reach " +
        "every copy and only wears the drive. For real protection, turn on BitLocker or device encryption (Settings > Privacy & security).";

    /// <summary>The words the user must tick on the page before shredding is offered (the confirm dialog asks again).</summary>
    public const string PageAcknowledgement = "I understand shredded files can't be recovered, not even by PC Tidy";

    public static ScanGroup ForFiles(IEnumerable<string> paths, ISafetyGuard guard, IReadOnlyList<DriveDetails> drives)
    {
        var rows = new List<ScanItem>();
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Row(raw, guard, drives) is { } row) rows.Add(row);
        return PcCareResultBuilder.Group(PcCareTabs.Shredder, "files", "Files to shred",
            "Files you picked. Each one is overwritten, then deleted. Can't be undone.", Badge.Permanent, rows);
    }

    private static ScanItem? Row(string path, ISafetyGuard guard, IReadOnlyList<DriveDetails> drives)
    {
        string name = Path.GetFileName(path);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return Directory.Exists(path)
                    ? PcCareResultBuilder.Disabled(name, "A folder.", "Only single files can be shredded, never whole folders.", Badge.Permanent, detail: path)
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PcCareResultBuilder.Disabled(name, "PC Tidy couldn't read this file.", "PC Tidy couldn't check this file, so it isn't offered.", Badge.Permanent, detail: path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && !CloudFile.IsPlaceholder(info.Attributes))
            return PcCareResultBuilder.Disabled(name, "A link to another place.", "Links are never followed or shredded.", Badge.Permanent, detail: path);
        if (CloudFile.IsPlaceholder(info.Attributes))
            return PcCareResultBuilder.Disabled(name, "An online-only OneDrive file.", "Online-only files live in the cloud; delete them in OneDrive instead.", Badge.Permanent, detail: path);

        var action = PcCareActions.Shred(info.FullName, info.Length, info.LastWriteTimeUtc);
        var verdict = guard.Check(action);
        if (!verdict.Allowed)
            return PcCareResultBuilder.Disabled(name, "PC Tidy protects this file.", verdict.Reason ?? "PC Tidy protects this place.", Badge.Permanent, detail: path);

        var media = drives.Where(d => PathUtil.IsUnder(info.FullName, d.Root)).OrderByDescending(d => d.Root.Length).FirstOrDefault()?.Media;
        var reason = "Overwritten, then deleted. No quarantine, no undo."
                     + (media == DriveMedia.Ssd ? " This is an SSD: the drive may keep old copies the overwrite can't reach." : "");
        return new ScanItem
        {
            Title = name, Detail = path, Reason = reason, Badge = Badge.Permanent, DefaultSelected = false,
            SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc, Actions = new[] { action },
        };
    }
}
