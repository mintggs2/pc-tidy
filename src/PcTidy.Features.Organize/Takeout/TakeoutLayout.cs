using System.Text.RegularExpressions;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Takeout;

/// <summary>What kind of folder a Google Photos export folder is.</summary>
public enum TakeoutFolderKind
{
    /// <summary>"Photos from 2019" (and its translations): Google's own by-year folders.</summary>
    Year,
    /// <summary>An album the user made (or a shared album).</summary>
    Album,
    /// <summary>Google Photos' bin: photos the user deleted. Left where they are.</summary>
    Bin,
}

/// <summary>
/// How a Google Takeout export is laid out (all names compared culture-invariant, ignoring case):
/// <c>Takeout\Google Photos\&lt;album or "Photos from YYYY"&gt;\photo.jpg</c> with a sidecar next to each photo.
/// Sidecar names vary between export versions: <c>photo.jpg.json</c>, <c>photo.jpg.supplemental-metadata.json</c>,
/// that name cut short to fit 51 characters (<c>photo.jpg.supplemental-me.json</c>), numbered copies
/// (<c>photo(1).jpg</c> ↔ <c>photo.jpg(1).json</c>), long photo names cut short in the sidecar name, and
/// edited copies (<c>photo-edited.jpg</c>) that share the original's sidecar.
/// </summary>
public static partial class TakeoutLayout
{
    private const string Supplemental = "supplemental-metadata";

    /// <summary>Folder names of the Google Photos part of a Takeout export (English and common translations).</summary>
    public static readonly IReadOnlySet<string> PhotosFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Google Photos", "Google Fotos", "Google Foto", "Google Zdjęcia", "Google Fotoğraflar",
    };

    /// <summary>Takeout's own info files that aren't photo sidecars (they stay in the Takeout folder).</summary>
    public static readonly IReadOnlySet<string> InfoFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.json", "print-subscriptions.json", "shared_album_comments.json", "user-generated-memory-titles.json",
        "archive_browser.html",
    };

    private static readonly IReadOnlySet<string> BinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Trash", "Bin", "Papierkorb", "Corbeille", "Papelera", "Cestino", "Prullenbak", "Lixeira", "Kosz", "Çöp Kutusu",
    };

    /// <summary>"-edited" and its translations, as Google appends them to the edited copy's name.</summary>
    private static readonly string[] EditedSuffixes =
    {
        "-edited", "-bearbeitet", "-modifié", "-editado", "-modificato", "-bewerkt", "-redigerad", "-edytowane", "-düzenlendi", "-muokattu",
    };

    [GeneratedRegex(@"^(Photos from|Fotos von|Photos de|Fotos de|Foto dal|Foto del|Foto's uit|Fotos från|Zdjęcia z|Fotky z)\s+(19|20)\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearFolderRegex();

    /// <summary>"photo(1).jpg": the "(1)" Google adds to a second photo with the same name.</summary>
    [GeneratedRegex(@"^(?<stem>.+?)\((?<n>\d{1,3})\)(?<ext>\.[^.]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedMediaRegex();

    /// <summary>A sidecar name without ".json", with Google's "(1)" taken off the end.</summary>
    [GeneratedRegex(@"^(?<core>.+?)\((?<n>\d{1,3})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedSidecarRegex();

    public static TakeoutFolderKind Classify(string folderName)
        => BinNames.Contains(folderName) ? TakeoutFolderKind.Bin
         : YearFolderRegex().IsMatch(folderName.Trim()) ? TakeoutFolderKind.Year
         : TakeoutFolderKind.Album;

    /// <summary>Photos and videos (the §9 Pictures and Videos tables).</summary>
    public static bool IsMedia(string fileName)
    {
        var ext = OrganizeCategories.ExtensionOf(fileName);
        return OrganizeCategories.Find(OrganizeCategories.Pictures)!.Extensions.Contains(ext)
               || OrganizeCategories.Find(OrganizeCategories.Videos)!.Extensions.Contains(ext);
    }

    public static bool IsSidecarCandidate(string fileName)
        => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !InfoFileNames.Contains(fileName);

    /// <summary>"IMG_1-edited.jpg" → "IMG_1.jpg"; null when the name isn't an edited copy.</summary>
    public static string? OriginalOfEdited(string mediaName)
    {
        var ext = Path.GetExtension(mediaName);
        var stem = Path.GetFileNameWithoutExtension(mediaName);
        foreach (var suffix in EditedSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return stem[..^suffix.Length] + ext;
        return null;
    }

    /// <summary>
    /// The key a sidecar file name stands for: the photo name it describes (lower-case, invariant) plus Google's
    /// copy number. "IMG.jpg.supplemental-metadata(1).json" → ("img.jpg", 1).
    /// </summary>
    public static (string Name, int Number) SidecarKey(string sidecarFileName)
    {
        var core = sidecarFileName[..^".json".Length];
        int number = 0;
        if (NumberedSidecarRegex().Match(core) is { Success: true } m)
        {
            core = m.Groups["core"].Value;
            number = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        // ".supplemental-metadata" (or the start of it, when Google cut the name short) after the photo name.
        int dot = core.LastIndexOf('.');
        if (dot > 0)
        {
            var tail = core[(dot + 1)..];
            // What's left must still be a file name with an extension ("IMG.jpg"), not just "IMG".
            if (tail.Length > 0 && Supplemental.StartsWith(tail, StringComparison.OrdinalIgnoreCase) && core.LastIndexOf('.', dot - 1) > 0)
                core = core[..dot];
        }
        return (core.ToLowerInvariant(), number);
    }

    /// <summary>The keys to look a photo's sidecar up by, best first: its own name, then (for "photo(1).jpg") the numbered key.</summary>
    public static IEnumerable<(string Name, int Number)> MediaKeys(string mediaName)
    {
        if (NumberedMediaRegex().Match(mediaName) is { Success: true } m)
        {
            var baseName = (m.Groups["stem"].Value + m.Groups["ext"].Value).ToLowerInvariant();
            yield return (baseName, int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        yield return (mediaName.ToLowerInvariant(), 0);
    }

    /// <summary>Google cuts long names short in the sidecar's name: a sidecar key that is the start of the photo's name.</summary>
    public static bool IsTruncatedKeyOf(string key, string mediaName)
        => key.Length >= 30 && key.Length < mediaName.Length && mediaName.StartsWith(key, StringComparison.OrdinalIgnoreCase);
}
