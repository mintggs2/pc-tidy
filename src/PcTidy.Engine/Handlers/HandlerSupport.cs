using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Engine handlers decide their exact targets (free name, temp name, quarantine entry, folders to create) BEFORE the
/// "Started" record is written, so that after a crash Resume/Undo know precisely what was intended. At execution they
/// use exactly those targets; if one was taken in the meantime the step is skipped rather than re-planned unjournaled.
/// </summary>
internal interface IPreparingHandler
{
    StepRecord Prepare(StepRecord step, HandlerContext ctx);
}

/// <summary>Keys used in <see cref="StepRecord.UndoData"/> by the engine's file handlers.</summary>
internal static class UndoKeys
{
    public const string QuarantineId = "quarantineId";
    public const string FinalPath = "finalPath";
    public const string TempPath = "tempPath";
    public const string CreatedDirs = "createdDirs";
    public const string Size = "size";
    public const string LastWriteTicks = "lastWriteTicks";
    public const string IsDirectory = "isDirectory";
    public const string Hash = "hash";
    public const string ManifestHash = "manifestHash";
    public const string OverwrittenQuarantineId = "overwrittenQuarantineId";
    public const string ZipPath = "zipPath";
    public const string OldPath = "oldPath";
    public const string Mode = "mode";
    public const string SkipReason = "skipReason";
    public const string Partial = "partial";
    // v2: edits with a backup (EditTextFile, HostsCommentOut, CookiesDeleteExcept) and shredding.
    public const string BackupPath = "backupPath";
    /// <summary>"1" when the backup reached quarantine; "0" when it stayed next to the file (its planned sibling name).</summary>
    public const string BackupInQuarantine = "backupInQuarantine";
    public const string OriginalHash = "originalHash";
    public const string CreationTicks = "creationTicks";
    public const string Attributes = "attributes";
    /// <summary>Encoding/line breaks the edited text file was written in (informational).</summary>
    public const string TextFormat = "textFormat";
    /// <summary>JSON: the cookie database files backed up (suffix → hash), and how many cookies went.</summary>
    public const string Files = "files";
    public const string Count = "count";
    /// <summary>ShredFile: hdd | ssd | unknown (shredding is best-effort on anything but a hard drive).</summary>
    public const string Media = "media";
    public const string ShredName = "shredName";
    /// <summary>Set on an UndoFailed step when undo deliberately left things alone (counted as "skipped").</summary>
    public const string UndoOutcome = "undoOutcome";
}

/// <summary>Shared options for engine file handlers.</summary>
internal sealed class TransferOptions
{
    public static readonly TransferOptions Default = new();
    /// <summary>Tests: treat every move as cross-volume (copy → verify → remove).</summary>
    public bool ForceCopy { get; init; }
}

/// <summary>
/// Names planned for the steps of one intent batch. Several steps of a batch are prepared before any of them runs, so
/// "next free name" must also skip names already promised to an earlier step of the same batch.
/// </summary>
internal static class PlannedTargets
{
    private sealed class State
    {
        public readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Existing names that the rename group being prepared moves away first (so they count as free).</summary>
        public HashSet<string> Vacating = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>Starts a planning scope (the executor's prepare phase).</summary>
    public static IDisposable Begin()
    {
        var previous = Current.Value;
        Current.Value = new State();
        return new Scope(previous);
    }

    /// <summary>The sources of the consecutive rename group about to be prepared: phase A parks them all first.</summary>
    public static void SetVacating(IEnumerable<string> sources)
    {
        if (Current.Value is { } s) s.Vacating = new HashSet<string>(sources.Select(Key), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"name.ext", then "name (2).ext"... skipping what exists and what this batch already planned.</summary>
    public static string FreeName(string desired)
    {
        var state = Current.Value;
        bool Taken(string p)
        {
            var k = Key(p);
            if (state?.Reserved.Contains(k) == true) return true;
            return FsOps.Exists(p) && state?.Vacating.Contains(k) != true;
        }
        var name = desired;
        if (Taken(name))
        {
            var display = Key(desired);
            var dir = Path.GetDirectoryName(display) ?? throw new IOException("Path has no folder: " + display);
            var stem = Path.GetFileNameWithoutExtension(display);
            var ext = Path.GetExtension(display);
            name = "";
            for (int i = 2; i < 100_000 && name.Length == 0; i++)
            {
                var c = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!Taken(c)) name = c;
            }
            if (name.Length == 0) throw new IOException("No free name for " + display);
        }
        state?.Reserved.Add(Key(name));
        return name;
    }

    private static string Key(string p) => LongPath.Display(LongPath.Io(p));

    private sealed class Scope(State? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Base for the engine's file handlers: result helpers and common checks.</summary>
public abstract class FileHandlerBase : IActionHandler
{
    internal FileHandlerBase(TransferOptions? options) => Options = options ?? TransferOptions.Default;

    internal TransferOptions Options { get; }

    public abstract bool CanHandle(PlanItem item);
    public abstract Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    public abstract Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    public abstract Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);
    public abstract Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct);

    protected static StepRecord Done(StepRecord s, string message, Dictionary<string, string>? undo, long bytes = 0)
        => s with { Status = StepStatus.Done, Message = message, UndoData = Merge(s.UndoData, undo), BytesAffected = bytes, FinishedUtc = DateTime.UtcNow };

    protected static StepRecord Skipped(StepRecord s, string message)
        => s with { Status = StepStatus.Skipped, Message = message, FinishedUtc = DateTime.UtcNow };

    protected static StepRecord Failed(StepRecord s, string message, Dictionary<string, string>? undo = null)
        => s with { Status = StepStatus.Failed, Message = message, UndoData = Merge(s.UndoData, undo), FinishedUtc = DateTime.UtcNow };

    protected static StepRecord Simulated(StepRecord s, string message, long bytes = 0)
        => s with { Status = StepStatus.Simulated, Message = message, BytesAffected = bytes, FinishedUtc = DateTime.UtcNow };

    protected static StepRecord Undone(StepRecord s, string message)
        => s with { Status = StepStatus.Undone, Message = message, FinishedUtc = DateTime.UtcNow };

    protected static StepRecord UndoFailed(StepRecord s, string message)
        => s with { Status = StepStatus.UndoFailed, Message = message, FinishedUtc = DateTime.UtcNow };

    /// <summary>Undo deliberately left it alone (changed since, missing...). Counted as skipped in the undo report.</summary>
    protected static StepRecord UndoLeftAlone(StepRecord s, string message)
        => s with
        {
            Status = StepStatus.UndoFailed, Message = message, FinishedUtc = DateTime.UtcNow,
            UndoData = Merge(s.UndoData, new() { [UndoKeys.UndoOutcome] = "skipped" }),
        };

    internal static Dictionary<string, string>? Merge(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (a is null) return b is null ? null : new Dictionary<string, string>(b);
        var r = new Dictionary<string, string>(a);
        if (b is not null) foreach (var (k, v) in b) r[k] = v;
        return r;
    }

    protected static string? Get(StepRecord s, string key) => s.UndoData is { } d && d.TryGetValue(key, out var v) ? v : null;

    protected static string Name(string path) => Path.GetFileName(LongPath.Display(LongPath.Io(path)));

    protected static string Require(string? path, string what)
        => string.IsNullOrWhiteSpace(path) ? throw new ArgumentException($"The plan item has no {what}.") : LongPath.Display(LongPath.Io(path));

    protected static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Plain-English text for an unexpected error.</summary>
    internal static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows denied access to it.",
        FileNotFoundException or DirectoryNotFoundException => "It's no longer there.",
        PathTooLongException => "Its path is too long for this drive.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "It's in use by another app right now.",
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "There isn't enough free space.",
        _ => ex.Message,
    };

    /// <summary>Records identity (size, modified time) so Undo can check it's still the same thing.</summary>
    internal static Dictionary<string, string> Identity(string path, TreeManifest manifest, string? fileHash = null)
    {
        var d = new Dictionary<string, string>
        {
            [UndoKeys.IsDirectory] = manifest.IsDirectory ? "1" : "0",
            [UndoKeys.Size] = Invariant(manifest.TotalBytes),
        };
        if (manifest.IsDirectory) d[UndoKeys.ManifestHash] = manifest.MetadataHash;
        else d[UndoKeys.LastWriteTicks] = Invariant(manifest.Entries[0].LastWriteTicks);
        if (fileHash is not null) d[UndoKeys.Hash] = fileHash;
        return d;
    }

    /// <summary>Is the thing at <paramref name="path"/> still what this step recorded? (size + modified time, + hash if recorded).</summary>
    internal static bool MatchesIdentity(StepRecord s, string path, CancellationToken ct)
    {
        if (!FsOps.Exists(path)) return false;
        if (Get(s, UndoKeys.IsDirectory) is null) return true; // resumed step: only the planned path is known
        var m = TreeManifest.Build(path, ct);
        var wantDir = Get(s, UndoKeys.IsDirectory) == "1";
        if (m.IsDirectory != wantDir) return false;
        if (Get(s, UndoKeys.Size) is { } size && size != Invariant(m.TotalBytes)) return false;
        if (wantDir) return Get(s, UndoKeys.ManifestHash) is not { } mh || mh == m.MetadataHash;
        if (Get(s, UndoKeys.LastWriteTicks) is { } lw && lw != Invariant(m.Entries[0].LastWriteTicks)) return false;
        if (Get(s, UndoKeys.Hash) is { } h && VerifiedCopier.HashFile(path, ct) != h) return false;
        return true;
    }

    /// <summary>
    /// Clash handling for a planned destination. Returns the final path, or null with a reason when it can't be used.
    /// AutoNumber: the planned free name must still be free (it was journaled before the step started).
    /// Overwrite never deletes: the existing item is quarantined first (restorable, and undo puts it back).
    /// </summary>
    internal static async Task<(string? Final, string? OverwrittenId, string? SkipReason)> ResolveClashAsync(
        string source, string destination, NameClashPolicy policy, HandlerContext ctx, CancellationToken ct)
    {
        if (!FsOps.Exists(destination) || (source.Length > 0 && LongPath.Same(source, destination)))
            return (destination, null, null);
        switch (policy)
        {
            case NameClashPolicy.Overwrite:
                var entry = await ctx.Quarantine.QuarantineAsync(destination, ctx.RunId, ct).ConfigureAwait(false);
                return (destination, entry.Id, null);
            case NameClashPolicy.Skip:
                return (null, null, $"Something called '{Name(destination)}' is already there.");
            default:
                return (null, null, $"Something new called '{Name(destination)}' appeared there just now, so it was left alone.");
        }
    }

    /// <summary>Planned final path (in Prepare): AutoNumber picks a free name that no other step of the batch was promised.</summary>
    internal static string PlanFinal(string source, string destination, NameClashPolicy policy)
        => policy == NameClashPolicy.AutoNumber && !(source.Length > 0 && LongPath.Same(source, destination))
            ? PlannedTargets.FreeName(destination) : destination;

    /// <summary>Quarantines with the entry id planned in the step's intent when the store supports it.</summary>
    internal static Task<QuarantineEntry> QuarantineAsync(HandlerContext ctx, string path, string? plannedId, CancellationToken ct)
        => ctx.Quarantine is QuarantineStore store
            ? store.QuarantineAsync(path, ctx.RunId, plannedId, ct)
            : ctx.Quarantine.QuarantineAsync(path, ctx.RunId, ct);

    internal static async Task<QuarantineEntry?> FindEntryAsync(HandlerContext ctx, string? id, string? originalPath, CancellationToken ct)
    {
        // By id: read just that entry (listing the whole run per step made big undos quadratic).
        if (id is not null && ctx.Quarantine is QuarantineStore store
            && await store.FindAsync(ctx.RunId, id, ct).ConfigureAwait(false) is { } direct)
            return direct;
        var entries = await ctx.Quarantine.ListAsync(ctx.RunId, ct).ConfigureAwait(false);
        if (id is not null && entries.FirstOrDefault(e => e.Id == id) is { } byId) return byId;
        return originalPath is null ? null
            : entries.Where(e => !e.Restored && !e.Purged && LongPath.Same(e.OriginalPath, originalPath)).FirstOrDefault();
    }

    /// <summary>
    /// Puts a quarantined item back to <paramref name="expectedOriginal"/> (the place the journaled plan says it came
    /// from) and nowhere else: the entry's own record must agree, and the place is re-checked by the safety guard.
    /// </summary>
    internal static async Task<string> RestoreEntryAsync(HandlerContext ctx, QuarantineEntry entry, string expectedOriginal, PlanItem forSafety, CancellationToken ct)
    {
        if (!LongPath.Same(entry.OriginalPath, expectedOriginal))
            throw new QuarantineIntegrityException("Its quarantine record doesn't match where it came from, so PC Tidy didn't put it back.");
        SafetyVerdict verdict;
        try { verdict = ctx.Safety.Check(forSafety); }
        catch (Exception) { verdict = SafetyVerdict.Block("The safety check couldn't run."); }
        if (!verdict.Allowed)
            throw new QuarantineIntegrityException("PC Tidy's safety rules don't allow putting it back there: " + (verdict.Reason ?? "it's protected."));
        return ctx.Quarantine is QuarantineStore store
            ? await store.RestoreToAsync(entry, expectedOriginal, ct).ConfigureAwait(false)
            : await ctx.Quarantine.RestoreAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>Restores an item this step quarantined because it was in the way at <paramref name="place"/> (an overwritten destination).</summary>
    internal static async Task<string?> RestoreOverwrittenAsync(HandlerContext ctx, string? id, string place, PlanItem forSafety, CancellationToken ct)
    {
        if (id is null) return null;
        var e = await FindEntryAsync(ctx, id, null, ct).ConfigureAwait(false);
        if (e is null || e.Restored || e.Purged) return null;
        return await RestoreEntryAsync(ctx, e, place, forSafety, ct).ConfigureAwait(false);
    }

    protected static string Describe(string path) => $"'{Name(path)}'";
}
