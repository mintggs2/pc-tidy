using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Photos.Photos;

/// <summary>Which files the photo organizer sorts, reads dates from and checks for blur.</summary>
public static class PhotoFormats
{
    private static IReadOnlySet<string> Set(string list)
        => new HashSet<string>(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Photos from cameras and phones. Drawings, icons, design files and animations (svg ico psd gif) aren't photos and
    /// stay put.
    /// </summary>
    public static readonly IReadOnlySet<string> Photos = Set("jpg jpeg jpe jfif png heic heif avif webp tif tiff bmp dng raw cr2 cr3 nef arw orf rw2");

    /// <summary>Videos from cameras and phones (".ts" is left out: it's as often TypeScript code as video).</summary>
    public static readonly IReadOnlySet<string> Videos = Set("mp4 mov m4v avi wmv mkv webm 3gp mts m2ts mpg mpeg");

    /// <summary>Formats with an EXIF block PC Tidy's own reader understands (JPEG and TIFF-based raw files).</summary>
    public static readonly IReadOnlySet<string> WithExif = Set("jpg jpeg jpe jfif tif tiff dng nef cr2 arw orf rw2");

    /// <summary>
    /// Formats Windows can decode for the blur and burst checks. HEIC, AVIF and WEBP need Windows' optional image
    /// extensions; when they're missing the photo is still sorted, just not checked.
    /// </summary>
    public static readonly IReadOnlySet<string> Decodable = Set("jpg jpeg jpe jfif png heic heif avif webp tif tiff bmp");

    public static bool IsPhoto(string fileName) => Photos.Contains(OrganizeCategories.ExtensionOf(fileName));
    public static bool IsVideo(string fileName) => Videos.Contains(OrganizeCategories.ExtensionOf(fileName));
    public static bool IsMedia(string fileName) => IsPhoto(fileName) || IsVideo(fileName);
    public static bool HasExif(string fileName) => WithExif.Contains(OrganizeCategories.ExtensionOf(fileName));
    public static bool CanDecode(string fileName) => Decodable.Contains(OrganizeCategories.ExtensionOf(fileName));
}
