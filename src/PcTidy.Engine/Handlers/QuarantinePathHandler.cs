using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// "Delete" = move into quarantine (restorable). The quarantine entry id is chosen in <see cref="Prepare"/>, so the
/// step's durable intent already says where the item goes (and after a crash the entry can be found and repaired).
/// Undo puts it back only to the step's own source path. UndoData: quarantine entry id.
/// Items with <c>Args["verifyHash"]</c> (duplicates) are re-verified against the kept copy first (<see cref="DuplicateVerifier"/>).
/// </summary>
public sealed class QuarantinePathHandler : FileHandlerBase, IPreparingHandler
{
    public QuarantinePathHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.QuarantinePath;

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
        => ctx.Quarantine is QuarantineStore
            ? step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.QuarantineId] = QuarantineStore.NewEntryId() }) }
            : step;

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        // Duplicates: both copies are re-hashed right before the move; any difference keeps this one.
        if (DuplicateVerifier.Applies(step.Item))
        {
            var problem = await Task.Run(() => DuplicateVerifier.Problem(step.Item, source, ct), ct).ConfigureAwait(false);
            if (problem is not null) return Skipped(step, problem);
        }
        try
        {
            var entry = await QuarantineAsync(ctx, source, Get(step, UndoKeys.QuarantineId), ct).ConfigureAwait(false);
            return Done(step, $"Moved to quarantine. You can restore it for {ctx.Settings.QuarantineDays} days.",
                new() { [UndoKeys.QuarantineId] = entry.Id }, entry.SizeBytes);
        }
        catch (SafetyRefusalException ex) { return Skipped(step, ex.Message); }
        catch (FileNotFoundException) { return Skipped(step, "It's no longer there."); }
        catch (DirectoryNotFoundException) { return Skipped(step, "It's no longer there."); }
        catch (PartialQuarantineException ex)
        {
            // Some originals stayed (changed or in use); everything that went is safe in quarantine.
            return Done(step, $"Moved to quarantine. {ex.Note}",
                new() { [UndoKeys.QuarantineId] = ex.Entry.Id, [UndoKeys.Partial] = "1" }, ex.Entry.SizeBytes);
        }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var info = FsOps.Stat(source);
        if (info is null) return Task.FromResult(Skipped(step, "It's no longer there."));
        if (CloudFile.IsPlaceholder(info.Attributes))
            return Task.FromResult(Skipped(step, "It's an online-only OneDrive file. PC Tidy never downloads it, so it would be left alone."));
        var size = info is FileInfo f ? f.Length : step.Item.ExpectedSize;
        if (DuplicateVerifier.Applies(step.Item))
        {
            // The copy that stays is put in place by an earlier step of this plan (DependsOn, e.g. Takeout moving the
            // year-folder photo). In a simulation that step only "would happen" (the executor lets this step through
            // only then), so the kept copy isn't there yet: check this copy and say what the real run will do.
            bool keptCopyPending = step.Item.DependsOn is { Count: > 0 } && DuplicateVerifier.KeptCopyMissing(step.Item);
            var problem = keptCopyPending ? DuplicateVerifier.SourceProblem(step.Item, source, ct) : DuplicateVerifier.Problem(step.Item, source, ct);
            if (problem is not null) return Task.FromResult(Skipped(step, problem));
            if (keptCopyPending)
                return Task.FromResult(Simulated(step,
                    $"Would move {Describe(source)} ({Sizes.Format(size)}) to quarantine once the copy that stays is in place (an earlier step puts it there), restorable for {ctx.Settings.QuarantineDays} days.", size));
        }
        return Task.FromResult(Simulated(step,
            $"Would move {Describe(source)} ({Sizes.Format(size)}) to quarantine, restorable for {ctx.Settings.QuarantineDays} days.", size));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var entry = await FindEntryAsync(ctx, Get(step, UndoKeys.QuarantineId), source, ct).ConfigureAwait(false);
        if (entry is null) return UndoLeftAlone(step, "Its quarantined copy is gone (deleted for good?), so it can't be put back.");
        if (entry.Purged) return UndoLeftAlone(step, "It was deleted for good from quarantine, so it can't be put back.");
        if (entry.Restored) return Undone(step, "It had already been restored.");
        try
        {
            var restoredTo = await RestoreEntryAsync(ctx, entry, source, step.Item, ct).ConfigureAwait(false);
            return Undone(step, LongPath.Same(restoredTo, source)
                ? "Put back where it was."
                : $"Put back as '{Name(restoredTo)}' because something new is in the way.");
        }
        catch (QuarantineIntegrityException ex) { return UndoLeftAlone(step, ex.Message); }
    }

    /// <summary>
    /// Done when the original is gone and its quarantine entry exists. With the planned entry id from the intent, an
    /// entry whose manifest didn't reach the disk before a power cut is rebuilt, and an unfinished one is tidied away.
    /// </summary>
    public override async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        QuarantineEntry? planned = null;
        if (Get(step, UndoKeys.QuarantineId) is { } id && ctx.Quarantine is QuarantineStore store)
            planned = await store.RecoverAsync(ctx.RunId, id, source, ct).ConfigureAwait(false);
        if (FsOps.Exists(source)) return false;
        return planned is not null || await FindEntryAsync(ctx, null, source, ct).ConfigureAwait(false) is not null;
    }
}
