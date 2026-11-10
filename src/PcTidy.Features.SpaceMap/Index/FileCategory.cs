namespace PcTidy.Features.SpaceMap.Index;

/// <summary>Colour group of a file in the treemap (WinDirStat-style "colour by file type").</summary>
public enum FileCategory : byte
{
    Other = 0,
    Videos,
    Images,
    Music,
    Documents,
    Archives,
    Apps,
    Games,
    System,
}

/// <summary>Extension → category map. Extensions are lower-case, without the dot.</summary>
public static class FileCategories
{
    public const int Count = 9;

    private static readonly Dictionary<string, FileCategory> Map = Build();

    public static FileCategory FromExtension(string extension)
        => Map.TryGetValue(extension, out var c) ? c : FileCategory.Other;

    public static string DisplayName(FileCategory c) => c switch
    {
        FileCategory.Videos => "Videos",
        FileCategory.Images => "Pictures",
        FileCategory.Music => "Music",
        FileCategory.Documents => "Documents",
        FileCategory.Archives => "Archives & installers",
        FileCategory.Apps => "Apps",
        FileCategory.Games => "Games",
        FileCategory.System => "System",
        _ => "Other",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        void Add(FileCategory c, string list) { foreach (var e in list.Split(' ')) d[e] = c; }
        Add(FileCategory.Videos, "mp4 mkv mov avi wmv m4v webm flv mpg mpeg m2ts mts ts 3gp vob ogv");
        Add(FileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico avif jxl");
        Add(FileCategory.Music, "mp3 flac wav m4a aac ogg opus wma aiff alac mid midi");
        Add(FileCategory.Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub json xml html htm one pst ost");
        Add(FileCategory.Archives, "zip 7z rar tar gz tgz bz2 xz zst iso img vhd vhdx vmdk cab msi msix msixbundle appx appxbundle");
        Add(FileCategory.Apps, "exe dll so node pak asar jar pyd lib a");
        Add(FileCategory.Games, "vpk pck uasset ubulk utoc ucas bsa ba2 forge wad");
        Add(FileCategory.System, "sys etl evtx log dmp mui cat inf blf regtrans-ms edb chk wim esd");
        return d;
    }
}
