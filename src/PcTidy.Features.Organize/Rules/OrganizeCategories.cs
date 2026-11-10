namespace PcTidy.Features.Organize.Rules;

/// <summary>One built-in category from Plan §9. Its folder name is also the default destination.</summary>
public sealed record OrganizeCategory(string Id, string FolderName, string Icon, IReadOnlySet<string> Extensions);

/// <summary>
/// The default categories, exactly as in Plan §9, in "first match wins" order.
/// Note: "ts" is listed under both Videos (MPEG transport stream) and Code (TypeScript);
/// Videos comes first, so a loose .ts file sorts into Videos.
/// </summary>
public static class OrganizeCategories
{
    public const string Pictures = "Pictures";
    public const string Videos = "Videos";
    public const string Music = "Music";
    public const string Documents = "Documents";
    public const string Archives = "Archives";
    public const string Installers = "Installers";
    public const string Code = "Code";
    public const string Other = "Other";

    private static IReadOnlySet<string> Set(string list)
        => new HashSet<string>(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Installers by extension. A ".exe" counts only when <see cref="Detection.InstallerDetector"/> says it's an installer.</summary>
    public static readonly IReadOnlySet<string> InstallerExtensions = Set("msi msix msixbundle appx appxbundle");

    public static readonly IReadOnlyList<OrganizeCategory> All = new OrganizeCategory[]
    {
        new(Pictures, "Pictures", "Image24", Set("jpg jpeg png gif bmp webp heic heif avif tif tiff svg ico psd raw cr2 cr3 nef arw dng orf rw2")),
        new(Videos, "Videos", "Video24", Set("mp4 mkv mov avi wmv webm m4v mpg mpeg 3gp flv ts mts m2ts")),
        new(Music, "Music", "MusicNote224", Set("mp3 m4a aac flac wav ogg opus wma aiff alac mid midi")),
        new(Documents, "Documents", "Document24", Set("pdf doc docx odt rtf txt md xls xlsx xlsm ods csv ppt pptx odp epub mobi one xps oxps")),
        new(Archives, "Archives", "FolderZip24", Set("zip 7z rar tar gz tgz bz2 xz zst cab iso img")),
        new(Installers, "Installers", "ArrowDownload24", InstallerExtensions),
        new(Code, "Code", "Code24", Set("cs py js ts jsx tsx java c cpp h hpp go rs rb php sh ps1 bat cmd json yaml yml xml toml sql ipynb html css")),
    };

    public static OrganizeCategory? Find(string? id)
        => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Category folder names PC Tidy creates. Files already inside one of these are left alone.</summary>
    public static readonly IReadOnlySet<string> FolderNames =
        new HashSet<string>(All.Select(c => c.FolderName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Lower-case extension without the dot ("" when none). Handles names like ".gitignore".</summary>
    public static string ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : "";
    }
}
