using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Base for handlers that rewrite one small text file in place, keeping a byte-exact backup (see <see cref="FileBackups"/>).
/// <list type="bullet">
/// <item>Prepare plans the temp name, the backup name and the backup's quarantine id (all journaled in the intent),
/// and, when the file can be read, the hashes of the original and of the new content.</item>
/// <item>Execute re-reads the file through a verified handle (no links, exact path), skips if it changed since the plan,
/// writes the new content to the temp file, flushes it, swaps it in with ReplaceFile (the original becomes the backup:
/// same bytes, times and attributes), then quarantines the backup.</item>
/// <item>Undo puts the original back only if the file is still exactly what PC Tidy wrote (otherwise it's left alone and
/// the original stays in quarantine), restoring its bytes, times and attributes.</item>
/// <item>WasCompleted: the backup exists (beside the file or in quarantine) ⇔ the swap happened. A swap that stopped
/// half-way is put back.</item>
/// </list>
/// Nothing here trusts names it didn't plan: temp and backup must be planned siblings of the file (the elevated helper
/// receives steps prepared by the app).
/// </summary>
public abstract class BackedUpTextEditHandler : FileHandlerBase, IPreparingHandler
{
    internal const long MaxEditBytes = 32L << 20;
    private const string ModeEdit = "edit";
    private const string ModeCreate = "create";
    private const string ModeUnchanged = "unchanged";
    private const string BadPlan = "PC Tidy's plan for this step doesn't look right, so nothing was changed.";

    internal BackedUpTextEditHandler() : base(null) { }

    /// <summary>The file this step changes (validated, display form).</summary>
    protected abstract string Target(PlanItem item, HandlerContext ctx);

    /// <summary>Why this step may not run (or be undone) here at all, or null.</summary>
    protected virtual string? Refusal(PlanItem item, HandlerContext ctx) => null;

    /// <summary>May a missing file be created (undo then removes it)?</summary>
    protected virtual bool CreatesWhenMissing(PlanItem item) => false;

    /// <summary>The new bytes, from the original (null when creating). Bytes null = skip with <see cref="Edit.SkipReason"/>.</summary>
    protected abstract Edit Transform(byte[]? original, PlanItem item);

    /// <summary>"'pip.ini'", "the hosts file" - for messages.</summary>
    protected abstract string What(string target);

    protected sealed record Edit(byte[]? Bytes, string? SkipReason, string Summary, string? Format = null);

    // ------------------------------------------------------------------ prepare

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var target = Target(step.Item, ctx);
        var d = new Dictionary<string, string> { [UndoKeys.TempPath] = FileBackups.PlanSibling(target, FileBackups.NewSuffix) };
        var info = FsOps.Stat(target);
        if (info is null && CreatesWhenMissing(step.Item))
        {
            d[UndoKeys.Mode] = ModeCreate;
            TryPlanHashes(d, null, step.Item);
        }
        else
        {
            d[UndoKeys.Mode] = ModeEdit;
            d[UndoKeys.BackupPath] = FileBackups.PlanSibling(target, FileBackups.BackupSuffix);
            if (ctx.Quarantine is QuarantineStore) d[UndoKeys.QuarantineId] = QuarantineStore.NewEntryId();
            if (info is FileInfo f && !CloudFile.IsPlaceholder(f.Attributes) && !FsOps.IsReparsePoint(f))
            {
                try { TryPlanHashes(d, FileBackups.ReadVerified(target, MaxEditBytes), step.Item); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* checked again when it runs */ }
            }
        }
        return step with { UndoData = Merge(step.UndoData, d) };
    }

    private void TryPlanHashes(Dictionary<string, string> d, byte[]? original, PlanItem item)
    {
        if (original is not null) d[UndoKeys.OriginalHash] = FileBackups.Hash(original);
        try
        {
            if (Transform(original, item).Bytes is { } b) d[UndoKeys.Hash] = FileBackups.Hash(b);
        }
        catch (ArgumentException) { /* reported when it runs */ }
    }

    // ------------------------------------------------------------------ execute

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.TempPath) is null) step = Prepare(step, ctx);
        if (Refusal(step.Item, ctx) is { } no) return Skipped(step, no);
        var target = Target(step.Item, ctx);
        var temp = Get(step, UndoKeys.TempPath);
        if (!FileBackups.IsPlannedSibling(temp, target, FileBackups.NewSuffix)) return Skipped(step, BadPlan);
        if (FsOps.Exists(temp!)) return Skipped(step, AppearedJustNow(temp!));
        return Get(step, UndoKeys.Mode) == ModeCreate
            ? Create(step, target, temp!)
            : await EditAsync(step, ctx, target, temp!).ConfigureAwait(false);
    }

    private StepRecord Create(StepRecord step, string target, string temp)
    {
        if (FsOps.Exists(target)) return Skipped(step, $"Something new called '{Name(target)}' appeared just now, so nothing was saved.");
        Edit e;
        try { e = Transform(null, step.Item); }
        catch (ArgumentException ex) { return Failed(step, ex.Message); }
        if (e.Bytes is not { } bytes) return Skipped(step, e.SkipReason ?? "There's nothing to write.");
        var hash = FileBackups.Hash(bytes);
        if (Get(step, UndoKeys.Hash) is { } planned && planned != hash) return Skipped(step, BadPlan);

        var created = FsOps.CreateParents(target);
        try
        {
            FileBackups.WriteNew(temp, bytes);
            FsOps.Rename(temp, target); // never replaces: if something appeared meanwhile, nothing is lost
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FsOps.DeleteTree(temp); // our own temp file
            FsOps.RemoveEmptyDirectories(created);
            return Failed(step, Friendly(ex));
        }
        return Done(step, $"Created {What(target)}.", new()
        {
            [UndoKeys.Mode] = ModeCreate,
            [UndoKeys.Hash] = hash,
            [UndoKeys.CreatedDirs] = FsOps.EncodeList(created),
        });
    }

    private async Task<StepRecord> EditAsync(StepRecord step, HandlerContext ctx, string target, string temp)
    {
        var info = FsOps.Stat(target);
        if (Problem(info, step.Item) is { } problem) return Skipped(step, problem);
        var backup = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(backup, target, FileBackups.BackupSuffix)) return Skipped(step, BadPlan);
        if (FsOps.Exists(backup!)) return Skipped(step, AppearedJustNow(backup!));

        byte[] original;
        try { original = FileBackups.ReadVerified(target, MaxEditBytes); }
        catch (SafetyRefusalException ex) { return Skipped(step, ex.Message); }
        catch (FileTooBigException ex) { return Skipped(step, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Skipped(step, Friendly(ex)); }
        var originalHash = FileBackups.Hash(original);
        if (Get(step, UndoKeys.OriginalHash) is { } plannedOriginal && plannedOriginal != originalHash)
            return Skipped(step, "It changed just now, so it was left alone.");

        Edit e;
        try { e = Transform(original, step.Item); }
        catch (ArgumentException ex) { return Failed(step, ex.Message); }
        if (e.Bytes is not { } bytes) return Skipped(step, e.SkipReason ?? "There's nothing to change.");
        if (bytes.AsSpan().SequenceEqual(original))
            return Done(step, $"Nothing needed changing: {What(target)} already says that.", new() { [UndoKeys.Mode] = ModeUnchanged });
        var newHash = FileBackups.Hash(bytes);
        if (Get(step, UndoKeys.Hash) is { } plannedNew && plannedNew != newHash)
            return Skipped(step, "It changed just now, so it was left alone.");

        var undo = FileBackups.Meta(info!);
        undo[UndoKeys.Mode] = ModeEdit;
        undo[UndoKeys.OriginalHash] = originalHash;
        undo[UndoKeys.Hash] = newHash;
        if (e.Format is { } format) undo[UndoKeys.TextFormat] = format;
        try
        {
            FileBackups.WriteNew(temp, bytes);
            FileBackups.Replace(temp, target, backup, info!.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FsOps.DeleteTree(temp); // our own new content; the original is in place
            return Failed(step, Friendly(ex) + " Nothing was changed.");
        }

        // The swap is done; the original file object now sits at the backup name. It goes to quarantine under the id
        // the intent planned. If that fails it stays beside the file (undo still finds it there).
        undo[UndoKeys.BackupInQuarantine] = "0";
        var where = $" The original is kept next to it as '{Name(backup!)}'.";
        try
        {
            var entry = await QuarantineAsync(ctx, backup!, Get(step, UndoKeys.QuarantineId), CancellationToken.None).ConfigureAwait(false);
            undo[UndoKeys.QuarantineId] = entry.Id;
            undo[UndoKeys.BackupInQuarantine] = "1";
            where = " The original is saved in quarantine; Undo puts it back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept beside the file */ }
        return Done(step, e.Summary + "." + where, undo);
    }

    /// <summary>Why the existing file can't be edited (missing, folder, link, online-only, changed since the scan), or null.</summary>
    private static string? Problem(FileSystemInfo? info, PlanItem item)
    {
        if (info is null) return "It's no longer there.";
        if (info is DirectoryInfo) return "It's a folder, not a file, so it was left alone.";
        if (FsOps.IsReparsePoint(info)) return "It's a link to somewhere else, so PC Tidy left it alone.";
        if (CloudFile.IsPlaceholder(info.Attributes)) return "It's an online-only OneDrive file. PC Tidy never downloads it.";
        if (item.ExpectedLastWriteUtc is { } expected)
        {
            var want = expected.Kind == DateTimeKind.Local ? expected.ToUniversalTime() : DateTime.SpecifyKind(expected, DateTimeKind.Utc);
            if (info.LastWriteTimeUtc.Ticks != want.Ticks) return "It changed since the scan, so it was left alone.";
        }
        return null;
    }

    private static string AppearedJustNow(string path) => $"Something new called '{Name(path)}' appeared just now, so nothing was changed.";

    // ------------------------------------------------------------------ simulate

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Refusal(step.Item, ctx) is { } no) return Task.FromResult(Skipped(step, no));
        var target = Target(step.Item, ctx);
        var info = FsOps.Stat(target);
        if (info is null && CreatesWhenMissing(step.Item))
            return Task.FromResult(Simulated(step, $"Would create {What(target)}. Undo would remove it again."));
        if (Problem(info, step.Item) is { } problem) return Task.FromResult(Skipped(step, problem));
        try
        {
            var original = FileBackups.ReadVerified(target, MaxEditBytes);
            var e = Transform(original, step.Item);
            if (e.Bytes is null) return Task.FromResult(Skipped(step, e.SkipReason ?? "There's nothing to change."));
            if (e.Bytes.AsSpan().SequenceEqual(original))
                return Task.FromResult(Simulated(step, $"Nothing would change: {What(target)} already says that."));
            return Task.FromResult(Simulated(step, $"Would do this: {e.Summary}. The original would be saved in quarantine first, and Undo puts it back."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(Skipped(step, Friendly(ex)));
        }
    }

    // ------------------------------------------------------------------ undo

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var mode = Get(step, UndoKeys.Mode);
        if (mode == ModeUnchanged) return Undone(step, "Nothing had changed.");
        if (Refusal(step.Item, ctx) is { } no) return UndoFailed(step, no);
        var target = Target(step.Item, ctx);
        if (FileBackups.RunningApps(step.Item.RequiresClosedApps) is { Count: > 0 } open)
            return UndoFailed(step, $"Close {string.Join(", ", open)} first, then undo again.");
        if (FsOps.Stat(target) is FileInfo && ctx.InUse.IsLocked(target)) return UndoFailed(step, "It's in use right now. Try Undo again later.");
        return mode == ModeCreate
            ? await UndoCreateAsync(step, ctx, target, ct).ConfigureAwait(false)
            : await UndoEditAsync(step, ctx, target, ct).ConfigureAwait(false);
    }

    private async Task<StepRecord> UndoCreateAsync(StepRecord step, HandlerContext ctx, string target, CancellationToken ct)
    {
        if (FsOps.Stat(target) is not FileInfo) return Undone(step, "It was already gone.");
        if (VerifiedCopier.HashFile(target, ct) != Get(step, UndoKeys.Hash))
            return UndoLeftAlone(step, $"'{Name(target)}' was edited after PC Tidy created it, so it was left alone.");
        await ctx.Quarantine.QuarantineAsync(target, ctx.RunId, ct).ConfigureAwait(false);
        FsOps.RemoveEmptyDirectories(FsOps.DecodeList(Get(step, UndoKeys.CreatedDirs)));
        return Undone(step, $"Removed {What(target)} PC Tidy created (it's in quarantine).");
    }

    private async Task<StepRecord> UndoEditAsync(StepRecord step, HandlerContext ctx, string target, CancellationToken ct)
    {
        var current = FsOps.Stat(target);
        if (current is not null && (current is not FileInfo || FsOps.IsReparsePoint(current)))
            return UndoLeftAlone(step, $"Something else is at '{Name(target)}' now, so the original wasn't put back.");
        var backup = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(backup, target, FileBackups.BackupSuffix)) return UndoLeftAlone(step, BadPlan);

        // The current file must still be exactly what PC Tidy wrote (a later change by the app or the user is kept).
        string? expectedNow = Get(step, UndoKeys.Hash);
        if (current is not null && expectedNow is not null && VerifiedCopier.HashFile(target, ct) != expectedNow)
            return UndoLeftAlone(step, $"{Capital(What(target))} changed after PC Tidy edited it, so it was left alone. The original is still in quarantine.");

        // The backup: from quarantine (back to its planned sibling name only), or still beside the file.
        string source;
        var entry = await FindEntryAsync(ctx, Get(step, UndoKeys.QuarantineId), backup, ct).ConfigureAwait(false);
        if (entry is { Restored: false, Purged: false })
        {
            try { source = await RestoreEntryAsync(ctx, entry, backup!, step.Item, ct).ConfigureAwait(false); }
            catch (QuarantineIntegrityException ex) { return UndoLeftAlone(step, ex.Message); }
        }
        else if (FsOps.Stat(backup!) is FileInfo) source = backup!;
        else return UndoLeftAlone(step, entry?.Purged == true
            ? "Its backup was deleted for good from quarantine, so the original can't be put back."
            : "Its backup is gone, so the original can't be put back.");

        var sourceInfo = (FileInfo)FsOps.Stat(source)!;
        if (Get(step, UndoKeys.OriginalHash) is { } wantOriginal && VerifiedCopier.HashFile(source, ct) != wantOriginal)
        {
            await ctx.Quarantine.QuarantineAsync(source, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
            return UndoLeftAlone(step, "Its backup doesn't match the original any more, so it wasn't used.");
        }
        if (current is not null && expectedNow is null && !await StillWhatWeWroteAsync(target, source, step.Item, ct).ConfigureAwait(false))
        {
            await ctx.Quarantine.QuarantineAsync(source, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
            return UndoLeftAlone(step, $"{Capital(What(target))} changed after PC Tidy edited it, so it was left alone. The original is still in quarantine.");
        }

        // PC Tidy's own content (verified above, and reproducible from the plan) is replaced by the original.
        if (current is not null) FileBackups.Replace(source, target, backup: null, current.Attributes);
        else FsOps.Rename(source, target);
        FileBackups.ApplyMeta(target, step.UndoData, sourceInfo);
        return Undone(step, $"Put the original {What(target)} back.");
    }

    /// <summary>For a step recorded Done by crash recovery (no hash journaled): recompute what PC Tidy wrote from the backup.</summary>
    private async Task<bool> StillWhatWeWroteAsync(string target, string backup, PlanItem item, CancellationToken ct)
    {
        var original = await File.ReadAllBytesAsync(LongPath.Io(backup), ct).ConfigureAwait(false);
        return Transform(original, item).Bytes is { } wrote && FileBackups.Hash(wrote) == VerifiedCopier.HashFile(target, ct);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ crash recovery

    public override async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var temp = Get(step, UndoKeys.TempPath);
        if (temp is null) return false;
        var target = Target(step.Item, ctx);
        if (!FileBackups.IsPlannedSibling(temp, target, FileBackups.NewSuffix)) return false;
        if (Get(step, UndoKeys.Mode) == ModeCreate)
        {
            FsOps.DeleteTree(temp); // our own unfinished temp file (it's gone once renamed into place)
            return FsOps.Stat(target) is FileInfo && VerifiedCopier.HashFile(target, ct) == Get(step, UndoKeys.Hash);
        }

        var backup = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(backup, target, FileBackups.BackupSuffix)) return false;
        QuarantineEntry? planned = null;
        if (Get(step, UndoKeys.QuarantineId) is { } id && ctx.Quarantine is QuarantineStore store)
            planned = await store.RecoverAsync(ctx.RunId, id, backup!, ct).ConfigureAwait(false);
        var backupBeside = FsOps.Stat(backup!) is FileInfo;
        if (planned is null && !backupBeside)
        {
            FsOps.DeleteTree(temp); // the swap never happened: only our temp file is left
            return false;
        }
        if (planned is null && !FsOps.Exists(target))
        {
            FsOps.Rename(backup!, target); // the swap stopped half-way: the original goes back to its name
            FsOps.DeleteTree(temp);
            return false;
        }
        FsOps.DeleteTree(temp);
        if (planned is null)
        {
            // The swap happened but the backup never reached quarantine: it goes where the intent said.
            try { await QuarantineAsync(ctx, backup!, Get(step, UndoKeys.QuarantineId), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* undo finds it beside the file */ }
        }
        return true;
    }
}
