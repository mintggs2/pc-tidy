using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine.Handlers;

/// <summary>
/// Deletes a browser profile's cookies except those of the sites to keep. Args: <c>browser</c> (chrome | edge | brave |
/// vivaldi | opera | chromium | firefox), <c>profileDir</c>, <c>keepHosts</c> ("\n"-separated; a host keeps its subdomains,
/// leading dots ignored; empty = delete all cookies).
/// <para>
/// Only the cookie database is ever touched (Chromium <c>&lt;profile&gt;\Network\Cookies</c>, Firefox
/// <c>&lt;profile&gt;\cookies.sqlite</c>) with its own <c>-journal</c>/<c>-wal</c>/<c>-shm</c> files. The browser must be
/// closed (its "close first" apps, and the database must not be open). The database is never edited in place:
/// a verified copy is edited (integrity-checked before and after), then swapped in with ReplaceFile; the original
/// database file object and its companion files go into a planned backup folder beside it ("Cookies.pctidy-backup"),
/// which is then quarantined under the id planned in the intent. Undo moves the current database files to quarantine
/// (so cookies the browser saved since are never lost) and puts the exact original files back.
/// </para>
/// UndoData: backup folder, quarantine id, the backed-up files (suffix → hash), the new database's hash, count.
/// </summary>
public sealed class CookiesDeleteExceptHandler : FileHandlerBase, IPreparingHandler
{
    private static readonly string[] Companions = { "-journal", "-wal", "-shm" };
    private const string ModeEdit = "edit";
    private const string ModeUnchanged = "unchanged";
    private const string BadPlan = "PC Tidy's plan for this step doesn't look right, so nothing was changed.";

    public CookiesDeleteExceptHandler() : base(null) { }

    public override bool CanHandle(PlanItem item) => item.Kind == ActionKind.CookiesDeleteExcept;

    private static string Arg(PlanItem item, string key) => item.Args is { } a && a.TryGetValue(key, out var v) ? v : "";

    private static bool IsFirefox(PlanItem item) => Arg(item, "browser").Trim().Equals("firefox", StringComparison.OrdinalIgnoreCase);

    /// <summary>The cookie database file of the profile (display form, literal).</summary>
    internal static string Database(PlanItem item)
    {
        var profile = Require(Arg(item, "profileDir"), "browser profile folder");
        return IsFirefox(item) ? Path.Combine(profile, "cookies.sqlite") : Path.Combine(profile, "Network", "Cookies");
    }

    /// <summary>The browser's own process names, plus the plan's "close first" apps.</summary>
    private static IEnumerable<string> BrowserApps(PlanItem item)
    {
        var own = Arg(item, "browser").Trim().ToLowerInvariant() switch
        {
            "chrome" or "chromium" => "chrome",
            "edge" => "msedge",
            "brave" => "brave",
            "vivaldi" => "vivaldi",
            "opera" => "opera",
            "firefox" => "firefox",
            _ => null,
        };
        return (item.RequiresClosedApps ?? Array.Empty<string>()).Concat(own is null ? Array.Empty<string>() : new[] { own });
    }

    private static string BrowserName(PlanItem item) => Arg(item, "browser") is { Length: > 0 } b ? char.ToUpperInvariant(b[0]) + b[1..] : "the browser";

    public StepRecord Prepare(StepRecord step, HandlerContext ctx)
    {
        var db = Database(step.Item);
        var d = new Dictionary<string, string>
        {
            [UndoKeys.TempPath] = FileBackups.PlanSibling(db, FileBackups.NewSuffix),
            [UndoKeys.BackupPath] = FileBackups.PlanSibling(db, FileBackups.BackupSuffix),
        };
        if (ctx.Quarantine is QuarantineStore) d[UndoKeys.QuarantineId] = QuarantineStore.NewEntryId();
        return step with { UndoData = Merge(step.UndoData, d) };
    }

    // ------------------------------------------------------------------ execute

    public override async Task<StepRecord> ExecuteAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.TempPath) is null) step = Prepare(step, ctx);
        var db = Database(step.Item);
        var temp = Get(step, UndoKeys.TempPath);
        var backupDir = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(temp, db, FileBackups.NewSuffix) || !FileBackups.IsPlannedSibling(backupDir, db, FileBackups.BackupSuffix))
            return Skipped(step, BadPlan);
        if (new[] { temp!, backupDir! }.Concat(Companions.Select(s => temp + s)).FirstOrDefault(FsOps.Exists) is { } taken)
            return Skipped(step, $"Something new called '{Name(taken)}' appeared just now, so nothing was changed.");
        if (Problem(step.Item, db) is { } problem) return Skipped(step, problem);

        // What's backed up: the database and whichever of its own companion files exist, each with its hash.
        var files = new Dictionary<string, string> { [""] = VerifiedCopier.HashFile(db, ct) };
        foreach (var s in Companions.Where(s => FsOps.Stat(db + s) is FileInfo)) files[s] = VerifiedCopier.HashFile(db + s, ct);
        var dbInfo = (FileInfo)FsOps.Stat(db)!;

        // Edit a verified copy (with its write-ahead log, so committed cookies in it count), never the browser's file.
        CookieDatabase.Result result;
        try
        {
            await VerifiedCopier.CopyFileAsync(db, temp!, ct).ConfigureAwait(false);
            if (FsOps.Stat(db + "-wal") is FileInfo { Length: > 0 }) await VerifiedCopier.CopyFileAsync(db + "-wal", temp + "-wal", ct).ConfigureAwait(false);
            result = await Task.Run(() => CookieDatabase.DeleteExcept(temp!, IsFirefox(step.Item), CookieHosts.Parse(Arg(step.Item, "keepHosts"))), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteTemp(temp!);
            return Failed(step, "The cookie database couldn't be read, so nothing was changed: " + Friendly(ex));
        }
        catch (OperationCanceledException)
        {
            DeleteTemp(temp!);
            throw;
        }
        foreach (var s in Companions) FsOps.DeleteTree(temp + s); // our copy's own leftovers (normally none after closing)
        if (result.Deleted == 0)
        {
            DeleteTemp(temp!);
            return Done(step, "Nothing to remove: every cookie belongs to a site you keep.", new() { [UndoKeys.Mode] = ModeUnchanged });
        }

        // Swap: companions into the backup folder first (stale ones must never meet the new database), then ReplaceFile
        // moves the original database file object into the backup folder and the edited copy into its place.
        var dbName = Name(db);
        var moved = new List<string>();
        try
        {
            Directory.CreateDirectory(LongPath.Io(backupDir!));
            foreach (var s in files.Keys.Where(k => k.Length > 0))
            {
                FsOps.Rename(db + s, Path.Combine(backupDir!, dbName + s));
                moved.Add(s);
            }
            FileBackups.Replace(temp!, db, Path.Combine(backupDir!, dbName), dbInfo.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var s in moved) FsOps.Rename(Path.Combine(backupDir!, dbName + s), db + s);
            FsOps.RemoveEmptyDirectories(new[] { backupDir! });
            DeleteTemp(temp!);
            return Failed(step, Friendly(ex) + " Nothing was changed.");
        }

        var undo = new Dictionary<string, string>
        {
            [UndoKeys.Mode] = ModeEdit,
            [UndoKeys.Files] = JsonSerializer.Serialize(files),
            [UndoKeys.Hash] = VerifiedCopier.HashFile(db, CancellationToken.None),
            [UndoKeys.Count] = Invariant(result.Deleted),
            [UndoKeys.BackupInQuarantine] = "0",
        };
        var where = $" The old cookies are kept next to it in '{Name(backupDir!)}'.";
        try
        {
            var entry = await QuarantineAsync(ctx, backupDir!, Get(step, UndoKeys.QuarantineId), CancellationToken.None).ConfigureAwait(false);
            undo[UndoKeys.QuarantineId] = entry.Id;
            undo[UndoKeys.BackupInQuarantine] = "1";
            where = " The old cookies are saved in quarantine; Undo puts them back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept beside the database */ }
        return Done(step, $"Removed {result.Deleted} cookie(s) and kept {result.Kept} from the sites you chose.{where}", undo);
    }

    /// <summary>Why the database can't be changed right now (browser open, missing, link, unclean shutdown), or null.</summary>
    private static string? Problem(PlanItem item, string db)
    {
        if (FileBackups.RunningApps(BrowserApps(item)) is { Count: > 0 } open) return $"Close {string.Join(", ", open)} first.";
        var info = FsOps.Stat(db);
        if (info is null) return "This browser profile has no cookie database, so there was nothing to do.";
        if (info is not FileInfo || FsOps.IsReparsePoint(info) || CloudFile.IsPlaceholder(info.Attributes))
            return "The cookie database isn't a plain file, so PC Tidy left it alone.";
        foreach (var s in Companions)
            if (FsOps.Stat(db + s) is { } c && (c is not FileInfo || FsOps.IsReparsePoint(c))) return "The cookie database isn't a plain file, so PC Tidy left it alone.";
        if (FsOps.Stat(db + "-journal") is FileInfo { Length: > 0 })
            return $"{BrowserName(item)} didn't close properly last time. Open and close it once, then try again.";
        try
        {
            // Also proves no link sits on the way (the handle's real location must be exactly this path).
            using var _ = HandleFs.OpenVerified(db, NativeMethods.GenericRead, share: 0);
        }
        catch (SafetyRefusalException ex) { return ex.Message; }
        catch (IOException) { return $"Close {BrowserName(item)} first: it's using its cookies."; }
        return null;
    }

    private static void DeleteTemp(string temp)
    {
        FsOps.DeleteTree(temp);
        foreach (var s in Companions) FsOps.DeleteTree(temp + s);
    }

    // ------------------------------------------------------------------ simulate

    public override Task<StepRecord> SimulateAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var db = Database(step.Item);
        if (Problem(step.Item, db) is { } problem) return Task.FromResult(Skipped(step, problem));
        var keep = CookieHosts.Parse(Arg(step.Item, "keepHosts"));
        return Task.FromResult(Simulated(step, keep.Count == 0
            ? $"Would delete all of {BrowserName(step.Item)}'s cookies (you'd be signed out of websites). The old cookies would be saved in quarantine first."
            : $"Would delete {BrowserName(step.Item)}'s cookies except those of {keep.Count} site(s) you keep. The old cookies would be saved in quarantine first."));
    }

    // ------------------------------------------------------------------ undo

    public override async Task<StepRecord> UndoAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        if (Get(step, UndoKeys.Mode) == ModeUnchanged) return Undone(step, "Nothing had changed.");
        var db = Database(step.Item);
        var backupDir = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(backupDir, db, FileBackups.BackupSuffix)) return UndoLeftAlone(step, BadPlan);
        if (FileBackups.RunningApps(BrowserApps(step.Item)) is { Count: > 0 } open)
            return UndoFailed(step, $"Close {string.Join(", ", open)} first, then undo again.");
        if (FsOps.Stat(db) is FileInfo && ctx.InUse.IsLocked(db)) return UndoFailed(step, $"Close {BrowserName(step.Item)} first, then undo again.");

        // The backup folder: from quarantine (to its planned place only), or still beside the database.
        string folder;
        var entry = await FindEntryAsync(ctx, Get(step, UndoKeys.QuarantineId), backupDir, ct).ConfigureAwait(false);
        if (entry is { Restored: false, Purged: false })
        {
            try { folder = await RestoreEntryAsync(ctx, entry, backupDir!, step.Item, ct).ConfigureAwait(false); }
            catch (QuarantineIntegrityException ex) { return UndoLeftAlone(step, ex.Message); }
        }
        else if (FsOps.IsDirectory(backupDir!)) folder = backupDir!;
        else return UndoLeftAlone(step, entry?.Purged == true
            ? "The old cookies were deleted for good from quarantine, so they can't be put back."
            : "The old cookies are gone, so they can't be put back.");

        var dbName = Name(db);
        var files = Get(step, UndoKeys.Files) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : null;
        files ??= new Dictionary<string, string>(Directory.EnumerateFiles(LongPath.Io(folder))
            .Select(f => Path.GetFileName(f)).Where(n => n.StartsWith(dbName, StringComparison.OrdinalIgnoreCase))
            .Select(n => KeyValuePair.Create(n[dbName.Length..], "")));
        foreach (var (suffix, hash) in files)
        {
            var f = Path.Combine(folder, dbName + suffix);
            if (FsOps.Stat(f) is not FileInfo || (hash.Length > 0 && VerifiedCopier.HashFile(f, ct) != hash))
            {
                await ctx.Quarantine.QuarantineAsync(folder, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
                return UndoLeftAlone(step, "The saved cookies don't match what PC Tidy stored, so they weren't put back.");
            }
        }

        // The current database files go to quarantine first (anything the browser saved since is kept, restorable).
        var changedSince = FsOps.Stat(db) is FileInfo && VerifiedCopier.HashFile(db, ct) != Get(step, UndoKeys.Hash)
                           || Companions.Any(s => FsOps.Exists(db + s));
        var parked = new List<(string Path, QuarantineEntry Entry)>();
        try
        {
            foreach (var p in Companions.Select(s => db + s).Append(db).Where(FsOps.Exists))
                parked.Add((p, await ctx.Quarantine.QuarantineAsync(p, ctx.RunId, CancellationToken.None).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (p, e) in parked) await RestoreEntryAsync(ctx, e, p, step.Item, CancellationToken.None).ConfigureAwait(false);
            await ctx.Quarantine.QuarantineAsync(folder, ctx.RunId, CancellationToken.None).ConfigureAwait(false);
            return UndoFailed(step, Friendly(ex) + " Nothing was changed; try Undo again.");
        }

        foreach (var suffix in files.Keys.OrderByDescending(s => s.Length)) // companions first, the database last
            FsOps.Rename(Path.Combine(folder, dbName + suffix), db + suffix);
        FsOps.RemoveEmptyDirectories(new[] { folder });
        return Undone(step, changedSince
            ? "Put your old cookies back. The cookies saved since then are in quarantine."
            : "Put your old cookies back.");
    }

    // ------------------------------------------------------------------ crash recovery

    /// <summary>Done ⇔ the original database reached the backup folder (beside it or in quarantine). A half-made swap is undone.</summary>
    public override async Task<bool> WasCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var db = Database(step.Item);
        var temp = Get(step, UndoKeys.TempPath);
        var backupDir = Get(step, UndoKeys.BackupPath);
        if (!FileBackups.IsPlannedSibling(temp, db, FileBackups.NewSuffix) || !FileBackups.IsPlannedSibling(backupDir, db, FileBackups.BackupSuffix))
            return false;
        DeleteTemp(temp!);
        if (Get(step, UndoKeys.QuarantineId) is { } id && ctx.Quarantine is QuarantineStore store
            && await store.RecoverAsync(ctx.RunId, id, backupDir!, ct).ConfigureAwait(false) is not null)
            return true;
        if (!FsOps.IsDirectory(backupDir!)) return false;
        var dbName = Name(db);
        if (FsOps.Stat(Path.Combine(backupDir!, dbName)) is FileInfo)
        {
            try { await QuarantineAsync(ctx, backupDir!, Get(step, UndoKeys.QuarantineId), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* undo finds it beside the database */ }
            return true;
        }
        // Stopped between moving the companions and the swap: they go back next to the untouched database.
        foreach (var s in Companions)
        {
            var parked = Path.Combine(backupDir!, dbName + s);
            if (FsOps.Exists(parked) && !FsOps.Exists(db + s)) FsOps.Rename(parked, db + s);
        }
        FsOps.RemoveEmptyDirectories(new[] { backupDir! });
        return false;
    }
}
