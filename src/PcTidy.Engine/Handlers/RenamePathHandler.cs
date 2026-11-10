using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Renames in two phases through a temp name: every source in a group first goes to a temp name,
/// then every temp goes to its final name. That makes case-only renames ("a.txt" → "A.txt") and
/// swaps ("a" ↔ "b") work. The executor hands consecutive rename steps over as one group.
/// UndoData: temp path (planned before starting) and final path.
/// </summary>
public sealed class RenamePathHandler : FileHandlerBase, IPreparingHandler
{
    public RenamePathHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.RenamePath;

    private static string Destination(PlanItem item)
        => LongPath.Display(LongPath.Io(item.DestinationPath ?? throw new ArgumentException("The plan item has no new name.")));

    private static string TempNameFor(string path)
        => Path.Combine(Path.GetDirectoryName(path)!, $".pctidy-rename-{Guid.NewGuid().ToString("N")[..10]}.tmp");

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var source = Require(step.Item.SourcePath, "source path");
        return step with
        {
            UndoData = Merge(step.UndoData, new() { [UndoKeys.TempPath] = TempNameFor(source), [UndoKeys.FinalPath] = PlanFinal(source, Destination(step.Item), step.Item.ClashPolicy) }),
        };
    }

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var prepared = Get(step, UndoKeys.TempPath) is null ? Prepare(step, ctx) : step;
        return (await ExecuteGroupAsync(new[] { prepared }, ctx, ct).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Phase A: all sources → temp names. Phase B: all temps → final names (clash policy applied now that
    /// the group's own sources are out of the way). Renames are instant, so the group is not cancellable mid-way.
    /// Steps must already be prepared (and journaled as Started).
    /// </summary>
    internal async Task<IReadOnlyList<StepRecord>> ExecuteGroupAsync(IReadOnlyList<StepRecord> steps, HandlerContext ctx, CancellationToken ct)
    {
        var results = new StepRecord[steps.Count];
        var parked = new List<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var source = Require(s.Item.SourcePath, "source path");
            var temp = Get(s, UndoKeys.TempPath)!;
            if (!FsOps.Exists(source)) { results[i] = Skipped(s, "It's no longer there."); continue; }
            try
            {
                var m = TreeManifest.Build(source, ct);
                s = s with { UndoData = Merge(s.UndoData, Identity(source, m)) };
                FsOps.Rename(source, temp);
                steps = Replace(steps, i, s);
                parked.Add(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { results[i] = Failed(s, Friendly(ex)); }
        }

        foreach (var i in parked)
        {
            var s = steps[i];
            var source = Require(s.Item.SourcePath, "source path");
            var temp = Get(s, UndoKeys.TempPath)!;
            try
            {
                var planned = Get(s, UndoKeys.FinalPath) ?? Destination(s.Item);
                var (final, overwrittenId, skip) = await ResolveClashAsync("", planned, s.Item.ClashPolicy, ctx, CancellationToken.None).ConfigureAwait(false);
                if (final is null)
                {
                    FsOps.Rename(temp, source);
                    results[i] = Skipped(s, skip!);
                    continue;
                }
                FsOps.Rename(temp, final);
                var undo = new Dictionary<string, string> { [UndoKeys.FinalPath] = final };
                if (overwrittenId is not null) undo[UndoKeys.OverwrittenQuarantineId] = overwrittenId;
                results[i] = Done(s, LongPath.Same(final, Destination(s.Item)) ? $"Renamed to '{Name(final)}'." : $"Renamed to '{Name(final)}' (name was taken).", undo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results[i] = Failed(s, Friendly(ex) + PutBack(temp, source));
            }
        }
        return results;
    }

    /// <summary>
    /// Undo a group (given in undo order): phase A finals → temps, phase B temps → original names, then any item a
    /// rename replaced (Overwrite quarantined it) goes back to the name it had.
    /// </summary>
    internal async Task<IReadOnlyList<StepRecord>> UndoGroupAsync(IReadOnlyList<StepRecord> steps, HandlerContext ctx, CancellationToken ct)
    {
        var results = new StepRecord[steps.Count];
        var parked = new List<(int Index, string Temp)>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var final = Get(s, UndoKeys.FinalPath) ?? Destination(s.Item);
            if (!FsOps.Exists(final)) { results[i] = UndoLeftAlone(s, $"'{Name(final)}' isn't there any more."); continue; }
            if (!MatchesIdentity(s, final, ct)) { results[i] = UndoLeftAlone(s, $"'{Name(final)}' was changed after the rename, so PC Tidy left it."); continue; }
            var temp = TempNameFor(final);
            try { FsOps.Rename(final, temp); parked.Add((i, temp)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { results[i] = UndoFailed(s, Friendly(ex)); }
        }
        foreach (var (i, temp) in parked)
        {
            var s = steps[i];
            var source = Require(s.Item.SourcePath, "source path");
            var target = FsOps.FreeRestoreName(source);
            try
            {
                FsOps.Rename(temp, target);
                var msg = LongPath.Same(target, source) ? "Renamed back." : $"Renamed back as '{Name(target)}' because the old name is taken.";
                results[i] = Undone(s, msg);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var final = Get(s, UndoKeys.FinalPath) ?? Destination(s.Item);
                results[i] = UndoFailed(s, Friendly(ex) + PutBack(temp, final));
            }
        }
        // Only now, with every name of the group back in place, do replaced items return.
        for (int i = 0; i < results.Length; i++)
            if (results[i].Status == StepStatus.Undone)
                results[i] = results[i] with { Message = results[i].Message + await PutBackReplacedAsync(steps[i], ctx, ct).ConfigureAwait(false) };
        return results;
    }

    /// <summary>Restores the item an Overwrite rename replaced, at the rename's final name (now free again).</summary>
    private static async Task<string> PutBackReplacedAsync(StepRecord s, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(s, UndoKeys.OverwrittenQuarantineId) is not { } id) return "";
        var final = Get(s, UndoKeys.FinalPath) ?? Destination(s.Item);
        try
        {
            return await RestoreOverwrittenAsync(ctx, id, final, s.Item, ct).ConfigureAwait(false) is not null ? " The item it replaced is back too." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return " The item it replaced is still in quarantine: " + Friendly(ex);
        }
    }

    private static string PutBack(string temp, string original)
    {
        try { FsOps.Rename(temp, original); return ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $" It is safe under the temporary name '{Name(temp)}'."; }
    }

    private static IReadOnlyList<StepRecord> Replace(IReadOnlyList<StepRecord> list, int i, StepRecord s)
    {
        var copy = list.ToArray();
        copy[i] = s;
        return copy;
    }

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        return (await UndoGroupAsync(new[] { step }, ctx, ct).ConfigureAwait(false))[0];
    }

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        if (!FsOps.Exists(source)) return Task.FromResult(Skipped(step, "It's no longer there."));
        return Task.FromResult(Simulated(step, $"Would rename {Describe(source)} to '{Name(Destination(step.Item))}'."));
    }

    /// <summary>
    /// Also tidies a half-finished rename: if the item is still parked under its temp name, it is put back
    /// under its original name first, so the answer is always clean (false = safe to run again).
    /// </summary>
    public override Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var source = Require(step.Item.SourcePath, "source path");
        if (Get(step, UndoKeys.TempPath) is { } temp && FsOps.Exists(temp))
        {
            if (!FsOps.Exists(source)) FsOps.Rename(temp, source);
            return Task.FromResult(false);
        }
        var final = Get(step, UndoKeys.FinalPath) ?? Destination(step.Item);
        return Task.FromResult(FsOps.Exists(final) && (!FsOps.Exists(source) || LongPath.Same(source, final) && ExactNameIs(final)));
    }

    /// <summary>Case-only rename: the item exists either way; done when the on-disk name has the new casing.</summary>
    private static bool ExactNameIs(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        return Directory.EnumerateFileSystemEntries(LongPath.Io(dir), name).Any(e => Path.GetFileName(e) == name);
    }
}
