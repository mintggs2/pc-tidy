using System.Buffers;
using System.IO.Compression;
using System.IO.Hashing;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Zips a file or folder, reopens the zip and verifies every entry (length + XXH3), and only then
/// moves the original to quarantine. Undo: restore the original from quarantine, then quarantine the zip and remove
/// the folders made for it (only while empty). UndoData: zip path + quarantine id of the original.
/// <para>
/// <c>Args["manifestJson"]</c> (optional): written to the zip's root as <see cref="ManifestEntryName"/> (UTF-8) and
/// verified like every other entry. <see cref="ExtractZipHandler"/> never extracts it.
/// </para>
/// </summary>
public sealed class ArchiveToZipHandler : FileHandlerBase, IPreparingHandler
{
    private const string PartialSuffix = ".pctidy-partial";
    public const string ManifestArg = "manifestJson";
    /// <summary>The manifest entry at the root of a zip PC Tidy made (what was archived, from where).</summary>
    public const string ManifestEntryName = "pctidy-archive.json";
    private static readonly System.Text.UTF8Encoding Utf8 = new(false);

    private static string? ManifestJson(PlanItem item) => item.Args is { } a && a.TryGetValue(ManifestArg, out var m) ? m : null;

    public ArchiveToZipHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.ArchiveToZip;

    private static string ZipTarget(PlanItem item, string source)
        => LongPath.Display(LongPath.Io(item.DestinationPath ?? source + ".zip"));

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "source path");
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.ZipPath] = PlanFinal(source, ZipTarget(step.Item, source), NameClashPolicy.AutoNumber) }) };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.ZipPath) is null) step = Prepare(step, ctx);
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Skipped(step, "It's no longer there.");
        var manifest = TreeManifest.Build(source, ct);
        if (manifest.CopyProblem is { } problem) return Skipped(step, problem);
        var manifestJson = ManifestJson(step.Item);
        if (manifestJson is not null && !manifest.IsDirectory && Path.GetFileName(source).Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase))
            return Skipped(step, $"A file called '{ManifestEntryName}' can't be zipped with PC Tidy's own description of the zip, so it was left alone.");

        // Exactly the journaled name: re-picking one now would leave the intent pointing elsewhere.
        var zip = Get(step, UndoKeys.ZipPath)!;
        if (FsOps.Exists(zip)) return Skipped(step, $"Something new called '{Name(zip)}' appeared just now, so nothing was changed.");
        var temp = zip + PartialSuffix;
        var created = FsOps.CreateParents(zip);
        try
        {
            var expected = await WriteZipAsync(source, manifest, temp, manifestJson, ct).ConfigureAwait(false);
            Verify(temp, expected, ct);
            FsOps.Rename(temp, zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            FsOps.DeleteTree(temp); // our own unfinished zip; the original is untouched
            FsOps.RemoveEmptyDirectories(created);
            if (ex is OperationCanceledException) throw;
            return Failed(step, "Couldn't make the zip: " + Friendly(ex));
        }

        var zipInfo = new FileInfo(LongPath.Io(zip));
        var undo = new Dictionary<string, string>
        {
            [UndoKeys.ZipPath] = zip,
            [UndoKeys.CreatedDirs] = FsOps.EncodeList(created),
        };
        try
        {
            var entry = await ctx.Quarantine.QuarantineAsync(source, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
            undo[UndoKeys.QuarantineId] = entry.Id;
            return Done(step, $"Zipped into '{Name(zip)}' ({Sizes.Format(zipInfo.Length)}) and moved the original to quarantine.", undo, manifest.TotalBytes);
        }
        catch (PartialQuarantineException ex)
        {
            undo[UndoKeys.QuarantineId] = ex.Entry.Id;
            undo[UndoKeys.Partial] = "1";
            return Done(step, $"Zipped into '{Name(zip)}' and moved the original to quarantine. {ex.Note}", undo, ex.Entry.SizeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Original still in place: the zip is a verified duplicate we just made, so remove it again.
            FsOps.DeleteTree(zip);
            FsOps.RemoveEmptyDirectories(created);
            return Failed(step, "The zip was fine, but the original couldn't be moved to quarantine: " + Friendly(ex) + " Nothing changed.");
        }
    }

    /// <summary>Writes the zip; returns entry name → (length, XXH3) of every file for verification.</summary>
    private static Task<Dictionary<string, (long Length, string Hash)>> WriteZipAsync(string source, TreeManifest manifest, string temp, string? manifestJson, CancellationToken ct)
        => Task.Run(() => WriteZip(source, manifest, temp, manifestJson, ct), ct);

    // ZipArchive writes synchronously anyway, so this runs as one off-thread job with one pooled buffer.
    private static Dictionary<string, (long Length, string Hash)> WriteZip(string source, TreeManifest manifest, string temp, string? manifestJson, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var expected = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
            var rootName = Path.GetFileName(source);
            var srcIo = LongPath.Io(source);
            using var fs = new FileStream(LongPath.Io(temp), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var e in manifest.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var entryName = manifest.IsDirectory ? rootName + "/" + e.RelativePath.Replace('\\', '/') : rootName;
                    if (e.IsDirectory)
                    {
                        archive.CreateEntry(entryName + "/");
                        continue;
                    }
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    var lw = new DateTime(e.LastWriteTicks, DateTimeKind.Utc).ToLocalTime();
                    if (lw.Year is >= 1980 and <= 2107) entry.LastWriteTime = lw;
                    var path = manifest.IsDirectory ? Path.Combine(srcIo, e.RelativePath) : srcIo;
                    var hasher = new XxHash3();
                    long len = 0;
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    using (var output = entry.Open())
                    {
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            hasher.Append(buf.AsSpan(0, n));
                            output.Write(buf, 0, n);
                            len += n;
                        }
                    }
                    expected[entryName] = (len, Convert.ToHexString(hasher.GetCurrentHash()));
                }
                if (manifestJson is not null)
                {
                    // Verified like the others (it's in the expected list), so a zip with a broken description never counts as good.
                    var bytes = Utf8.GetBytes(manifestJson);
                    var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    using (var output = entry.Open()) output.Write(bytes);
                    expected[ManifestEntryName] = (bytes.Length, Convert.ToHexString(XxHash3.Hash(bytes)));
                }
            }
            fs.Flush(flushToDisk: true);
            return expected;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>Reopens the zip and checks every file entry's length and hash against what was written.</summary>
    private static void Verify(string zipPath, Dictionary<string, (long Length, string Hash)> expected, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(LongPath.Io(zipPath));
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (files.Count != expected.Count) throw new InvalidDataException("The zip is missing files.");
            foreach (var e in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!expected.TryGetValue(e.FullName, out var want) || e.Length != want.Length) throw new InvalidDataException($"'{e.FullName}' in the zip doesn't match.");
                var hasher = new XxHash3();
                using (var s = e.Open())
                {
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) hasher.Append(buf.AsSpan(0, n));
                }
                if (Convert.ToHexString(hasher.GetCurrentHash()) != want.Hash) throw new InvalidDataException($"'{e.FullName}' in the zip doesn't match.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Task.FromResult(Skipped(step, "It's no longer there."));
        var m = TreeManifest.Build(source, ct);
        if (m.CopyProblem is { } p) return Task.FromResult(Skipped(step, p));
        return Task.FromResult(Simulated(step,
            $"Would zip {Describe(source)} ({m.FileCount} file(s), {Sizes.Format(m.TotalBytes)}) into '{Name(ZipTarget(step.Item, source))}', check it, then move the original to quarantine.",
            m.TotalBytes));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var entry = await FindEntryAsync(ctx, Get(step, UndoKeys.QuarantineId), step.Item.SourcePath, ct).ConfigureAwait(false);
        if (entry is null || entry.Purged) return UndoLeftAlone(step, "The original is no longer in quarantine, so the zip was kept.");
        string restoredTo = entry.OriginalPath;
        if (!entry.Restored)
        {
            try { restoredTo = await RestoreEntryAsync(ctx, entry, Require(step.Item.SourcePath, "source path"), step.Item, ct).ConfigureAwait(false); }
            catch (QuarantineIntegrityException ex) { return UndoLeftAlone(step, ex.Message); }
        }
        var zip = Get(step, UndoKeys.ZipPath);
        if (zip is not null && FsOps.Exists(zip)) await ctx.Quarantine.QuarantineAsync(zip, ctx.RunId, ct).ConfigureAwait(false);
        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs))); // folders made for the zip, if empty now
        var where = LongPath.Same(restoredTo, entry.OriginalPath) ? "" : $" (as '{Name(restoredTo)}', the name was taken)";
        return Undone(step, $"Original put back{where}; the zip was moved to quarantine.");
    }

    public override async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var zip = Get(step, UndoKeys.ZipPath);
        if (zip is null) return false;
        FsOps.DeleteTree(zip + PartialSuffix);
        if (!FsOps.Exists(zip) || FsOps.Exists(source)) return false;
        return await FindEntryAsync(ctx, null, source, ct).ConfigureAwait(false) is not null;
    }
}
