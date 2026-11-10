using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>The fake D: drive (only when the profile has a second drive). Steam's library on D: is made by GamesArea.</summary>
internal static class SecondDriveArea
{
    public static void Build(Ctx c)
    {
        if (!c.P.SecondDrive) return;
        var d = c.DriveD;
        c.Sparse(Path.Combine(d, "Backups", "old-backup-2024.zip"), Formats.Zip(("README.txt", c.F.Lines("Backup"))), 750L * 1024 * 1024, 500, "big-old,archive");
        // Cross-drive exact duplicate of the holiday photo.
        c.Put(Path.Combine(d, "Photos", "Holiday 2025", "IMG_20250614_101500.jpg"), c.Shared.HolidayPhoto, 135, "picture,photo,exif,duplicate,dup:holiday");
        c.Put(Path.Combine(d, "Media", "Movies", "Home video 2023.mp4"), c.F.Mp4(), 700, "video");
        c.Put(Path.Combine(d, "Media", "Music", "Live set.flac"), c.F.Flac(), 650, "music");
        c.Put(Path.Combine(d, "Work", "archive-2022.zip"), Formats.Zip(("notes.txt", c.F.Lines("Archive 2022"))), 900, "archive");
        c.EmptyDir(Path.Combine(d, "Temp stuff"), 200);
    }
}
