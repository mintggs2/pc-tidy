using System.Buffers;
using System.IO.Hashing;
using PcTidy.Core;

namespace PcTidy.Engine.IO;

/// <summary>What a verified delete removed and what it deliberately left at the original place.</summary>
internal sealed record RemovalReport(int Removed, IReadOnlyList<string> Changed, IReadOnlyList<string> Kept)
{
    /// <summary>Everything of the original is gone (it all lives on in the verified copy).</summary>
    public bool Complete => Changed.Count == 0 && Kept.Count == 0;

    /// <summary>Not a single original item could be removed: the original is fully intact.</summary>
    public bool NothingRemoved => Removed == 0 && !Complete;

    public IReadOnlyList<string> LeftBehind => Changed.Concat(Kept).ToList();

    /// <summary>One plain sentence about what stayed behind ("" when complete).</summary>
    public string Note()
    {
        if (Complete) return "";
        var parts = new List<string>();
        if (Changed.Count > 0) parts.Add($"{Changed.Count} item(s) changed or were added after the copy");
        if (Kept.Count > 0) parts.Add($"{Kept.Count} item(s) were in use");
        return string.Join(" and ", parts) + ", so they were left at the original place. Nothing was lost.";
    }
}

/// <summary>Result of a verified cross-volume move.</summary>
internal sealed record TransferResult(TreeManifest Manifest, TreeManifest CopyManifest, string? FileHash, RemovalReport Removal)
{
    public bool Complete => Removal.Complete;
}

/// <summary>Result of a verified copy: the source as copied, the copy as it now is on its own volume, and a file's XXH3.</summary>
internal sealed record CopyResult(TreeManifest Source, TreeManifest Copy, string? FileHash);

/// <summary>
/// Cross-volume copy that proves every byte arrived (size + XXH3 of every file, the same names and folders, file times
/// within what the destination file system can store) and keeps attributes and creation/modified times. The source is
/// removed only after that proof, and only the items that are still exactly what was copied (see <see cref="RemoveVerified"/>):
/// anything new, changed or in use stays where it is. Data is never lost.
/// </summary>
internal static class VerifiedCopier
{
    private const int MaxBufferSize = 1 << 20;
    /// <summary>Files up to this size are copied with plain synchronous I/O (async overhead dominates for small files).</summary>
    private const int SmallFile = 256 * 1024;

    /// <summary>Copy, verify, then remove the verified source items. Removal ignores cancellation (it's the commit phase).</summary>
    public static async Task<TransferResult> MoveAsync(string source, string destination, CancellationToken ct)
    {
        var copy = await CopyAsync(source, destination, ct).ConfigureAwait(false);
        var removal = RemoveVerified(source, copy.Source);
        return new TransferResult(copy.Source, copy.Copy, copy.FileHash, removal);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> (which must not exist) and verifies it.
    /// Also checks the source didn't change while copying. On any failure the partial copy is removed.
    /// </summary>
    public static async Task<CopyResult> CopyAsync(string source, string destination, CancellationToken ct)
    {
        var before = TreeManifest.Build(source, ct);
        if (before.CopyProblem is { } problem) throw new SafetyRefusalException(problem);
        if (FsOps.Exists(destination)) throw new IOException($"'{Path.GetFileName(LongPath.Display(destination))}' already exists there.");
        try
        {
            string? fileHash = null;
            TreeManifest copy;
            if (before.IsDirectory) copy = await CopyTreeAsync(source, destination, before, ct).ConfigureAwait(false);
            else
            {
                fileHash = await CopyFileAsync(source, destination, ct).ConfigureAwait(false);
                copy = TreeManifest.Build(destination, ct);
            }

            var after = TreeManifest.Build(source, ct);
            if (after.MetadataHash != before.MetadataHash)
                throw new IOException("It changed while PC Tidy was copying it, so nothing was moved.");
            return new CopyResult(before, copy, fileHash);
        }
        catch
        {
            FsOps.DeleteTree(destination); // our own partial copy; the source is untouched
            throw;
        }
    }

    /// <summary>Copies one file (never overwrites), verifies size + XXH3, keeps times/attributes. Returns the hash.</summary>
    public static async Task<string> CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        var srcIo = LongPath.Io(source);
        var dstIo = LongPath.Io(destination);
        var src = new FileInfo(srcIo);
        if (CloudFile.IsPlaceholder(src.Attributes))
            throw new SafetyRefusalException("It's an online-only OneDrive file. PC Tidy never downloads it, so it was left alone.");

        var buffer = ArrayPool<byte>.Shared.Rent(BufferFor(src.Length));
        try
        {
            var hasher = new XxHash3();
            long copied = 0;
            var small = src.Length <= SmallFile;
            // FileShare.Read: nobody may write to the source while it's being copied.
            using (var input = new FileStream(srcIo, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                       small ? FileOptions.SequentialScan : FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var output = new FileStream(dstIo, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       small ? FileOptions.None : FileOptions.Asynchronous))
            {
                if (input.Length > 0) output.SetLength(input.Length);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = small ? input.Read(buffer, 0, buffer.Length) : await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    hasher.Append(buffer.AsSpan(0, n));
                    if (small) output.Write(buffer, 0, n);
                    else await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    copied += n;
                }
                output.SetLength(copied);
                // The copy must be on the disk before the original can ever be removed.
                output.Flush(flushToDisk: true);
            }
            var srcHash = Convert.ToHexString(hasher.GetCurrentHash());

            CopyMetadata(src, new FileInfo(dstIo));

            var dst = new FileInfo(dstIo);
            if (dst.Length != copied || copied != src.Length || HashFile(dstIo, buffer, ct) != srcHash)
            {
                SafeDeleteFile(dstIo);
                throw new IOException("The copy didn't match the original, so nothing was moved.");
            }
            return srcHash;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int BufferFor(long length) => (int)Math.Clamp(length, 4096, MaxBufferSize);

    /// <summary>XXH3-64 of a whole file with a caller-supplied buffer (no per-file large allocations).</summary>
    private static string HashFile(string io, byte[] buffer, CancellationToken ct)
    {
        var h = new XxHash3();
        using var fs = new FileStream(io, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            h.Append(buffer.AsSpan(0, n));
        }
        return Convert.ToHexString(h.GetCurrentHash());
    }

    /// <summary>XXH3-64 of a whole file, with a pooled buffer sized to the file. Caller makes sure it isn't a cloud placeholder.</summary>
    public static string HashFile(string path, CancellationToken ct)
    {
        var io = LongPath.Io(path);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferFor(new FileInfo(io).Length));
        try { return HashFile(io, buffer, ct); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Copies a folder tree and proves the copy: every file's bytes (size + XXH3, checked per file), the same set of
    /// file and folder names, and modified times within what the destination file system keeps (FAT 2 s, exFAT 10 ms).
    /// Returns the copy's own manifest (its times as stored there), which is what later identity checks compare with.
    /// </summary>
    private static async Task<TreeManifest> CopyTreeAsync(string source, string destination, TreeManifest manifest, CancellationToken ct)
    {
        var srcIo = LongPath.Io(source);
        var dstIo = LongPath.Io(destination);
        Directory.CreateDirectory(dstIo);
        var dirs = manifest.Entries.Where(e => e.IsDirectory).OrderBy(e => e.RelativePath.Length).ToList();
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(dstIo, d.RelativePath));
        foreach (var f in manifest.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            await CopyFileAsync(Path.Combine(srcIo, f.RelativePath), Path.Combine(dstIo, f.RelativePath), ct).ConfigureAwait(false);
        }

        // Folder times/attributes go back deepest first (creating children changes a folder's modified time).
        foreach (var d in dirs.OrderByDescending(e => e.RelativePath.Length))
            CopyMetadata(new DirectoryInfo(Path.Combine(srcIo, d.RelativePath)), new DirectoryInfo(Path.Combine(dstIo, d.RelativePath)));
        CopyMetadata(new DirectoryInfo(srcIo), new DirectoryInfo(dstIo));

        var copy = TreeManifest.Build(dstIo, ct);
        if (!SameTree(manifest, copy, VolumeInfo.TimeTolerance(destination)))
            throw new IOException("The copy didn't match the original, so nothing was moved.");
        return copy;
    }

    /// <summary>Same names (case-insensitive), kinds and sizes; file times equal within the destination's precision.</summary>
    internal static bool SameTree(TreeManifest original, TreeManifest copy, TimeSpan timeTolerance)
    {
        if (copy.FileCount != original.FileCount || copy.TotalBytes != original.TotalBytes || copy.Entries.Count != original.Entries.Count)
            return false;
        var byName = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in copy.Entries) byName[c.RelativePath] = c;
        foreach (var e in original.Entries)
        {
            if (!byName.TryGetValue(e.RelativePath, out var c) || c.IsDirectory != e.IsDirectory) return false;
            if (e.IsDirectory) continue;
            if (c.Size != e.Size) return false;
            if (Math.Abs(c.LastWriteTicks - e.LastWriteTicks) > timeTolerance.Ticks) return false;
        }
        return true;
    }

    /// <summary>Creation/modified/accessed times and attributes. Read-only is applied last so the times can be set.</summary>
    public static void CopyMetadata(FileSystemInfo from, FileSystemInfo to)
    {
        from.Refresh();
        to.Attributes = FileAttributes.Normal | (to is DirectoryInfo ? FileAttributes.Directory : 0);
        to.CreationTimeUtc = from.CreationTimeUtc;
        to.LastWriteTimeUtc = from.LastWriteTimeUtc;
        to.LastAccessTimeUtc = from.LastAccessTimeUtc;
        var keep = from.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
        to.Attributes = keep == 0 ? FileAttributes.Normal : keep;
    }

    /// <summary>
    /// Removes the original after a verified copy, but ONLY what was verified: each file listed in
    /// <paramref name="verified"/> is opened exclusively, re-checked (size + modified time) and deleted through that
    /// handle; anything changed, new, or in use stays. Folders are removed only once empty, deepest first.
    /// Never follows links (a link would have stopped the copy).
    /// </summary>
    public static RemovalReport RemoveVerified(string source, TreeManifest verified)
    {
        var changed = new List<string>();
        var kept = new List<string>();
        var removed = 0;
        var rootIo = LongPath.Io(source);
        if (!verified.IsDirectory)
        {
            var e = verified.Entries[0];
            Count(HandleFs.DeleteFileIfUnchanged(rootIo, e.Size, e.LastWriteTicks), LongPath.Display(rootIo));
            return new RemovalReport(removed, changed, kept);
        }

        foreach (var f in verified.Entries.Where(e => !e.IsDirectory))
        {
            var io = Path.Combine(rootIo, f.RelativePath);
            Count(HandleFs.DeleteFileIfUnchanged(io, f.Size, f.LastWriteTicks), LongPath.Display(io));
        }
        var explained = changed.Count + kept.Count;
        string? firstStuckFolder = null;
        var folders = verified.Entries.Where(e => e.IsDirectory)
            .OrderByDescending(e => e.RelativePath.Count(c => c == '\\'))
            .Select(e => Path.Combine(rootIo, e.RelativePath))
            .Append(rootIo);
        foreach (var d in folders)
            if (!RemoveEmptyFolder(d)) firstStuckFolder ??= LongPath.Display(d);
        // A folder that can't go although none of its files were kept holds something new: report it.
        if (explained == 0 && firstStuckFolder is not null) changed.Add(firstStuckFolder);
        return new RemovalReport(removed, changed, kept);

        void Count(DeleteOutcome outcome, string path)
        {
            switch (outcome)
            {
                case DeleteOutcome.Deleted or DeleteOutcome.Missing: removed++; break;
                case DeleteOutcome.Changed: changed.Add(path); break;
                default: kept.Add(path); break;
            }
        }
    }

    /// <summary>Removes a folder only if it is empty (a link: just the link). True if it's gone.</summary>
    private static bool RemoveEmptyFolder(string io)
    {
        FileAttributes? readOnly = null;
        try
        {
            if (Directory.EnumerateFileSystemEntries(io).Any()) return false;
            var a = File.GetAttributes(io);
            if ((a & FileAttributes.ReadOnly) != 0) { File.SetAttributes(io, a & ~FileAttributes.ReadOnly); readOnly = a; }
            Directory.Delete(io, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever stays keeps its attributes exactly as they were.
            try { if (readOnly is { } original) File.SetAttributes(io, original); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static void SafeDeleteFile(string io)
    {
        try { FsOps.ClearReadOnly(io); File.Delete(io); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
