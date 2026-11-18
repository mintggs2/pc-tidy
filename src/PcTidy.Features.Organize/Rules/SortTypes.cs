using System.Text.RegularExpressions;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Organize.Rules;

/// <summary>One "Sort by type" folder (3.3). <see cref="Folder"/> is relative to the folder being sorted.</summary>
public sealed record SortType(string Id, string Title, string Folder, string Icon);

/// <summary>What a file is, for "Sort by type". <see cref="CheckFirst"/> rows are shown unticked with the reason.</summary>
public sealed record SortVerdict(SortType Type, bool CheckFirst, string Why);

/// <summary>
/// The "Sort by type" types and the classifier behind them (3.3, option A). It looks at the name and, when the file may
/// be read (never an online-only OneDrive file), at its first bytes and a zip's file list, not only at the extension.
/// Read-only. Anything it can't name for sure gets null and stays where it is. Scripts you run, app settings and
/// libraries are never typed (they stay next to whatever uses them). See docs/reviews/v3.3-organize-design-review.md.
/// </summary>
public static class SortTypes
{
    public static readonly SortType Installers = new("installers", "Program installers", "Program installers", "ArrowDownload24");
    public static readonly SortType CallOfDuty = new("cod", "Games › Call of Duty", @"Games\Call of Duty", "Games24");
    public static readonly SortType Cronus = new("cronus", "Games › Cronus scripts", @"Games\Cronus scripts", "XboxController24");
    public static readonly SortType Screenshots = new("screenshots", "Pictures › Screenshots", @"Pictures\Screenshots", "Screenshot24");
    public static readonly SortType Pictures = new("pictures", "Pictures", "Pictures", "Image24");
    public static readonly SortType Clips = new("clips", "Videos › Clips and recordings", @"Videos\Clips and recordings", "VideoClip24");
    public static readonly SortType Videos = new("videos", "Videos", "Videos", "Video24");
    public static readonly SortType Music = new("music", "Music", "Music", "MusicNote224");
    public static readonly SortType Invoices = new("invoices", "Documents › Invoices", @"Documents\Invoices", "DocumentPdf24");
    public static readonly SortType Documents = new("documents", "Documents", "Documents", "Document24");
    public static readonly SortType Archives = new("archives", "Archives", "Archives", "FolderZip24");
    public static readonly SortType DiskImages = new("diskimages", "Disk images", "Disk images", "HardDrive24");
    public static readonly SortType Torrents = new("torrents", "Torrents", "Torrents", "ArrowDownload24");
    public static readonly SortType Fonts = new("fonts", "Fonts", "Fonts", "TextFont24");
    public static readonly SortType Code = new("code", "Code", "Code", "Code24");

    /// <summary>In the order the preset lists them.</summary>
    public static readonly IReadOnlyList<SortType> All = new[]
    {
        Installers, CallOfDuty, Cronus, Screenshots, Pictures, Clips, Videos, Music, Invoices, Documents, Archives, DiskImages, Torrents, Fonts, Code,
    };

    public static SortType? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Top-level folder names these types create ("Games", "Pictures"…): a file already in one is left alone.</summary>
    public static readonly IReadOnlySet<string> TopFolderNames =
        All.Select(t => t.Folder.Split('\\')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Set(string list) => new HashSet<string>(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> PictureExt = Set("jpg jpeg png gif bmp webp heic heif avif tif tiff psd raw cr2 cr3 nef arw dng orf rw2 svg");
    private static readonly IReadOnlySet<string> VideoExt = Set("mp4 mkv mov avi wmv webm m4v mpg mpeg 3gp flv mts m2ts ts");
    private static readonly IReadOnlySet<string> MusicExt = Set("mp3 m4a aac flac wav ogg opus wma aiff alac mid midi");
    private static readonly IReadOnlySet<string> DocumentExt = Set("pdf doc docx odt rtf txt md xls xlsx xlsm ods csv ppt pptx odp epub mobi one xps oxps");
    private static readonly IReadOnlySet<string> ArchiveExt = Set("zip 7z rar tar gz tgz bz2 xz zst cab");
    private static readonly IReadOnlySet<string> FontExt = Set("ttf otf ttc woff woff2");
    private static readonly IReadOnlySet<string> CodeExt = Set("cs java c cpp h hpp go rs ipynb sql css jsx tsx kt swift");
    /// <summary>BO1/BO2/WaW-era Call of Duty mod and script formats (Plutonium and others).</summary>
    private static readonly IReadOnlySet<string> CodExt = Set("gsc gscbin csc ff ipak iwd");
    private static readonly IReadOnlySet<string> CronusExt = Set("gpc gpj");
    private static readonly IReadOnlySet<string> ModernInstallerExt = Set("msix msixbundle appx appxbundle");

    /// <summary>
    /// Never typed by "Sort by type" (v3.3 review S3/S4, code review M4): scripts you run (also Python, shell, Lua, Ruby,
    /// PHP and Perl ones), app settings, libraries and data files stay next to the program or shortcut that uses them.
    /// Saved web pages stay with their "_files" folder, icons with the shortcuts that show them, and .cue/.bin disc
    /// images stay whole (a .cue can list several .bin tracks with other names).
    /// </summary>
    private static readonly IReadOnlySet<string> StayPut = Set("bat cmd ps1 psm1 vbs vbe js jse wsf ahk reg ini cfg json xml yaml yml toml config conf log db sqlite dll sys dat lock py pyw sh lua rb php pl html htm mht ico cue bin");

    /// <summary>Whole words in a zip's or config's name that point to Call of Duty (never "zombies" or "mp" alone: review D12).</summary>
    private static readonly IReadOnlySet<string> CodWords = Set("plutonium bo1 bo2 bo3 bo4 t4 t5 t6 iw4x iw4m iw5 iw6 mw2 mw3 waw blackops callofduty");

    // Screenshot and clip names, checked per tool (review D7). Each also needs a real image / video file.
    private static readonly Regex[] ScreenshotNames =
    {
        new(@"^Screenshot[ _(-]", RegexOptions.IgnoreCase),                      // Windows: "Screenshot (12)", "Screenshot 2026-11-02 191544"
        new(@"^Screen ?Shot[ _]\d{4}", RegexOptions.IgnoreCase),                  // "Screen Shot 2026-..."
        new(@"^\d{14}_\d+\.(png|jpe?g)$", RegexOptions.IgnoreCase),              // Steam: 20261102191544_1.jpg
        new(@" \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.(png|jpe?g)$", RegexOptions.IgnoreCase), // Xbox Game Bar: "<game> 2026-11-02 19-15-44.png"
    };
    private static readonly Regex[] ClipNames =
    {
        new(@"^\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.(mkv|mp4|flv|mov)$", RegexOptions.IgnoreCase),        // OBS default
        new(@" \d{4}\.\d{2}\.\d{2} - \d{2}\.\d{2}\.\d{2}(\.\d{2})?(\.DVR)?\.mp4$", RegexOptions.IgnoreCase),  // NVIDIA ShadowPlay / Instant Replay
        new(@" \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.mp4$", RegexOptions.IgnoreCase),                        // Xbox Game Bar
        new(@"^Replay \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.(mkv|mp4)$", RegexOptions.IgnoreCase),           // OBS replay buffer
    };
    private static readonly Regex InvoiceWords = new(@"\b(invoice|receipt|statement|bill)s?\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// A program dressed up as a document or picture ("invoice.pdf.exe", or a ".jpg" that is really a program): never
    /// sorted, shown with a warning. Harmless mismatches (a WebP saved as .jpg) are not flagged (review S11).
    /// </summary>
    public static string? DisguiseProblem(FileFacts f)
    {
        if (Regex.IsMatch(f.Name, @"\.(pdf|docx?|xlsx?|pptx?|txt|jpe?g|png|gif|mp4|mp3|mov|zip)\.(exe|scr|com|pif|bat|cmd|js|vbs|msi)$", RegexOptions.IgnoreCase))
            return "This is a program with a document's or picture's name. PC Tidy won't sort it. Don't open it unless you trust where it came from.";
        bool looksHarmless = PictureExt.Contains(f.Extension) || VideoExt.Contains(f.Extension) || MusicExt.Contains(f.Extension) || DocumentExt.Contains(f.Extension);
        if (looksHarmless && f.Head is { Length: >= 2 } h && FileSignature.IsProgram(h))
            return $"This is a program, but its name ends in .{f.Extension}. PC Tidy won't sort it. Don't open it unless you trust where it came from.";
        return null;
    }

    /// <param name="gameFilesTicked">Game files start ticked only in Downloads (review S5); elsewhere they're Check first.</param>
    public static SortVerdict? Classify(FileFacts f, bool gameFilesTicked)
    {
        var ext = f.Extension;
        var head = f.Head;

        // ---- games (before archives and configs: a mod zip is a game file)
        if (CodExt.Contains(ext))
            return new(CallOfDuty, !gameFilesTicked, $"A Call of Duty mod or script file (.{ext}).");
        if (CronusExt.Contains(ext))
            return new(Cronus, !gameFilesTicked, "A Cronus Zen script.");
        if (ext == "cfg" && HasCodWord(f.Name))
            return new(CallOfDuty, true, "A config file whose name points to Call of Duty. Check it isn't one a game loads from here.");
        if (ext == "zip" && (HasCodWord(f.Name) || f.ZipHasAny(CodExt)))
            return new(CallOfDuty, !gameFilesTicked, HasCodWord(f.Name) ? "A zip named for Call of Duty." : "A zip with Call of Duty mod files inside.");

        if (StayPut.Contains(ext)) return null;

        // ---- installers (installed programs are never in these folders; portable apps stay put)
        if (ext == "msi")
            return new(Installers, true, "An installer. Windows may ask for this exact file when you repair or remove the app, so check first.");
        if (ModernInstallerExt.Contains(ext)) return new(Installers, false, "An app installer package.");
        if (ext == "exe") return f.Installer.IsInstaller ? new(Installers, false, f.Installer.Why) : null;

        // ---- pictures, screenshots
        if (PictureExt.Contains(ext))
        {
            if (head is not null && ext is not ("svg" or "psd" or "raw") && !FileSignature.IsImage(head) && FileSignature.Known(head) is not null) return null;
            return ScreenshotNames.Any(r => r.IsMatch(f.Name)) ? new(Screenshots, false, "A screenshot (its name).") : new(Pictures, false, "A picture.");
        }

        // ---- videos, clips (.ts is TypeScript unless the file is really a video stream: review D1)
        if (ext == "ts")
            return head is not null && FileSignature.IsMpegTs(head) ? new(Videos, false, "A video (MPEG transport stream).")
                 : head is not null ? new(Code, false, "A TypeScript code file.") : null;
        if (VideoExt.Contains(ext))
            return ClipNames.Any(r => r.IsMatch(f.Name)) ? new(Clips, false, "A game clip or screen recording (its name).") : new(Videos, false, "A video.");

        if (MusicExt.Contains(ext)) return new(Music, false, "Music or audio.");

        // ---- documents (a .pdf must really be a PDF)
        if (DocumentExt.Contains(ext))
        {
            if (ext == "pdf" && head is { Length: >= 4 } && !FileSignature.IsPdf(head)) return null;
            return ext == "pdf" && InvoiceWords.IsMatch(f.Name) ? new(Invoices, false, "A bill or receipt (its name).") : new(Documents, false, "A document.");
        }

        if (ArchiveExt.Contains(ext)) return new(Archives, false, "A compressed archive.");
        if (ext == "img" && f.HasSibling(".ccd")) return null; // a CloneCD image stays whole with its .ccd and .sub
        if (ext == "iso" || ext == "img") return new(DiskImages, false, "A disk image.");
        if (ext is "vhd" or "vhdx") return new(DiskImages, true, "A virtual disk. A virtual machine may use it from here, so check first.");
        if (ext == "torrent") return new(Torrents, false, "A torrent file.");
        if (FontExt.Contains(ext)) return new(Fonts, true, "A font file. If Windows installed it as a link, it needs the file here, so check first.");
        if (CodeExt.Contains(ext)) return new(Code, false, "A code file.");
        return null;
    }

    private static bool HasCodWord(string name)
    {
        var words = InstallerDetector.Words(Path.GetFileNameWithoutExtension(name)).ToList();
        if (words.Any(CodWords.Contains)) return true;
        // "Black Ops", "Call of Duty" split into separate words
        for (int i = 0; i + 1 < words.Count; i++)
            if (words[i] == "black" && words[i + 1] == "ops") return true;
        for (int i = 0; i + 2 < words.Count; i++)
            if (words[i] == "call" && words[i + 1] == "of" && words[i + 2] == "duty") return true;
        return false;
    }
}
