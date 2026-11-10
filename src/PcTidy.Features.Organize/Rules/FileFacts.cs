using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Detection;

namespace PcTidy.Features.Organize.Rules;

/// <summary>
/// What rules may ask about one file. Expensive facts (installer check, download site, EXIF date)
/// are read lazily and only once, and never for online-only cloud files.
/// </summary>
public sealed class FileFacts
{
    private InstallerVerdict? _installer;
    private ZoneInfo? _zone;
    private bool _zoneRead;
    private DateTime? _taken;
    private bool _takenRead;

    public FileFacts(FileEntry entry, string root, ScanDepth depth)
    {
        Entry = entry;
        Root = root;
        Depth = depth;
        Extension = OrganizeCategories.ExtensionOf(entry.Name);
    }

    public FileEntry Entry { get; }
    public string Root { get; }
    public ScanDepth Depth { get; }
    public string Name => Entry.Name;
    public string Path => Entry.FullPath;
    public string Extension { get; }
    public string Folder => System.IO.Path.GetDirectoryName(Entry.FullPath) ?? Root;
    private bool MayReadContent => !Entry.IsCloudPlaceholder && !Entry.IsDirectory;

    /// <summary>Problems met while evaluating (e.g. a pattern that timed out), shown as scan notes.</summary>
    public List<string> Warnings { get; } = new();

    public InstallerVerdict Installer
        => _installer ??= Extension == "exe" ? InstallerDetector.Inspect(Path, MayReadContent) : new InstallerVerdict(false, "Not an .exe file.");

    public ZoneInfo? Zone
    {
        get
        {
            if (!_zoneRead) { _zone = MayReadContent ? ZoneIdentifier.Read(Path) : null; _zoneRead = true; }
            return _zone;
        }
    }

    /// <summary>EXIF date taken. Only read in Deep mode, and only for picture files.</summary>
    public DateTime? DateTaken
    {
        get
        {
            if (!_takenRead)
            {
                _takenRead = true;
                var isPicture = OrganizeCategories.Find(OrganizeCategories.Pictures)!.Extensions.Contains(Extension);
                if (Depth == ScanDepth.Deep && isPicture && MayReadContent) _taken = ExifReader.ReadDateTaken(Path);
            }
            return _taken;
        }
    }

    /// <summary>Local date used for {year}/{month} folders: date taken when known, else the modified date.</summary>
    public DateTime FolderDate => DateTaken ?? Entry.LastWriteUtc.ToLocalTime();

    /// <summary>Built-in category (exact §9 table, installer .exe detection included), or null for "Other".</summary>
    public string? CategoryId
    {
        get
        {
            foreach (var cat in OrganizeCategories.All)
            {
                if (cat.Extensions.Contains(Extension)) return cat.Id;
                if (cat.Id == OrganizeCategories.Installers && Extension == "exe" && Installer.IsInstaller) return cat.Id;
            }
            return null;
        }
    }
}
