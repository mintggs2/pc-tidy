using System.IO.Compression;
using PcTidy.Core;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Import;

/// <summary>
/// Read-only view of a chat export: the zip as downloaded, or a folder it was extracted to. Entries are relative paths
/// with "/" separators. Zip entries are streamed (never extracted, never loaded whole).
/// </summary>
internal sealed class ExportArchive : IDisposable
{
    private const int MaxFolderEntries = 200_000;

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _zipEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private ExportArchive(string path, OriginKind kind, ZipArchive? zip)
    {
        Path = path;
        Kind = kind;
        _zip = zip;
    }

    public string Path { get; }
    public OriginKind Kind { get; }
    public IEnumerable<string> Entries => _zip is not null ? _zipEntries.Keys : _files.Keys;

    /// <summary>Opens a .zip file or an extracted export folder. Throws a friendly <see cref="InvalidDataException"/> otherwise.</summary>
    public static ExportArchive Open(string path)
    {
        if (Directory.Exists(path)) return OpenFolder(path);
        if (!File.Exists(path)) throw new FileNotFoundException("That file isn't there any more.", path);
        var attributes = File.GetAttributes(path);
        if (CloudFile.IsPlaceholder(attributes))
            throw new InvalidDataException("This file is online-only in OneDrive. Make it available offline first, then import it.");
        var stream = File.OpenRead(path);
        try
        {
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var archive = new ExportArchive(path, OriginKind.ExportZip, zip);
            foreach (var e in zip.Entries)
                if (!string.IsNullOrEmpty(e.Name)) archive._zipEntries.TryAdd(e.FullName.Replace('\\', '/').TrimStart('/'), e);
            return archive;
        }
        catch (InvalidDataException)
        {
            stream.Dispose();
            throw new InvalidDataException("This doesn't look like a zip file. Pick the export zip exactly as you downloaded it.");
        }
    }

    private static ExportArchive OpenFolder(string folder)
    {
        var archive = new ExportArchive(PathUtil.Normalize(folder), OriginKind.ExportFolder, null);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Never follow links out of the folder; never touch online-only files (opening them would download them).
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00400000 | (FileAttributes)0x00040000,
            MaxRecursionDepth = 6,
        };
        foreach (var file in Directory.EnumerateFiles(archive.Path, "*", options))
        {
            if (archive._files.Count >= MaxFolderEntries) break;
            archive._files.TryAdd(System.IO.Path.GetRelativePath(archive.Path, file).Replace('\\', '/'), file);
        }
        return archive;
    }

    public bool Contains(string entry) => _zip is not null ? _zipEntries.ContainsKey(entry) : _files.ContainsKey(entry);

    public Stream OpenEntry(string entry)
    {
        if (_zip is not null) return _zipEntries[entry].Open();
        return new FileStream(_files[entry], FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    public long EntrySize(string entry)
    {
        if (_zip is not null) return _zipEntries.TryGetValue(entry, out var z) ? z.Length : 0;
        try { return _files.TryGetValue(entry, out var f) ? new FileInfo(f).Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>The file on disk for a folder export (null for zips).</summary>
    public string? FilePathOf(string entry) => _zip is null && _files.TryGetValue(entry, out var f) ? f : null;

    /// <summary>Entries whose file name matches, shallowest first.</summary>
    public IReadOnlyList<string> ByFileName(Func<string, bool> nameMatches)
        => Entries.Where(e => nameMatches(FileName(e)))
            .OrderBy(e => e.Count(c => c == '/')).ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();

    public string? FirstNamed(string fileName)
        => ByFileName(n => n.Equals(fileName, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

    public static string FileName(string entry)
    {
        var cut = entry.LastIndexOf('/');
        return cut < 0 ? entry : entry[(cut + 1)..];
    }

    public void Dispose() => _zip?.Dispose();
}
