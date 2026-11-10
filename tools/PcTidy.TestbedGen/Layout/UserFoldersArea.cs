using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Desktop / Documents / Pictures (inside OneDrive when the profile redirects them), Videos, Music,
/// OneDrive's own folders and the simulated online-only placeholders.</summary>
internal static class UserFoldersArea
{
    public static void Build(Ctx c)
    {
        Desktop(c, c.K.Desktop);
        Documents(c, c.K.Documents);
        Pictures(c, c.K.Pictures);
        Videos(c, c.K.Videos);
        Music(c, c.K.Music);
        if (c.P.OneDrive) OneDrive(c);
        if (!c.P.SecondDrive)
            c.Sparse(Path.Combine(c.K.UserProfile, "Backups", "old-backup-2024.zip"), Formats.Zip(("README.txt", c.F.Lines("Backup"))), 750L * 1024 * 1024, 500, "big-old,archive");
    }

    private static void Desktop(Ctx c, string d)
    {
        c.Put(Path.Combine(d, "FakeApp.lnk"), Formats.Lnk(Path.Combine(c.K.ProgramFiles, "FakeApp", "FakeApp.exe")), 100, "shortcut");
        if (c.P.Steam) c.Put(Path.Combine(d, "Steam.lnk"), Formats.Lnk(Path.Combine(c.K.SteamRoot!, "steam.exe")), 300, "shortcut");
        c.Put(Path.Combine(d, "Broken Game.lnk"), Formats.Lnk(Path.Combine(c.K.ProgramFiles, "Uninstalled Game", "game.exe")), 250, "shortcut,broken-shortcut");
        c.Put(Path.Combine(d, "Recipes.url"), Formats.Url("https://example.com/recipes"), 90, "shortcut");
        c.Put(Path.Combine(d, "desktop.ini"), Fake.Text("[.ShellClassInfo]\r\n"), 500, "desktop-ini,hidden,system", FileAttributes.Hidden | FileAttributes.System);
        c.Default(Path.Combine(d, "desktop.ini"), "desktop.ini is never moved");
        c.Put(Path.Combine(d, "Untitled.txt"), c.F.Lines("Untitled"), 45, "document,clutter");
        c.Put(Path.Combine(d, "todo.txt"), c.F.Lines("TODO"), 1, "document,clutter");
        c.Put(Path.Combine(d, "Screenshot 2026-11-01 120000.png"), c.Png(c.Seed + 60), 9, "picture,screenshot,clutter");
        c.Put(Path.Combine(d, "Report final - Copy.docx"), c.Shared.ReportDocx, 40, "document,duplicate,dup:report,clutter");
        c.Put(Path.Combine(d, "beach-small.jpg"), c.Shared.BeachResized, 18, "picture,near-duplicate,near:beach,clutter");
        c.Put(Path.Combine(d, "budget 2026.xlsx"), Formats.OfficeShell("xl/workbook.xml", c.F.Lines("<workbook/>")), 15, "document,clutter");
        c.Put(Path.Combine(d, "setup-tool.exe"), c.F.Exe(), 70, "installer,old,clutter");
        c.EmptyDir(Path.Combine(d, "New folder"), 30, "empty-folder,clutter");
        c.Put(Path.Combine(d, "Stuff to sort", "random.pdf"), Formats.Pdf("random"), 120, "document,clutter");
    }

    private static void Documents(Ctx c, string d)
    {
        c.Put(Path.Combine(d, "Work", "Report final.docx"), c.Shared.ReportDocx, 55, "document,duplicate,dup:report");
        c.Put(Path.Combine(d, "Work", "Quarterly plan.pptx"), Formats.OfficeShell("ppt/presentation.xml", c.F.Lines("<presentation/>")), 80, "document");
        c.Put(Path.Combine(d, "Invoices", "invoice-2024-11.pdf"), Formats.Pdf("Invoice 2024-11"), 360, "document,pdf,invoice");
        c.Put(Path.Combine(d, "Taxes", "2024 tax return.pdf"), Formats.Pdf("Tax return 2024"), 200, "document,pdf");
        c.EmptyDir(Path.Combine(d, "Empty Project"), 400);
        c.Canary(Path.Combine(d, "My Games", "SomeGame", "Saves", "save1.dat"), c.F.Blob(5_000, 20_000), 4, "Game save in Documents");
        c.Canary(Path.Combine(d, "My Games", "SomeGame", "Config", "settings.ini"), c.F.Lines("[Graphics]"), 4, "Game settings in Documents");
        c.Put(Path.Combine(d, "Notes", "readme.txt"), c.F.Lines("Notes index"), 20, "document");
    }

    private static void Pictures(Ctx c, string p)
    {
        var holiday = Path.Combine(p, "Holiday 2025");
        c.Put(Path.Combine(holiday, "IMG_20250614_101500.jpg"), c.Shared.HolidayPhoto, 140, "picture,photo,exif,duplicate,dup:holiday");
        c.Put(Path.Combine(holiday, "IMG_20250614_101500 (1).jpg"), c.Shared.HolidayPhoto, 139, "picture,photo,exif,duplicate,dup:holiday");
        c.Put(Path.Combine(holiday, "beach.jpg"), c.Shared.BeachOriginal, 140, "picture,photo,exif,near-duplicate,near:beach,near-original");
        for (int i = 0; i < 4; i++)
        {
            var taken = new DateTime(2025, 6, 15 + i, 9 + i, 30, 0);
            c.Put(Path.Combine(holiday, $"IMG_{taken:yyyyMMdd_HHmmss}.jpg"), c.Photo(c.Seed + 70 + i, taken), 140 - i, "picture,photo,exif");
        }
        var shots = Path.Combine(p, "Screenshots");
        for (int i = 0; i < 3; i++) c.Put(Path.Combine(shots, $"Screenshot 2026-0{i + 1}-10 1{i}0000.png"), c.Png(c.Seed + 80 + i), 300 - i * 30, "picture,screenshot");
        c.Put(Path.Combine(p, "Saved Pictures", "meme.gif"), c.F.Gif(), 200, "picture");
        c.Put(Path.Combine(p, "Wallpapers", "wallpaper.jpg"), c.Photo(c.Seed + 90, null, 90, 1.0, 192, 108), 500, "picture");
    }

    private static void Videos(Ctx c, string v)
    {
        c.Sparse(Path.Combine(v, "Old screen recording 2025-03.mp4"), c.F.Mp4(4_096, 4_097), 700L * 1024 * 1024, 380, "big-old,video");
        var captures = Path.Combine(v, "Captures");
        for (int i = 0; i < 3; i++)
            c.Put(Path.Combine(captures, $"SomeGame 2026-10-0{i + 2} 21-1{i}-00.mp4"), c.F.Mp4(), 35 - i * 10, "video,game-clip");
        c.Put(Path.Combine(captures, "SomeGame 2026-10-02 21-10-00.png"), c.Png(c.Seed + 95), 35, "picture,game-clip");
        c.Put(Path.Combine(v, "Movie.mkv"), c.F.Mkv(), 250, "video");
    }

    private static void Music(Ctx c, string m)
    {
        for (int i = 1; i <= 4; i++) c.Put(Path.Combine(m, "Fake Artist", "Fake Album", $"0{i} - Track {i}.mp3"), c.F.Mp3(), 400, "music");
        c.Put(Path.Combine(m, "podcast-ep12.mp3"), c.F.Mp3(), 60, "music");
    }

    private static void OneDrive(Ctx c)
    {
        var od = c.OneDriveRoot;
        c.Put(Path.Combine(od, "Shared", "Family budget.xlsx"), Formats.OfficeShell("xl/workbook.xml", c.F.Lines("<workbook/>")), 30, "document,onedrive");
        // Without folder redirection OneDrive still has its own Pictures/Documents folders.
        var odPictures = Path.Combine(od, "Pictures");
        var odDocuments = Path.Combine(od, "Documents");
        for (int i = 0; i < 4; i++)
        {
            var taken = new DateTime(2019, 7, 1 + i, 12, 0, 0);
            c.Placeholder(Path.Combine(odPictures, "Camera Roll", $"IMG_{taken:yyyyMMdd}_120000.jpg"), c.Photo(c.Seed + 110 + i, taken), 2_000 - i);
        }
        c.Placeholder(Path.Combine(odDocuments, "Archive", "old-taxes-2019.pdf"), Formats.Pdf("Taxes 2019"), 2_100);
        c.Placeholder(Path.Combine(odDocuments, "Archive", "scan-0001.pdf"), Formats.Pdf("Scan"), 1_900);
        c.Put(Path.Combine(od, "Personal Vault.lnk"), Formats.Lnk(Path.Combine(od, "Personal Vault")), 400, "shortcut,broken-shortcut,onedrive");
    }
}
