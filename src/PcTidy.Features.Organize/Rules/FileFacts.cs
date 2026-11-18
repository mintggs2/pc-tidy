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
    private byte[]? _head;
    private bool _headRead;
    private IReadOnlyList<string>? _zipNames;
    private SortVerdict? _sort;
    private bool _sortRead;

    /// <summary>Bytes "Sort by type" reads from the start of a file (enough for every signature it checks).</summary>
    public const int HeadBytes = 4096;
    /// <summary>A zip's file list is read only up to this size (the list sits at the end; big zips are just "archives").</summary>
    private const long MaxZipListBytes = 4L * 1024 * 1024 * 1024;

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
    /// <summary>False for online-only OneDrive files: reading any byte would download them (v3.3 review S13).</summary>
    public bool MayReadContent => !Entry.IsCloudPlaceholder && !Entry.IsDirectory;

    /// <summary>The folder being sorted is the user's Downloads: game files start ticked only there (review S5).</summary>
    public bool RootIsDownloads { get; init; }

    /// <summary>The first <see cref="HeadBytes"/> bytes, or null when the file may not or could not be read.</summary>
    public byte[]? Head
    {
        get
        {
            if (_headRead) return _head;
            _headRead = true;
            if (!MayReadContent) return null;
            try
            {
                using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, HeadBytes);
                var buf = new byte[(int)Math.Min(HeadBytes, Math.Max(0, fs.Length))];
                int read = 0, n;
                while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                _head = read == buf.Length ? buf : buf[..read];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _head = null;
            }
            return _head;
        }
    }

    /// <summary>True when this .zip lists a file with one of <paramref name="extensions"/> (read-only, nothing unpacked).</summary>
    public bool ZipHasAny(IReadOnlySet<string> extensions)
    {
        if (_zipNames is null)
        {
            _zipNames = Array.Empty<string>();
            if (MayReadContent && Extension == "zip" && Entry.Size is > 0 and <= MaxZipListBytes)
            {
                try
                {
                    // Shared like a reader that steps aside (a download or an app writing it isn't blocked), and only
                    // when the zip's own table of contents is small: listing reads all of it (code review L8).
                    using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (SmallZipDirectory(stream))
                    {
                        stream.Position = 0;
                        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                        _zipNames = zip.Entries.Take(MaxZipEntries).Select(e => e.FullName).ToList();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    // Not a readable zip: it's just "an archive".
                }
            }
        }
        return _zipNames.Any(n => extensions.Contains(OrganizeCategories.ExtensionOf(n)));
    }

    private const int MaxZipEntries = 5000;
    private const long MaxZipDirectoryBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The zip's end record says it lists at most <see cref="MaxZipEntries"/> files in a table of contents of at most
    /// 2 MB (and isn't a Zip64 file). Reads only the last 64 KB.
    /// </summary>
    private static bool SmallZipDirectory(FileStream stream)
    {
        long len = stream.Length;
        if (len < 22) return false;
        int tail = (int)Math.Min(len, 22 + 65535);
        var buf = new byte[tail];
        stream.Position = len - tail;
        int read = 0;
        while (read < tail) { int n = stream.Read(buf, read, tail - read); if (n <= 0) return false; read += n; }
        for (int i = tail - 22; i >= 0; i--)
        {
            if (buf[i] != 0x50 || buf[i + 1] != 0x4B || buf[i + 2] != 0x05 || buf[i + 3] != 0x06) continue;
            int entries = buf[i + 10] | (buf[i + 11] << 8);
            uint size = (uint)(buf[i + 12] | (buf[i + 13] << 8) | (buf[i + 14] << 16) | (buf[i + 15] << 24));
            return entries != 0xFFFF && size != 0xFFFFFFFF && entries <= MaxZipEntries && size <= MaxZipDirectoryBytes;
        }
        return false;
    }

    /// <summary>A file with the same name and <paramref name="otherExtension"/> (".cue") sits next to this one.</summary>
    public bool HasSibling(string otherExtension)
    {
        try { return File.Exists(System.IO.Path.Combine(Folder, System.IO.Path.GetFileNameWithoutExtension(Name) + otherExtension)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>What "Sort by type" makes of this file (null: it stays where it is). Worked out once.</summary>
    public SortVerdict? Sort
    {
        get
        {
            if (!_sortRead) { _sortRead = true; _sort = SortTypes.Classify(this, gameFilesTicked: RootIsDownloads); }
            return _sort;
        }
    }

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
