using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Moves a folder to <see cref="PlanItem.DestinationPath"/> and leaves a junction at the old place,
/// so apps keep finding it. Other drive: copy → verify → rename original to "&lt;name&gt;.pctidy-old" →
/// create junction → test read through it → quarantine the old copy. Same drive: rename, then junction.
/// Any failure puts everything back as it was. Undo removes ONLY the junction (never recursive through it)
/// and moves the folder's current content back.
/// </summary>
public sealed class MoveFolderWithJunctionHandler : FileHandlerBase, IPreparingHandler
{
    private const string OldSuffix = ".pctidy-old";
    private const string ModeRename = "rename";
    private const string ModeCopy = "copy";

    public MoveFolderWithJunctionHandler() : base(null) { }
    internal MoveFolderWithJunctionHandler(TransferOptions options) : base(options) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.MoveFolderWithJunction;

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "folder path");
        var final = PlanFinal(source, MovePathHandler.Destination(step.Item, source), NameClashPolicy.AutoNumber);
        return step with
        {
            UndoData = Merge(step.UndoData, new()
            {
                [UndoKeys.FinalPath] = final,
                [UndoKeys.OldPath] = PlannedTargets.FreeName(source + OldSuffix),
            }),
        };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.FinalPath) is null) step = Prepare(step, ctx);
        var source = Require(step.Item.SourcePath, "folder path");
        var info = FsOps.Stat(source);
        if (info is not DirectoryInfo) return Skipped(step, "The folder is no longer there.");
        if (FsOps.IsReparsePoint(info)) return Skipped(step, "It's already a link to somewhere else.");
        var manifest = TreeManifest.Build(source, ct);
        if (manifest.CopyProblem is { } problem) return Skipped(step, problem);

        // Exactly the journaled names: re-picking one now would leave the intent pointing elsewhere.
        var final = Get(step, UndoKeys.FinalPath)!;
        var old = Get(step, UndoKeys.OldPath)!;
        if (LongPath.IsUnder(final, source)) return Skipped(step, "A folder can't be moved into itself.");
        if (FsOps.Exists(final) || FsOps.Exists(old))
            return Skipped(step, $"Something new called '{Name(FsOps.Exists(final) ? final : old)}' appeared just now, so nothing was changed.");
        var created = FsOps.CreateParents(final);
        string mode;
        var arrived = manifest;

        // 1. Get the content to its new home.
        try
        {
            if (!Options.ForceCopy && FsOps.TryRename(source, final) == RenameOutcome.Renamed) mode = ModeRename;
            else
            {
                arrived = (await VerifiedCopier.CopyAsync(source, final, ct).ConfigureAwait(false)).Copy;
                mode = ModeCopy;
                try { FsOps.Rename(source, old); }
                catch
                {
                    FsOps.DeleteTree(final); // our verified duplicate; the original is untouched
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            FsOps.RemoveEmptyDirectories(created);
            if (ex is OperationCanceledException) throw;
            return ex is SafetyRefusalException ? Skipped(step, ex.Message) : Failed(step, Friendly(ex) + " Nothing changed.");
        }

        // 2. Junction at the old place, and prove it reads the same content.
        try
        {
            Junction.Create(source, final);
            if (!Junction.PointsTo(source, final) || !SameTopLevel(source, final))
                throw new IOException("The new link couldn't be read back.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = RollBack(source, final, old, mode);
            FsOps.RemoveEmptyDirectories(created);
            return Failed(step, $"Couldn't make the link: {Friendly(ex)} {rollback}");
        }

        var undo = Identity(final, arrived);
        undo[UndoKeys.FinalPath] = final;
        undo[UndoKeys.Mode] = mode;
        undo[UndoKeys.CreatedDirs] = FsOps.EncodeList(created);
        if (mode == ModeRename)
            return Done(step, $"Moved to '{final}'. A link was left at the old place, so apps still find it.", undo, manifest.TotalBytes);

        // 3. The old copy goes to quarantine (restorable) instead of being deleted.
        undo[UndoKeys.OldPath] = old;
        try
        {
            var entry = await ctx.Quarantine.QuarantineAsync(old, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
            undo[UndoKeys.QuarantineId] = entry.Id;
            return Done(step, $"Moved to '{final}'. A link was left at the old place, so apps still find it. The old copy is in quarantine.", undo, manifest.TotalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(step, $"Moved to '{final}' with a link at the old place. The old copy couldn't go to quarantine ({Friendly(ex)}), so it's still at '{old}'.", undo, manifest.TotalBytes);
        }
    }

    private static bool SameTopLevel(string viaLink, string target)
    {
        static List<string> Names(string p) => Directory.EnumerateFileSystemEntries(LongPath.Io(p)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()!;
        return Names(viaLink).SequenceEqual(Names(target));
    }

    /// <summary>Puts the original back after a failed link step. Returns a sentence for the user.</summary>
    private static string RollBack(string source, string final, string old, string mode)
    {
        try
        {
            if (FsOps.Stat(source) is DirectoryInfo d && FsOps.IsReparsePoint(d)) Junction.Remove(source);
            if (mode == ModeRename) FsOps.Rename(final, source);
            else
            {
                FsOps.Rename(old, source);
                FsOps.DeleteTree(final); // our verified duplicate
            }
            return "Everything was put back as it was.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return mode == ModeRename
                ? $"Your folder is safe at '{final}'."
                : $"Your folder is safe at '{(FsOps.Exists(old) ? old : source)}'.";
        }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "folder path");
        var info = FsOps.Stat(source);
        if (info is not DirectoryInfo) return Task.FromResult(Skipped(step, "The folder is no longer there."));
        if (FsOps.IsReparsePoint(info)) return Task.FromResult(Skipped(step, "It's already a link to somewhere else."));
        var m = TreeManifest.Build(source, ct);
        if (m.CopyProblem is { } p) return Task.FromResult(Skipped(step, p));
        var final = PlanFinal(source, MovePathHandler.Destination(step.Item, source), NameClashPolicy.AutoNumber);
        return Task.FromResult(Simulated(step, $"Would move {Describe(source)} ({Sizes.Format(m.TotalBytes)}) to '{final}' and leave a link at the old place.", m.TotalBytes));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "folder path");
        var final = Get(step, UndoKeys.FinalPath) ?? MovePathHandler.Destination(step.Item, source);
        if (!Junction.PointsTo(source, final))
            return UndoLeftAlone(step, $"'{Name(source)}' isn't the link PC Tidy made any more, so nothing was changed.");
        if (!FsOps.IsDirectory(final)) return UndoLeftAlone(step, $"'{final}' is missing, so the link was left in place.");

        Junction.Remove(source); // the link only: NEVER a recursive delete through it
        try
        {
            if (Options.ForceCopy || FsOps.TryRename(final, source) == RenameOutcome.NotSameVolume)
            {
                // The folder may have been used through the link, so move its CURRENT content back.
                var result = await VerifiedCopier.MoveAsync(final, source, ct).ConfigureAwait(false);
                if (!result.Complete)
                    return Undone(step, $"Moved back. Some items stayed at '{final}': {result.Removal.Note()}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Keep apps working: put the link back.
            try { if (!FsOps.Exists(source)) Junction.Create(source, final); } catch (IOException) { }
            if (ex is OperationCanceledException) throw;
            return UndoFailed(step, $"Couldn't move it back: {Friendly(ex)} The link is still in place.");
        }

        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        var note = Get(step, UndoKeys.QuarantineId) is not null ? " The older copy stays in quarantine until it's emptied." : "";
        return Undone(step, "Moved back and removed the link." + note);
    }

    /// <summary>
    /// Done when the old place is a junction to the final folder. Otherwise a half-finished move is tidied
    /// back to the starting state (originals renamed back; a duplicate copy goes to quarantine, never deleted).
    /// </summary>
    public override async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "folder path");
        var final = Get(step, UndoKeys.FinalPath);
        var old = Get(step, UndoKeys.OldPath);
        if (final is null) return false;
        if (Junction.PointsTo(source, final) && FsOps.IsDirectory(final)) return true;

        if (FsOps.Stat(source) is DirectoryInfo link && FsOps.IsReparsePoint(link)) Junction.Remove(source);
        if (!FsOps.Exists(source))
        {
            if (old is not null && FsOps.Exists(old))
            {
                FsOps.Rename(old, source);
                if (FsOps.Exists(final)) await ctx.Quarantine.QuarantineAsync(final, ctx.RunId, ct).ConfigureAwait(false);
            }
            else if (FsOps.Exists(final)) FsOps.Rename(final, source);
        }
        else if (FsOps.Exists(final))
        {
            await ctx.Quarantine.QuarantineAsync(final, ctx.RunId, ct).ConfigureAwait(false); // unfinished duplicate copy
        }
        return false;
    }
}
