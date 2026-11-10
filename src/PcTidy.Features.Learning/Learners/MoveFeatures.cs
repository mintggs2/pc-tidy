using System.Text;
using PcTidy.Core;
using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Learners;

/// <summary>What a move can have in common with others. Order = preference when two explain the same moves.</summary>
public enum MoveFeature
{
    Extension = 0,
    NamePattern = 1,
    DownloadSite = 2,
    SourceFolder = 3,
    SizeBucket = 4,
}

/// <summary>
/// Explainable features of one move, from its name, size, folder and download site only (never file contents).
/// </summary>
public static class MoveFeatures
{
    public const long MB = 1024 * 1024;
    public const long GB = 1024 * MB;

    /// <summary>Size buckets: id, plain name, min (inclusive), max (exclusive, null = no limit).</summary>
    public static readonly IReadOnlyList<(string Id, string Name, long Min, long? Max)> SizeBuckets = new (string, string, long, long?)[]
    {
        ("under-1mb", "small files (under 1 MB)", 0, MB),
        ("1mb-100mb", "files of 1–100 MB", MB, 100 * MB),
        ("100mb-1gb", "big files (100 MB – 1 GB)", 100 * MB, GB),
        ("over-1gb", "very big files (over 1 GB)", GB, null),
    };

    public static IEnumerable<(MoveFeature Feature, string Value)> Of(MoveEvent move)
    {
        var name = Path.GetFileName(move.SourcePath);
        var ext = Extension(name);
        if (ext.Length > 0) yield return (MoveFeature.Extension, ext);
        if (NamePatternOf(name) is { } pattern) yield return (MoveFeature.NamePattern, pattern);
        if (!string.IsNullOrEmpty(move.Host)) yield return (MoveFeature.DownloadSite, move.Host!);
        if (move.SourceFolder.Length > 0) yield return (MoveFeature.SourceFolder, Normalize(move.SourceFolder));
        yield return (MoveFeature.SizeBucket, SizeBucketOf(move.Size));
    }

    public static string Extension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : "";
    }

    /// <summary>
    /// "IMG_20240101_1234.jpg" → "IMG_*_*.jpg": digit runs become "*" (dates, counters), the rest stays.
    /// Null when nothing but digits/separators would be left (too broad to mean anything) or the name has no digits
    /// (an exact name is not a pattern).
    /// </summary>
    public static string? NamePatternOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!stem.Any(char.IsDigit)) return null;
        var sb = new StringBuilder();
        foreach (var ch in stem)
        {
            if (char.IsDigit(ch)) { if (sb.Length == 0 || sb[^1] != '*') sb.Append('*'); }
            else if (ch is '*' or '?' or ';') return null; // can't be expressed as a glob
            else sb.Append(ch);
        }
        var pattern = sb.ToString();
        if (pattern.Count(char.IsLetter) < 3) return null;
        return pattern + ext;
    }

    public static string SizeBucketOf(long size)
        => SizeBuckets.First(b => size >= b.Min && (b.Max is null || size < b.Max)).Id;

    public static string Normalize(string folder)
    {
        try { return PathUtil.Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return folder; }
    }

    /// <summary>Plain words for a feature value: ".blend files", "files named IMG_*.jpg", "files from github.com".</summary>
    public static string Describe(MoveFeature feature, string value) => feature switch
    {
        MoveFeature.Extension => $".{value} files",
        MoveFeature.NamePattern => $"files named like {value}",
        MoveFeature.DownloadSite => $"files downloaded from {value}",
        MoveFeature.SourceFolder => $"files from {FolderName(value)}",
        _ => SizeBuckets.FirstOrDefault(b => b.Id == value).Name ?? "files",
    };

    public static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;
    }
}
