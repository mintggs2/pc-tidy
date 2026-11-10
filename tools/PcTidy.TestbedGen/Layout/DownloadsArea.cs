using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>The messy Downloads folder: everything Organize and "old installers" must get right, plus every odd
/// file-system case (odd names, long paths, attributes, zero-byte, empty folders, a code project, junctions).</summary>
internal static class DownloadsArea
{
    public const string WebProject = "my-web-app";

    public static void Build(Ctx c)
    {
        var dl = c.K.Downloads;
        Installers(c, dl);
        PartialAndRecent(c, dl);
        ShortcutsAndSystemFiles(c, dl);
        Documents(c, dl);
        Media(c, dl);
        ArchivesAndCode(c, dl);
        DuplicatesAndDecoys(c, dl);
        OddCases(c, dl);
        WebProjectFolder(c, Path.Combine(dl, WebProject));
        c.Sparse(Path.Combine(dl, "Win11_24H2_English_x64.iso"), c.F.Bin("\0\0\0\0CD001", 32_768), 900L * 1024 * 1024, 400, "big-old,iso");
        c.Junction(Path.Combine(dl, "Linked Projects"), Path.Combine(c.K.UserProfile, "source", "repos"));
        // A cycle: a walker that follows junctions never finishes here.
        var oldStuff = Path.Combine(dl, "Old stuff");
        c.Put(Path.Combine(oldStuff, "old-notes.txt"), c.F.Lines("Old notes"), 700, "document,old");
        c.Put(Path.Combine(oldStuff, "photo-2019.jpg"), c.Photo(c.Seed + 300, new DateTime(2019, 8, 3, 9, 0, 0)), 1_200, "picture,old");
        c.Junction(Path.Combine(oldStuff, "loop back"), oldStuff);
    }

    private static void Installers(Ctx c, string dl)
    {
        (string Name, double Age, string Kind)[] installers =
        {
            ("ChromeSetup.exe", 130, "exe"), ("Firefox Installer.exe", 95, "exe"), ("npp.8.6.Installer.x64.exe", 210, "exe"),
            ("SteamSetup.exe", 400, "exe"), ("node-v20.11.0-x64.msi", 200, "msi"), ("Microsoft.WindowsTerminal_1.19.msixbundle", 60, "zip"),
            ("SomeTool_1.2.0.0_x64.appx", 45, "zip"), ("setup-vlc-3.0.20-win64.exe", 300, "exe"),
            ("ZoomInstallerFull.exe", 3, "exe"), ("VSCodeUserSetup-x64-1.95.0.exe", 8, "exe"), ("7z2408-x64.msi", 12, "msi"),
        };
        foreach (var (name, age, kind) in installers)
        {
            var bytes = kind switch { "msi" => c.F.Msi(), "zip" => Formats.Zip(("AppxManifest.xml", c.F.Lines("<Package/>"))), _ => c.F.Exe() };
            c.Put(Path.Combine(dl, name), bytes, age, $"installer,{(age >= 30 ? "old" : "fresh")}");
        }
        // Real installers by version info, but nothing in the name says so; fake PE has no version resource.
        c.Put(Path.Combine(dl, "python-3.12.1-amd64.exe"), c.F.Exe(), 150, "exe-ambiguous,old");
        foreach (var (name, age) in new[] { ("rufus-4.5p.exe", 150.0), ("procexp64.exe", 300), ("putty.exe", 500) })
            c.Put(Path.Combine(dl, name), c.F.Exe(), age, "portable-exe,old");
    }

    private static void PartialAndRecent(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "Win11_24H2.iso.crdownload"), c.F.Blob(40_000, 90_000), 0.01, "partial-download");
        c.Put(Path.Combine(dl, "big-dataset.zip.part"), c.F.Blob(40_000, 90_000), 3, "partial-download");
        c.Put(Path.Combine(dl, "song.mp3.opdownload"), c.F.Blob(), 1, "partial-download");
        c.Put(Path.Combine(dl, "movie.mkv.partial"), c.F.Blob(), 20, "partial-download");
        c.Put(Path.Combine(dl, "update.exe.download"), c.F.Blob(), 40, "partial-download");
        c.Put(Path.Combine(dl, "linux.iso.!qB"), c.F.Blob(), 15, "partial-download");
        c.Put(Path.Combine(dl, "~$port final.docx"), c.F.Blob(162, 163), 2, "office-lock", FileAttributes.Hidden);
        c.Put(Path.Combine(dl, ".~lock.budget.ods#"), Fake.Text("tester,tester-pc,01.11.2026 10:00\n"), 2, "office-lock");
        c.Put(Path.Combine(dl, "tmp8A3F.tmp"), c.F.Blob(), 5, "partial-download");
        // "Changed in the last 10 minutes": never moved (relative to the generation anchor).
        c.Put(Path.Combine(dl, "just-downloaded.pdf"), Formats.Pdf("Just downloaded"), 2.0 / 1440, "document,recent-10min");
    }

    private static void ShortcutsAndSystemFiles(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "FakeApp.lnk"), Formats.Lnk(Path.Combine(c.K.ProgramFiles, "FakeApp", "FakeApp.exe")), 100, "shortcut");
        c.Put(Path.Combine(dl, "Old Tool.lnk"), Formats.Lnk(Path.Combine(c.K.ProgramFiles, "GoneTool", "GoneTool.exe")), 400, "shortcut,broken-shortcut");
        c.Put(Path.Combine(dl, "Project docs.url"), Formats.Url("https://example.com/docs"), 50, "shortcut");
        c.Put(Path.Combine(dl, "desktop.ini"), Fake.Text("[.ShellClassInfo]\r\nLocalizedResourceName=@%SystemRoot%\\system32\\shell32.dll,-21798\r\n"),
            500, "desktop-ini,hidden,system", FileAttributes.Hidden | FileAttributes.System);
        c.Default(Path.Combine(dl, "desktop.ini"), "desktop.ini is never moved");
        c.Put(Path.Combine(dl, "Thumbs.db"), c.F.Blob(), 300, "thumbs-db,hidden", FileAttributes.Hidden);
    }

    private static void Documents(Ctx c, string dl)
    {
        (string Name, double Age, string Tags)[] pdfs =
        {
            ("Invoice_2025-03_ACME.pdf", 220, "document,pdf,invoice"), ("invoice-1043.pdf", 40, "document,pdf,invoice"),
            ("Receipt - Amazon order 112-4.pdf", 10, "document,pdf,invoice"), ("Bank statement March 2026.pdf", 240, "document,pdf,invoice"),
            ("user-manual.pdf", 500, "document,pdf"), ("boarding-pass.pdf", 90, "document,pdf"),
        };
        foreach (var (name, age, tags) in pdfs) c.Put(Path.Combine(dl, name), Formats.Pdf(Path.GetFileNameWithoutExtension(name)), age, tags);
        c.Put(Path.Combine(dl, "readonly-manual.pdf"), Formats.Pdf("Read-only manual"), 365, "document,pdf,read-only", FileAttributes.ReadOnly);
        c.Put(Path.Combine(dl, "CV 2026.docx"), Formats.Docx("Curriculum vitae"), 70, "document");
        c.Put(Path.Combine(dl, "budget.xlsx"), Formats.OfficeShell("xl/workbook.xml", c.F.Lines("<workbook/>")), 33, "document");
        c.Put(Path.Combine(dl, "slides.pptx"), Formats.OfficeShell("ppt/presentation.xml", c.F.Lines("<presentation/>")), 140, "document");
        c.Put(Path.Combine(dl, "data export.csv"), Fake.Text("id,name\n1,alpha\n2,beta\n"), 25, "document");
        c.Put(Path.Combine(dl, "book.epub"), Formats.Zip(("mimetype", Fake.Text("application/epub+zip"))), 260, "document");
    }

    private static void Media(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "Screenshot 2026-10-01 101530.png"), c.Png(c.Seed + 10), 40, "picture,screenshot");
        c.Put(Path.Combine(dl, "Screenshot 2026-11-08 093012.png"), c.Png(c.Seed + 11), 2, "picture,screenshot");
        c.Put(Path.Combine(dl, "Screen Shot 2025-05-02 at 10.00.00.png"), c.Png(c.Seed + 12), 560, "picture,screenshot");
        c.Put(Path.Combine(dl, "Snip_20260115.png"), c.Png(c.Seed + 13), 300, "picture,screenshot");
        c.Put(Path.Combine(dl, "Screenshot (14).png"), c.Png(c.Seed + 14), 120, "picture,screenshot");
        c.Put(Path.Combine(dl, "IMG_20250614_101500.jpg"), c.Shared.HolidayPhoto, 30, "picture,photo,exif,duplicate,dup:holiday");
        c.Put(Path.Combine(dl, "PXL_20240802_181212.jpg"), c.Photo(c.Seed + 20, new DateTime(2024, 8, 2, 18, 12, 12)), 400, "picture,photo,exif");
        c.Put(Path.Combine(dl, "DSC_0042.JPG"), c.Photo(c.Seed + 21), 600, "picture,photo,no-exif");
        c.Put(Path.Combine(dl, "beach (edited).jpg"), c.Shared.BeachReencoded, 20, "picture,near-duplicate,near:beach");
        c.Put(Path.Combine(dl, "beach.png"), c.Shared.BeachPng, 19, "picture,near-duplicate,near:beach");
        c.Put(Path.Combine(dl, "funny.gif"), c.F.Gif(), 90, "picture");
        c.Put(Path.Combine(dl, "VID_20250101_120000.mp4"), c.F.Mp4(), 300, "video");
        c.Put(Path.Combine(dl, "screen-recording.mkv"), c.F.Mkv(), 35, "video");
        c.Put(Path.Combine(dl, "Artist - Song.mp3"), c.F.Mp3(), 150, "music");
        c.Put(Path.Combine(dl, "track01.flac"), c.F.Flac(), 420, "music");
        c.Put(Path.Combine(dl, "ringtone.m4a"), c.F.M4a(), 80, "music");
    }

    private static void ArchivesAndCode(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "photos-backup.zip"), Formats.Zip(("a.jpg", c.Photo(c.Seed + 30)), ("b.jpg", c.Photo(c.Seed + 31))), 210, "archive,zip");
        c.Put(Path.Combine(dl, "project-src.zip"), Formats.Zip(("src/main.py", Fake.Text("print('hi')\n")), ("README.md", Fake.Text("# Project\n"))), 100, "archive,zip");
        c.Put(Path.Combine(dl, "chatgpt-export-2026-05-01.zip"), Formats.Zip(
            ("conversations.json", Fake.Text("[{\"title\":\"Trip ideas\",\"create_time\":1714550400,\"mapping\":{}}]")),
            ("chat.html", Fake.Text("<html><body>Chat export</body></html>")),
            ("user.json", Fake.Text("{\"email\":\"tester@example.invalid\"}"))), 190, "archive,zip,ai-export,chatgpt-export");
        c.Put(Path.Combine(dl, "claude-export-2026-06-01.zip"), Formats.Zip(
            ("conversations.json", Fake.Text("[{\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"Recipe help\",\"chat_messages\":[]}]")),
            ("projects.json", Fake.Text("[]")),
            ("users.json", Fake.Text("[{\"full_name\":\"Tester\",\"email_address\":\"tester@example.invalid\"}]"))), 160, "archive,zip,ai-export,claude-export");
        c.Put(Path.Combine(dl, "old-drivers.7z"), c.F.SevenZip(), 380, "archive");
        c.Put(Path.Combine(dl, "game-mod.rar"), c.F.Rar(), 230, "archive");
        c.Put(Path.Combine(dl, "ubuntu-24.04-desktop-amd64.iso.torrent"), Formats.Torrent("ubuntu-24.04-desktop-amd64.iso", 6_000_000_000, c.F.Bytes(20)), 180, "torrent");
        (string Name, string Text)[] code =
        {
            ("script.py", "import sys\nprint(sys.argv)\n"), ("index.html", "<!doctype html><title>t</title>"), ("data.json", "{\"a\":1}"),
            ("query.sql", "SELECT 1;"), ("notes.md", "# Notes\n- one\n"), ("build.ps1", "Write-Output 'build'"),
        };
        for (int i = 0; i < code.Length; i++) c.Put(Path.Combine(dl, code[i].Name), Fake.Text(code[i].Text), 10 + i * 20, "code");
    }

    private static void DuplicatesAndDecoys(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "Report final.docx"), c.Shared.ReportDocx, 50, "document,duplicate,dup:report");
        c.Put(Path.Combine(dl, "dataset-a.bin"), c.Shared.DecoyA, 70, "decoy,same-size-same-prefix");
        c.Put(Path.Combine(dl, "dataset-b.bin"), c.Shared.DecoyB, 70, "decoy,same-size-same-prefix");
        c.Put(Path.Combine(dl, "same-size-1.dat"), c.Shared.SameSize1, 60, "decoy,same-size");
        c.Put(Path.Combine(dl, "same-size-2.dat"), c.Shared.SameSize2, 60, "decoy,same-size");
    }

    private static void OddCases(Ctx c, string dl)
    {
        c.Put(Path.Combine(dl, "Résumé – José Müller 履歴書.docx"), Formats.Docx("Résumé"), 45, "document,odd-name,unicode");
        c.Put(Path.Combine(dl, "фото_отпуск.jpg"), c.Photo(c.Seed + 40, new DateTime(2025, 7, 1, 12, 0, 0)), 130, "picture,odd-name,unicode");
        c.Put(Path.Combine(dl, "🎉 party plan.txt"), c.F.Lines("Party plan"), 25, "document,odd-name,unicode,emoji");
        c.Put(Path.Combine(dl, "notes."), c.F.Lines("Trailing dot"), 80, "odd-name,trailing-dot");
        c.Put(Path.Combine(dl, "draft "), c.F.Lines("Trailing space"), 80, "odd-name,trailing-space");
        c.Put(Path.Combine(dl, " leading space.txt"), c.F.Lines("Leading space"), 80, "document,odd-name,leading-space");
        c.Put(Path.Combine(dl, "CON.txt"), c.F.Lines("Reserved device name"), 80, "document,odd-name,reserved-name");
        c.Put(Path.Combine(dl, "UPPER.TXT"), c.F.Lines("Upper-case extension"), 80, "document,odd-name");
        c.Put(Path.Combine(dl, "New Text Document.txt"), Array.Empty<byte>(), 33, "document,zero-byte");
        c.Put(Path.Combine(dl, "empty.log"), Array.Empty<byte>(), 12, "zero-byte");
        c.Put(Path.Combine(dl, ".secret-notes.txt"), c.F.Lines("Hidden notes"), 50, "document,hidden", FileAttributes.Hidden);
        c.EmptyDir(Path.Combine(dl, "New folder"), 100);
        c.EmptyDir(Path.Combine(dl, "New folder (2)"), 10);
        c.EmptyDir(Path.Combine(dl, "Nested empty", "inner", "innermost"), 60);

        // Long paths: a file > 260 characters deep in folders, and a loose file whose own name is very long.
        var deep = Path.Combine(dl, "Deep folder");
        for (int i = 1; i <= 5; i++) deep = Path.Combine(deep, $"Level {i:D2} - a rather long folder name that pushes the path length");
        c.Put(Path.Combine(deep, "long-path-report.pdf"), Formats.Pdf("Long path"), 150, "document,pdf,long-path");
        var longName = "Meeting notes from the quarterly planning session with every department and a very long list of action items " +
                       "that somebody pasted into the file name by accident and never shortened.txt";
        c.Put(Path.Combine(dl, longName), c.F.Lines("Long name"), 75, "document,long-path,long-name");
    }

    private static void WebProjectFolder(Ctx c, string p)
    {
        Git(c, Path.Combine(p, ".git"), 3);
        c.Put(Path.Combine(p, "package.json"), Fake.Text("{\n  \"name\": \"my-web-app\",\n  \"version\": \"0.1.0\",\n  \"dependencies\": { \"lodash\": \"^4.17.21\" }\n}\n"), 3, "code-project");
        c.Put(Path.Combine(p, "package-lock.json"), Fake.Text("{\"lockfileVersion\":3}"), 3, "code-project");
        c.Put(Path.Combine(p, "README.md"), Fake.Text("# my-web-app\n"), 30, "code-project");
        c.Put(Path.Combine(p, "src", "index.js"), Fake.Text("console.log('hello');\n"), 3, "code-project");
        c.Put(Path.Combine(p, "src", "App.jsx"), Fake.Text("export default function App(){ return null; }\n"), 3, "code-project");
        c.Put(Path.Combine(p, "src", "logo.png"), c.Png(c.Seed + 50, 32, 32), 30, "code-project");     // loose-looking picture inside a project
        c.Put(Path.Combine(p, "node_modules", "lodash", "package.json"), Fake.Text("{\"name\":\"lodash\",\"version\":\"4.17.21\"}"), 60, "code-project,node_modules");
        c.Put(Path.Combine(p, "node_modules", "lodash", "lodash.js"), c.F.Lines("/* lodash */", 40), 60, "code-project,node_modules");
        c.Put(Path.Combine(p, "node_modules", ".package-lock.json"), Fake.Text("{}"), 60, "code-project,node_modules");
        c.Tag(p, "code-project");
        c.NeverTree(Path.Combine(p, ".git"), "Git repository");
        c.NeverTree(Path.Combine(p, "src"), "Source code inside a project");
    }

    /// <summary>Minimal-looking .git folder (hidden, like git makes it).</summary>
    public static void Git(Ctx c, string git, double ageDays)
    {
        c.Put(Path.Combine(git, "HEAD"), Fake.Text("ref: refs/heads/main\n"), ageDays, "git");
        c.Put(Path.Combine(git, "config"), Fake.Text("[core]\n\trepositoryformatversion = 0\n\tbare = false\n"), ageDays + 30, "git");
        c.Put(Path.Combine(git, "index"), c.F.Bin("DIRC\0\0\0\x02", 2_000), ageDays, "git");
        c.Put(Path.Combine(git, "refs", "heads", "main"), Fake.Text(c.F.Hex(40) + "\n"), ageDays, "git");
        for (int i = 0; i < 3; i++)
        {
            var hash = c.F.Hex(40);
            c.Put(Path.Combine(git, "objects", hash[..2], hash[2..]), c.F.Bin("x\x01", c.F.Next(100, 3_000)), ageDays + i * 5, "git");
        }
        c.W.SetAttributes(git, FileAttributes.Hidden);
    }
}
