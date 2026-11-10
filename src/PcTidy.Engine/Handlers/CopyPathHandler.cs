using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Copies a file or folder to <see cref="PlanItem.DestinationPath"/> (full new path; ending in "\" = "into this folder,
/// same name") and leaves the original exactly as it was. Never overwrites: the free name (AutoNumber, or Skip when the
/// plan says so) is chosen when the step is prepared and journaled; if it's taken by then the step is skipped.
/// The copy is written under a temp name, proven (size + XXH3 of every file, same names, times within what the
/// destination keeps, the original unchanged while copying), then renamed into place. Online-only files and links
/// inside stop it. Undo moves the copy to quarantine if it's still exactly what was copied.
/// UndoData: final path, folders created, identity of the copy.
/// </summary>
public sealed class CopyPathHandler : FileHandlerBase, IPreparingHandler
{
    private const string PartialSuffix = ".pctidy-partial";

    public CopyPathHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.CopyPath;

    private static NameClashPolicy Policy(PlanItem item) => item.ClashPolicy == NameClashPolicy.Skip ? NameClashPolicy.Skip : NameClashPolicy.AutoNumber;

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "source path");
        var final = PlanFinal("", MovePathHandler.Destination(step.Item, source), Policy(step.Item));
        return step with { UndoData = Merge(step.UndoData, new() { [UndoKeys.FinalPath] = final }) };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.FinalPath) is null) step = Prepare(step, ctx);
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Skipped(step, "It's no longer there.");
        var final = Get(step, UndoKeys.FinalPath)!;
        if (LongPath.IsUnder(final, source)) return Skipped(step, "A folder can't be copied into itself.");
        var temp = final + PartialSuffix;
        if (FsOps.Exists(final) || FsOps.Exists(temp))
            return Skipped(step, Policy(step.Item) == NameClashPolicy.Skip
                ? $"Something called '{Name(final)}' is already there."
                : $"Something new called '{Name(final)}' appeared just now, so nothing was copied.");

        var created = FsOps.CreateParents(final);
        try
        {
            var copy = await VerifiedCopier.CopyAsync(source, temp, ct).ConfigureAwait(false);
            FsOps.Rename(temp, final); // never replaces
            var undo = Identity(final, copy.Copy, copy.FileHash);
            undo[UndoKeys.FinalPath] = final;
            undo[UndoKeys.CreatedDirs] = FsOps.EncodeList(created);
            var renamed = LongPath.Same(final, MovePathHandler.Destination(step.Item, source)) ? "" : " (the name was taken)";
            return Done(step, $"Copied to '{final}'{renamed}. The original is untouched.", undo, copy.Copy.TotalBytes);
        }
        catch (SafetyRefusalException ex)
        {
            FsOps.RemoveEmptyDirectories(created);
            return Skipped(step, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            FsOps.DeleteTree(temp); // our own partial copy
            FsOps.RemoveEmptyDirectories(created);
            if (ex is OperationCanceledException) throw;
            return Failed(step, "Couldn't copy it: " + Friendly(ex) + " Nothing changed.");
        }
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Task.FromResult(Skipped(step, "It's no longer there."));
        var m = TreeManifest.Build(source, ct);
        if (m.CopyProblem is { } p) return Task.FromResult(Skipped(step, p));
        var dest = FsOps.NextFreeName(MovePathHandler.Destination(step.Item, source));
        return Task.FromResult(Simulated(step,
            $"Would copy {Describe(source)} ({m.FileCount} file(s), {Sizes.Format(m.TotalBytes)}) to '{dest}' and check the copy. The original stays.", m.TotalBytes));
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath);
        if (final is null || !FsOps.Exists(final)) return Undone(step, "The copy was already gone.");
        if (!MatchesIdentity(step, final, ct)) return UndoLeftAlone(step, $"The copy '{Name(final)}' was changed after PC Tidy made it, so it was left alone.");
        await ctx.Quarantine.QuarantineAsync(final, ctx.RunId, ct).ConfigureAwait(false);
        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        return Undone(step, "Moved the copy to quarantine. The original was never touched.");
    }

    /// <summary>Done when the copy is at its planned name (the temp copy only becomes it after verification).</summary>
    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var final = Get(step, UndoKeys.FinalPath);
        if (final is null) return Task.FromResult(false);
        FsOps.DeleteTree(final + PartialSuffix); // an unfinished, unverified copy of ours
        return Task.FromResult(FsOps.Exists(final));
    }
}
