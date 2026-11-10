using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Moves a file or folder. <see cref="PlanItem.DestinationPath"/> is the full new path
/// (ending in "\" = "into this folder, same name"). Same volume = atomic rename; other volume =
/// copy → verify → remove the verified originals (anything changed or in use stays, nothing is lost).
/// Clash: AutoNumber picks "name (2).ext" when the step is prepared (journaled before it starts) or Skip; Overwrite
/// quarantines the existing item first. Records the folders it created so undo can remove them if empty.
/// UndoData: final path (+ identity of what arrived there).
/// </summary>
public sealed class MovePathHandler : FileHandlerBase, IPreparingHandler
{
    public MovePathHandler() : base(null) { }
    internal MovePathHandler(TransferOptions options) : base(options) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.MovePath;

    internal static string Destination(PlanItem item, string source)
    {
        var dest = item.DestinationPath ?? throw new ArgumentException("The plan item has no destination.");
        if (dest.EndsWith('\\') || dest.EndsWith('/')) dest = Path.Combine(dest, Path.GetFileName(source));
        return LongPath.Display(LongPath.Io(dest));
    }

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var final = PlanFinal(source, Destination(step.Item, source), step.Item.ClashPolicy);
        return step with
        {
            UndoData = Merge(step.UndoData, new()
            {
                [UndoKeys.FinalPath] = final,
                [UndoKeys.CreatedDirs] = FsOps.EncodeList(FsOps.MissingDirectories(Path.GetDirectoryName(final)!)),
            }),
        };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.FinalPath) is null) step = Prepare(step, ctx);
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Skipped(step, "It's no longer there.");
        var planned = Get(step, UndoKeys.FinalPath)!;
        var (final, overwrittenId, skip) = await ResolveClashAsync(source, planned, step.Item.ClashPolicy, ctx, ct).ConfigureAwait(false);
        if (final is null) return Skipped(step, skip!);
        if (LongPath.IsUnder(final, source) && !LongPath.Same(final, source))
            return await Abandon(Skipped(step, "A folder can't be moved into itself.")).ConfigureAwait(false);

        var manifest = TreeManifest.Build(source, ct);
        if (manifest.CloudPlaceholders.Count > 0) return await Abandon(Skipped(step, manifest.CopyProblem!)).ConfigureAwait(false);

        var created = FsOps.CreateParents(final);
        var renamedName = LongPath.Same(final, Destination(step.Item, source)) ? $"Moved to '{final}'." : $"Moved to '{final}' (name was taken).";
        try
        {
            if (!Options.ForceCopy && FsOps.TryRename(source, final) == RenameOutcome.Renamed)
                return Done(step, renamedName, UndoData(final, created, manifest, null, overwrittenId), manifest.TotalBytes);

            var result = await VerifiedCopier.MoveAsync(source, final, ct).ConfigureAwait(false);
            if (result.Removal.NothingRemoved)
            {
                // The original is fully intact, so the verified duplicate goes again: nothing changed.
                FsOps.DeleteTree(final);
                FsOps.RemoveEmptyDirectories(created);
                return await Abandon(Failed(step, (result.Removal.Kept.Count > 0 ? "It's in use by another app right now." : "It changed while being moved.")
                                                  + " Nothing changed.")).ConfigureAwait(false);
            }
            var undo = UndoData(final, created, result.CopyManifest, result.FileHash, overwrittenId, partial: !result.Complete);
            return Done(step, result.Complete ? renamedName : $"{renamedName} {result.Removal.Note()}", undo, result.CopyManifest.TotalBytes);
        }
        catch (OperationCanceledException)
        {
            FsOps.RemoveEmptyDirectories(created);
            await RestoreOverwrittenAsync(ctx, overwrittenId, final, step.Item, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (SafetyRefusalException ex)
        {
            FsOps.RemoveEmptyDirectories(created);
            return await Abandon(Skipped(step, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FsOps.RemoveEmptyDirectories(created);
            return await Abandon(Failed(step, Friendly(ex))).ConfigureAwait(false);
        }

        async Task<StepRecord> Abandon(StepRecord r)
        {
            await RestoreOverwrittenAsync(ctx, overwrittenId, final!, step.Item, CancellationToken.None).ConfigureAwait(false);
            return r;
        }
    }

    private static Dictionary<string, string> UndoData(string final, List<string> created, TreeManifest arrived, string? hash, string? overwrittenId, bool partial = false)
    {
        var d = Identity(final, arrived, hash);
        d[UndoKeys.FinalPath] = final;
        d[UndoKeys.CreatedDirs] = FsOps.EncodeList(created);
        if (overwrittenId is not null) d[UndoKeys.OverwrittenQuarantineId] = overwrittenId;
        if (partial) d[UndoKeys.Partial] = "1";
        return d;
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Task.FromResult(Skipped(step, "It's no longer there."));
        var dest = Destination(step.Item, source);
        if (FsOps.Exists(dest) && !LongPath.Same(source, dest))
        {
            var msg = step.Item.ClashPolicy switch
            {
                NameClashPolicy.Skip => $"Would skip: something called '{Name(dest)}' is already there.",
                NameClashPolicy.Overwrite => $"Would move the existing '{Name(dest)}' to quarantine, then move {Describe(source)} there.",
                _ => $"Would move {Describe(source)} to '{FsOps.NextFreeName(dest)}' (name is taken).",
            };
            return Task.FromResult(step.Item.ClashPolicy == NameClashPolicy.Skip ? Skipped(step, msg) : Simulated(step, msg, step.Item.ExpectedSize));
        }
        return Task.FromResult(Simulated(step, $"Would move {Describe(source)} to '{dest}'.", step.Item.ExpectedSize));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var final = Get(step, UndoKeys.FinalPath) ?? Destination(step.Item, source);
        if (!FsOps.Exists(final)) return UndoLeftAlone(step, $"It isn't at '{final}' any more, so there's nothing to move back.");
        if (!MatchesIdentity(step, final, ct))
            return UndoLeftAlone(step, $"'{Name(final)}' was changed after the move, so PC Tidy left it where it is.");

        var target = FsOps.FreeRestoreName(source);
        var createdBack = FsOps.CreateParents(target);
        string note = "";
        try
        {
            if (Options.ForceCopy || FsOps.TryRename(final, target) == RenameOutcome.NotSameVolume)
            {
                var result = await VerifiedCopier.MoveAsync(final, target, ct).ConfigureAwait(false);
                if (!result.Complete) note = $" Some items stayed at '{final}' too: {result.Removal.Note()}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FsOps.RemoveEmptyDirectories(createdBack);
            return UndoFailed(step, Friendly(ex));
        }

        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        string? restoredOverwritten = null;
        try { restoredOverwritten = await RestoreOverwrittenAsync(ctx, Get(step, UndoKeys.OverwrittenQuarantineId), final, step.Item, ct).ConfigureAwait(false); }
        catch (QuarantineIntegrityException ex) { note += " " + ex.Message; }
        var msg = LongPath.Same(target, source) ? "Moved back." : $"Moved back as '{Name(target)}' because something new is in the way.";
        if (restoredOverwritten is not null) msg += " The item it replaced is back too.";
        return Undone(step, msg + note);
    }

    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var final = Get(step, UndoKeys.FinalPath);
        return Task.FromResult(final is not null && FsOps.Exists(final) && !FsOps.Exists(source));
    }
}
