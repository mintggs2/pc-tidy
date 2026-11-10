using System.Text;
using System.Text.Json;
using PcTidy.Catalog.PcCare;

namespace PcTidy.Platform;

/// <summary>One file of a driver backup, as the admin helper exported it.</summary>
public sealed record DriverBackupFile(string Path, string Sha256, long Size);

/// <summary>
/// What the admin helper exported: every file (relative path, SHA-256, size) and the INF files. Kept in the helper's
/// admin-only store (&lt;admin data&gt;\backups\drivers\manifests), so nothing the user can write decides what gets
/// installed later.
/// </summary>
public sealed record DriverBackupManifest(DateTime CreatedUtc, IReadOnlyList<DriverBackupFile> Files, IReadOnlyList<string> Infs);

/// <summary>
/// Driver backups made and put back by the admin helper.
/// Export: pnputil writes into the admin store, the helper records a manifest there, then copies the files to the user's
/// folder (handle by handle, links refused, nothing overwritten). Restore: the user's folder is copied into the admin
/// store and every driver package (a top-level folder of the backup) is installed only if its files are exactly those of
/// a package in a recorded manifest (same names, sizes and SHA-256) and none of its files is a known vulnerable driver.
/// </summary>
internal static class DriverBackups
{
    private const int MaxFiles = 50_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ManifestFolder(string adminDataRoot) => Path.Combine(DataPaths.Backups(adminDataRoot), "drivers", "manifests");

    /// <summary>Hashes everything under <paramref name="exportFolder"/> and saves the manifest as &lt;id&gt;.json.</summary>
    public static DriverBackupManifest Record(string exportFolder, string manifestFolder, string id, CancellationToken ct)
    {
        var files = Files(exportFolder) ?? throw new IOException("The driver backup contains a link, so it wasn't recorded.");
        var entries = files.Select(f =>
        {
            ct.ThrowIfCancellationRequested();
            return new DriverBackupFile(Relative(exportFolder, f), Sha256Of(f, ct), new FileInfo(f).Length);
        }).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var manifest = new DriverBackupManifest(DateTime.UtcNow, entries, entries.Where(f => IsInf(f.Path)).Select(f => f.Path).ToList());
        Directory.CreateDirectory(manifestFolder);
        File.WriteAllText(Path.Combine(manifestFolder, id + ".json"), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }

    public static IReadOnlyList<DriverBackupManifest> LoadAll(string manifestFolder)
    {
        var list = new List<DriverBackupManifest>();
        if (!Directory.Exists(manifestFolder)) return list;
        foreach (var file in Directory.EnumerateFiles(manifestFolder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<DriverBackupManifest>(File.ReadAllText(file), Json) is { Files: not null } m) list.Add(m);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* a damaged manifest matches nothing */ }
        }
        return list;
    }

    /// <summary>
    /// Copies the admin export to the user's folder with <see cref="NoFollowWriter"/>: every folder opened without
    /// following links, every file created new (an existing file is kept, never overwritten). Returns the files copied.
    /// </summary>
    public static int CopyToUserFolder(string adminCopy, string userFolder, CancellationToken ct)
    {
        var files = Files(adminCopy) ?? throw new IOException("The driver backup contains a link, so it wasn't copied.");
        using var target = NoFollowWriter.Open(userFolder);
        var copied = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var parts = Relative(adminCopy, file).Split('\\');
            var folders = new List<NoFollowWriter>();
            try
            {
                var at = target;
                foreach (var part in parts[..^1]) { at = at.Sub(part); folders.Add(at); }
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (at.CreateFile(parts[^1], source)) copied++;
            }
            finally
            {
                foreach (var f in folders) f.Dispose();
            }
        }
        return copied;
    }

    /// <summary>The result of checking a staged (admin-only) copy of a backup.</summary>
    public sealed record RestoreDecision(IReadOnlyList<string> Approved, IReadOnlyList<string> NotFromABackup, IReadOnlyList<string> Vulnerable);

    /// <summary>
    /// Copies the user's backup folder into <paramref name="staging"/> (links refused), then decides per package.
    /// Packages that aren't approved are deleted from the staging copy, so pnputil only sees approved ones.
    /// Returns null with a reason when nothing could be staged.
    /// </summary>
    public static (RestoreDecision? Decision, string? Problem) StageAndCheck(string source, string staging, IReadOnlyList<DriverBackupManifest> manifests, CancellationToken ct)
    {
        var files = Files(source);
        if (files is null) return (null, "The driver backup contains a link to somewhere else, so PC Tidy won't install from it.");
        if (files.Count == 0) return (null, "The driver backup folder is empty.");
        if (files.Count > MaxFiles) return (null, "The driver backup folder is too large to check.");
        TryDelete(staging);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(staging, Relative(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }

        // Hash the admin-only copy (what pnputil will read), never the user's folder.
        var staged = (Files(staging) ?? new List<string>()).Select(f => new DriverBackupFile(Relative(staging, f), Sha256Of(f, ct), new FileInfo(f).Length)).ToList();
        var known = manifests.SelectMany(m => Packages(m.Files)).Select(p => Signature(p.Files)).ToHashSet(StringComparer.Ordinal);

        var approved = new List<string>();
        var notFromBackup = new List<string>();
        var vulnerable = new List<string>();
        foreach (var package in Packages(staged))
        {
            var name = package.Name.Length == 0 ? "(files at the top of the folder)" : package.Name;
            if (!known.Contains(Signature(package.Files))) notFromBackup.Add(name);
            else if (VulnerableFile(staging, package.Files) is { } bad) vulnerable.Add($"{name} ({bad})");
            else { approved.Add(name); continue; }
            Remove(staging, package);
        }
        return (new RestoreDecision(approved, notFromBackup, vulnerable), null);
    }

    private sealed record Package(string Name, IReadOnlyList<DriverBackupFile> Files);

    /// <summary>A backup's files grouped by top-level folder (one folder per driver package; loose files form one group).</summary>
    private static IEnumerable<Package> Packages(IEnumerable<DriverBackupFile> files)
        => files.GroupBy(f => f.Path.Contains('\\') ? f.Path[..f.Path.IndexOf('\\')] : "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Package(g.Key, g.ToList()));

    /// <summary>Exact identity of a package: every file's path, size and hash, in a fixed order.</summary>
    private static string Signature(IEnumerable<DriverBackupFile> files)
        => string.Join("\n", files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => $"{f.Path.ToUpperInvariant()}|{f.Size}|{f.Sha256.ToUpperInvariant()}"));

    /// <summary>A file of the package (or a file an INF names) on PC Tidy's list of vulnerable drivers, or null.</summary>
    internal static string? VulnerableFile(string root, IEnumerable<DriverBackupFile> files)
    {
        foreach (var f in files)
        {
            if (VulnerableDriverCatalog.Match(Path.GetFileName(f.Path)) is { } hit) return hit.FileName;
            if (!IsInf(f.Path)) continue;
            string text;
            try { text = ReadInf(Path.Combine(root, f.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Path.GetFileName(f.Path); }
            if (VulnerableDriverCatalog.All.FirstOrDefault(d => text.Contains(d.FileName, StringComparison.OrdinalIgnoreCase)) is { } named) return named.FileName;
        }
        return null;
    }

    /// <summary>INF files are UTF-16 (with a byte-order mark) or ANSI; the names PC Tidy looks for are plain ASCII either way.</summary>
    private static string ReadInf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    private static void Remove(string staging, Package package)
    {
        if (package.Name.Length > 0) { TryDelete(Path.Combine(staging, package.Name)); return; }
        foreach (var f in package.Files)
        {
            try { File.Delete(Path.Combine(staging, f.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// SHA-256 (a cryptographic hash: nobody can make a different file with the same value). Not Core's FileHash,
    /// which is a fast non-cryptographic hash meant for finding duplicates.
    /// </summary>
    internal static string Sha256Of(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static bool IsInf(string path) => path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file);

    /// <summary>All files under the folder; null if any file or folder inside is a link (or an online-only file).</summary>
    internal static List<string>? Files(string root)
    {
        var list = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var info in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos("*", options))
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || CloudFile.IsPlaceholder(info.Attributes)) return null;
                if (info is DirectoryInfo) stack.Push(info.FullName);
                else list.Add(info.FullName);
                if (list.Count > MaxFiles) return list;
            }
        }
        return list;
    }

    public static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
