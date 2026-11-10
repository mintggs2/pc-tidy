using System.IO.Compression;
using System.Text;
using PcTidy.Core;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Archive;

/// <summary>One archive zip found in the archive folder.</summary>
/// <param name="Manifest">PC Tidy's note from inside the zip, or null (no note, unreadable, or an online-only file that isn't opened).</param>
/// <param name="Problem">Why it can't be restored with one click, or null.</param>
internal sealed record ArchivedProject(string ZipPath, string Name, DateTime ArchivedDate, long ZipBytes, DateTime ZipWriteUtc,
    ArchiveManifest? Manifest, string? Problem);

/// <summary>
/// Lists the archive zips PC Tidy made (by their name pattern) in the archive folder and reads each one's note
/// (<see cref="ArchiveNaming.ManifestEntryName"/>). Read-only: zips are opened for reading only, and online-only
/// (OneDrive) zips are never opened, so nothing is downloaded.
/// </summary>
internal static class ArchivedProjects
{
    public static IReadOnlyList<ArchivedProject> List(DevContext dev, string archiveFolder, CancellationToken ct)
    {
        if (!FolderMeasure.DirectoryExists(archiveFolder)) return Array.Empty<ArchivedProject>();
        var list = new List<ArchivedProject>();
        foreach (var e in FolderMeasure.Children(dev.Walker, archiveFolder, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || e.IsReparsePoint || ArchiveNaming.Parse(e.Name) is not { } parsed) continue;
            if (e.IsCloudPlaceholder)
            {
                list.Add(new ArchivedProject(e.FullPath, parsed.Name, parsed.Date, e.Size, e.LastWriteUtc, null,
                    "This zip is online-only in OneDrive. Make it available on this PC first; PC Tidy never downloads it for you."));
                continue;
            }
            var (manifest, problem) = ReadManifest(e.FullPath);
            list.Add(new ArchivedProject(e.FullPath, parsed.Name, parsed.Date, e.Size, e.LastWriteUtc, manifest, problem));
        }
        return list.OrderByDescending(a => a.ArchivedDate).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The note inside the zip, or why there isn't a usable one.</summary>
    internal static (ArchiveManifest? Manifest, string? Problem) ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(ArchiveNaming.ManifestEntryName);
            if (entry is null || entry.Length is <= 0 or > ArchiveNaming.MaxManifestBytes)
                return (null, "This zip has no PC Tidy note saying where the project came from. Open it in Explorer to unzip it yourself.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var manifest = ArchiveManifest.FromJson(reader.ReadToEnd());
            if (manifest is null) return (null, "PC Tidy's note in this zip couldn't be read. Open it in Explorer to unzip it yourself.");
            // The note must match the zip's own top-level folder, or restore could put files somewhere unexpected.
            if (!zip.Entries.Any(x => x.FullName.StartsWith(manifest.FolderName + "/", StringComparison.Ordinal)))
                return (null, "PC Tidy's note doesn't match what's in this zip. Open it in Explorer to unzip it yourself.");
            return (manifest, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return (null, "This zip couldn't be opened, so PC Tidy can't restore it. It may be damaged or in use.");
        }
    }
}
