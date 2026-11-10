using System.Buffers;
using System.IO.Compression;
using System.IO.Hashing;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Unzips into a NEW folder (AutoNumber if the name is taken), via a temp folder that is only renamed
/// into place after every file was checked. Entries that would land outside the folder are refused.
/// The zip itself is kept. Undo moves the extracted folder to quarantine. UndoData: final folder.
/// <para>
/// <c>Args["unwrapRoot"]</c> (optional): the zip's top-level folder name whose CONTENTS go directly into
/// DestinationPath (restoring an archived "Album" lands in "Album", not "Album\Album"); every other entry must be
/// inside that folder. PC Tidy's own description entry (<see cref="ArchiveToZipHandler.ManifestEntryName"/> at the
/// zip's root) is never extracted.
/// </para>
/// </summary>
public sealed class ExtractZipHandler : FileHandlerBase, IPreparingHandler
{
    private const string PartialSuffix = ".pctidy-partial";
    public const string UnwrapRootArg = "unwrapRoot";

    private static string? UnwrapRoot(PlanItem item)
    {
        if (item.Args is not { } a || !a.TryGetValue(UnwrapRootArg, out var r) || string.IsNullOrWhiteSpace(r)) return null;
        var root = r.Trim('/', '\\');
        if (root.Length == 0 || root is "." or ".." || root.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || root.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The folder to unwrap isn't a plain folder name.");
        return root;
    }

    public ExtractZipHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.ExtractZip;

    private static string Target(PlanItem item, string zip)
        => LongPath.Display(LongPath.Io(item.DestinationPath ?? Path.Combine(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip))));

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var zip = Require(step.Item.SourcePath, "zip path");
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.FinalPath] = PlanFinal(zip, Target(step.Item, zip), NameClashPolicy.AutoNumber) }) };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.FinalPath) is null) step = Prepare(step, ctx);
        var zip = Require(step.Item.SourcePath, "zip path");
        var info = FsOps.Stat(zip);
        if (info is not FileInfo) return Skipped(step, "The zip is no longer there.");
        if (CloudFile.IsPlaceholder(info.Attributes)) return Skipped(step, "The zip is an online-only OneDrive file. PC Tidy never downloads it.");

        // Exactly the journaled folder name: re-picking one now would leave the intent pointing elsewhere.
        var final = Get(step, UndoKeys.FinalPath)!;
        if (FsOps.Exists(final)) return Skipped(step, $"Something new called '{Name(final)}' appeared just now, so nothing was changed.");
        string? unwrap;
        try { unwrap = UnwrapRoot(step.Item); }
        catch (ArgumentException ex) { return Failed(step, ex.Message); }
        var temp = final + PartialSuffix;
        var created = FsOps.CreateParents(final);
        try
        {
            var count = await Task.Run(() => Extract(zip, temp, unwrap, ct), ct).ConfigureAwait(false);
            FsOps.Rename(temp, final);
            var m = TreeManifest.Build(final, ct);
            var undo = Identity(final, m);
            undo[UndoKeys.FinalPath] = final;
            undo[UndoKeys.CreatedDirs] = FsOps.EncodeList(created);
            return Done(step, $"Extracted {count} file(s) into '{Name(final)}'.", undo, m.TotalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            FsOps.DeleteTree(temp); // our own unfinished folder
            FsOps.RemoveEmptyDirectories(created);
            if (ex is OperationCanceledException) throw;
            return Failed(step, "Couldn't extract it: " + Friendly(ex));
        }
    }

    private static int Extract(string zip, string temp, string? unwrapRoot, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var rootIo = LongPath.Io(temp);
            Directory.CreateDirectory(rootIo);
            var rootDisplay = LongPath.Display(rootIo);
            using var archive = new ZipArchive(new FileStream(LongPath.Io(zip), FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
            int files = 0;
            foreach (var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.FullName == ArchiveToZipHandler.ManifestEntryName) continue; // PC Tidy's own description of the zip
                var name = e.FullName;
                if (unwrapRoot is not null)
                {
                    var prefix = unwrapRoot + "/";
                    if (name == prefix) continue; // the unwrapped folder itself
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidDataException($"The zip has '{e.FullName}' outside its '{unwrapRoot}' folder, so it wasn't unpacked.");
                    name = name[prefix.Length..];
                }
                var rel = name.Replace('/', '\\');
                if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('\\').Any(p => p is ".." or "."))
                    throw new InvalidDataException($"The zip contains an unsafe path ('{e.FullName}').");
                var target = Path.Combine(rootDisplay, rel.TrimEnd('\\'));
                if (!LongPath.IsUnder(target, rootDisplay)) throw new InvalidDataException($"The zip contains an unsafe path ('{e.FullName}').");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(LongPath.Io(target)); continue; }

                Directory.CreateDirectory(LongPath.Io(Path.GetDirectoryName(target)!));
                var hasher = new XxHash3();
                long len = 0;
                using (var input = e.Open())
                using (var output = new FileStream(LongPath.Io(target), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hasher.Append(buf.AsSpan(0, n));
                        output.Write(buf, 0, n);
                        len += n;
                    }
                    output.Flush(flushToDisk: true);
                }
                if (len != e.Length || VerifiedCopier.HashFile(target, ct) != Convert.ToHexString(hasher.GetCurrentHash()))
                    throw new InvalidDataException($"'{e.FullName}' didn't extract correctly.");
                File.SetLastWriteTimeUtc(LongPath.Io(target), e.LastWriteTime.UtcDateTime);
                files++;
            }
            return files;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var zip = Require(step.Item.SourcePath, "zip path");
        if (FsOps.Stat(zip) is not FileInfo) return Task.FromResult(Skipped(step, "The zip is no longer there."));
        return Task.FromResult(Simulated(step, $"Would extract {Describe(zip)} into '{FsOps.NextFreeName(Target(step.Item, zip))}'. The zip is kept."));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath);
        if (final is null || !FsOps.Exists(final)) return Undone(step, "The extracted folder was already gone.");
        await ctx.Quarantine.QuarantineAsync(final, ctx.RunId, ct).ConfigureAwait(false);
        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        return Undone(step, "Moved the extracted folder to quarantine. The zip is still there.");
    }

    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath);
        if (final is null) return Task.FromResult(false);
        FsOps.DeleteTree(final + PartialSuffix);
        return Task.FromResult(FsOps.IsDirectory(final));
    }
}
