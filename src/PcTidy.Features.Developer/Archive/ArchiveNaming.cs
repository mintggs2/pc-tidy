using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Developer.Archive;

/// <summary>
/// Names and the small note PC Tidy puts inside each archive zip, so "Archived projects" can be listed (and restored to
/// the exact original place) from the archive folder alone. Zip: <c>&lt;archive folder&gt;\&lt;name&gt; (archived yyyy-MM-dd).zip</c>,
/// holding the project as its top-level folder plus <see cref="ManifestEntryName"/> at the zip's root.
/// </summary>
public static partial class ArchiveNaming
{
    /// <summary>The note's entry name at the root of the zip.</summary>
    public const string ManifestEntryName = "pctidy-archive.json";
    /// <summary>ArchiveToZip Args key: JSON text the engine writes into the zip as <see cref="ManifestEntryName"/>.</summary>
    public const string ManifestArg = "manifestJson";
    /// <summary>ExtractZip Args key: the zip's top-level folder whose CONTENTS go into DestinationPath (so the project lands exactly where it was).</summary>
    public const string UnwrapRootArg = "unwrapRoot";
    /// <summary>The note's format id (bump when its shape changes).</summary>
    public const string Format = "pctidy-archive/1";
    /// <summary>The longest note PC Tidy reads back (anything bigger isn't one of its notes).</summary>
    public const int MaxManifestBytes = 64 * 1024;

    [GeneratedRegex(@"^(?<name>.+) \(archived (?<date>\d{4}-\d{2}-\d{2})\)(?: \(\d+\))?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipNamePattern();

    /// <summary>"WeatherApp (archived 2026-11-10).zip" (the date is the local calendar day, invariant digits).</summary>
    public static string ZipName(string projectName, DateTime localDate)
        => $"{projectName} (archived {localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).zip";

    public static string ZipPath(string archiveFolder, string projectName, DateTime localDate) => Path.Combine(archiveFolder, ZipName(projectName, localDate));

    /// <summary>Project name and archive date from a zip name PC Tidy made (also "… (2).zip" when the name was taken), or null.</summary>
    public static (string Name, DateTime Date)? Parse(string fileName)
    {
        var m = ZipNamePattern().Match(fileName);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (m.Groups["name"].Value, d)
            : null;
    }
}

/// <summary>The note inside an archive zip: where the project came from, so restore puts it back exactly there.</summary>
public sealed record ArchiveManifest
{
    [JsonPropertyName("format")] public string Format { get; init; } = ArchiveNaming.Format;
    [JsonPropertyName("originalPath")] public required string OriginalPath { get; init; }
    /// <summary>The project's folder name, which is also the zip's top-level folder.</summary>
    [JsonPropertyName("folderName")] public required string FolderName { get; init; }
    [JsonPropertyName("archivedUtc")] public DateTime ArchivedUtc { get; init; }
    [JsonPropertyName("lastActivityUtc")] public DateTime? LastActivityUtc { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("fileCount")] public int FileCount { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a note; null when it isn't one of PC Tidy's (wrong format, broken JSON, no original path).</summary>
    public static ArchiveManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<ArchiveManifest>(json);
            return m is { OriginalPath: { Length: > 0 } original, FolderName: { Length: > 0 } } && m.Format == ArchiveNaming.Format && Path.IsPathFullyQualified(original) ? m : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return null; }
    }
}
