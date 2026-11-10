using System.Collections.Concurrent;
using System.Security.Principal;
using System.Text.Json;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// Restorable "recycle bin" for everything PC Tidy removes.
/// Layout: <c>&lt;store&gt;\&lt;runId&gt;\&lt;entryId&gt;\payload\&lt;original name&gt;</c> + <c>entry.json</c>.
/// The store is <c>&lt;DataRoot&gt;\Quarantine</c>, or (optional, for speed) a hidden <c>&lt;drive&gt;\PC Tidy Quarantine</c>
/// on the file's own fixed drive so the move is an instant rename (used only while that folder is private to this user).
/// Same volume → atomic rename. Other volume → copy, verify (size + XXH3 of every file), then remove the verified
/// originals; anything changed or in use stays at the original place and the caller is told (never lose data).
/// <para>
/// Restores go back ONLY to the place the caller says the item came from (the journaled plan), and only if the entry's
/// own record agrees, so an edited <c>entry.json</c> can't redirect a restore.
/// </para>
/// <para>
/// The elevated helper uses an admin store (<see cref="CreateAdminStore"/>) inside <see cref="AdminDataFolder"/>: no
/// per-drive folders, every move is by handle (links refused, real location confirmed), same volume only, and the
/// folder is re-verified before every operation.
/// </para>
/// </summary>
public sealed class QuarantineStore : IQuarantineStore
{
    public const string VolumeFolderName = "PC Tidy Quarantine";
    private const string ManifestName = "entry.json";
    private const string PayloadName = "payload";
    private static readonly TimeSpan TrustCacheLifetime = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(AppSettings.Json);

    private readonly string _dataRoot;
    private readonly IKnownLocations _locations;
    private readonly AppSettings _settings;
    private readonly Action? _verifyAdminFolder;
    private readonly ConcurrentDictionary<string, (DateTime CheckedUtc, string? Problem)> _volumeTrust = new(StringComparer.OrdinalIgnoreCase);

    public QuarantineStore(string dataRoot, IKnownLocations locations, AppSettings settings)
        : this(dataRoot, locations, settings, verifyAdminFolder: null) { }

    private QuarantineStore(string dataRoot, IKnownLocations locations, AppSettings settings, Action? verifyAdminFolder)
    {
        _dataRoot = dataRoot;
        _locations = locations;
        _settings = settings;
        _verifyAdminFolder = verifyAdminFolder;
    }

    /// <summary>The elevated helper's quarantine, inside its admin-only folder.</summary>
    public static QuarantineStore CreateAdminStore(AdminDataFolder folder, IKnownLocations locations, AppSettings settings)
        => new(folder.Root, locations, settings, folder.Verify);

    /// <summary>Tests: an admin-mode store whose folder check is supplied by the test.</summary>
    internal static QuarantineStore CreateAdminStore(string root, IKnownLocations locations, AppSettings settings, Action verify)
        => new(root, locations, settings, verify);

    /// <summary>True for the elevated helper's store (handle-based moves, no per-drive folders, restores only by undo).</summary>
    public bool IsAdminStore => _verifyAdminFolder is not null;

    /// <summary>Tests: behave as if the data folder were on another drive (forces copy → verify → remove).</summary>
    internal bool ForceCopyIntoDataRoot { get; init; }

    public string DataStoreRoot => DataPaths.Quarantine(_dataRoot);

    /// <summary>Why the last per-drive quarantine folder was passed over (also written to the engine log).</summary>
    internal string? LastVolumeStoreProblem { get; private set; }

    // ------------------------------------------------------------------ quarantine

    public Task<QuarantineEntry> QuarantineAsync(string path, string runId, CancellationToken ct)
        => QuarantineAsync(path, runId, plannedId: null, ct);

    /// <summary>
    /// <paramref name="plannedId"/>: the entry id the executor chose and journaled durably (write-ahead intent) before
    /// calling. The journal then says where the item went, so the entry's own manifest needs no forced disk flush.
    /// Without it (undo, clash handling), manifests are flushed to disk around the move.
    /// </summary>
    internal async Task<QuarantineEntry> QuarantineAsync(string path, string runId, string? plannedId, CancellationToken ct)
    {
        RunIds.Validate(runId);
        VerifyAdminFolder();
        var display = LongPath.Display(LongPath.Io(path));
        var info = FsOps.Stat(display) ?? throw new FileNotFoundException("It's no longer there.", display);
        if (Path.GetDirectoryName(display) is null) throw new SafetyRefusalException("PC Tidy never quarantines a whole drive.");
        if (CloudFile.IsPlaceholder(info.Attributes))
            throw new SafetyRefusalException("It's an online-only OneDrive file. PC Tidy never downloads it, so it was left alone.");
        if (AllStoreRoots().Any(r => LongPath.IsUnder(display, r)))
            throw new SafetyRefusalException("It's already in quarantine.");

        var durable = plannedId is null;
        var id = IsValidEntryId(plannedId) ? plannedId! : NewEntryId();
        // Folders are described up front (online-only files or links inside stop it); a file's own attributes suffice.
        TreeManifest? manifest = null;
        if (info is DirectoryInfo)
        {
            using (EngineTimings.Measure("q.manifest")) manifest = TreeManifest.Build(display, ct);
            if (manifest.CloudPlaceholders.Count > 0) throw new SafetyRefusalException(manifest.CopyProblem!);
        }
        ct.ThrowIfCancellationRequested();

        if (IsAdminStore)
            return RenameInto(DataStoreRoot, display, info, runId, id, durable, mustSucceed: true, byHandle: true)
                   ?? throw new SafetyRefusalException("It's on a different drive than PC Tidy's admin quarantine, so it was left alone.");

        if (VolumeStoreFor(display) is { } volumeStore
            && RenameInto(volumeStore, display, info, runId, id, durable, mustSucceed: false, byHandle: false) is { } fast)
            return fast;

        if (!ForceCopyIntoDataRoot && RenameInto(DataStoreRoot, display, info, runId, id, durable, mustSucceed: true, byHandle: false) is { } renamed)
            return renamed;

        manifest ??= TreeManifest.Build(display, ct);
        return await CopyIntoAsync(DataStoreRoot, display, info, manifest, runId, id, ct).ConfigureAwait(false);
    }

    /// <summary>Atomic rename into a store. Returns null when the store is on another volume (or unusable and optional).</summary>
    private QuarantineEntry? RenameInto(string storeRoot, string display, FileSystemInfo info, string runId, string id,
        bool durable, bool mustSucceed, bool byHandle)
    {
        var (entryDir, stored) = EntryPaths(storeRoot, runId, id, display);
        if (FsOps.Exists(entryDir)) throw new IOException("That quarantine entry already exists.");
        try { Directory.CreateDirectory(LongPath.Io(Path.GetDirectoryName(stored)!)); }
        catch (Exception ex) when (!mustSucceed && ex is IOException or UnauthorizedAccessException) { return null; }

        var entry = NewEntry(id, runId, display, stored, info);
        // Without a durable journal intent, the manifest goes first so the payload is never an orphan after a crash.
        if (durable) WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Complete, Entry = entry }, flushToDisk: true);
        RenameOutcome outcome;
        try
        {
            using (EngineTimings.Measure("q.rename"))
                outcome = byHandle ? HandleFs.MoveVerified(display, stored) : FsOps.TryRename(display, stored);
        }
        catch (Exception ex) when (!mustSucceed && ex is IOException or UnauthorizedAccessException && ex is not SafetyRefusalException)
        {
            // Optional drive store refused (permissions?): fall back to the data folder, which reports the real problem.
            FsOps.DeleteTree(entryDir);
            return null;
        }
        catch
        {
            FsOps.DeleteTree(entryDir);
            throw;
        }
        if (outcome == RenameOutcome.NotSameVolume)
        {
            FsOps.DeleteTree(entryDir);
            return null;
        }

        // Describe the payload from the copy now in our hands (nothing can change it mid-way any more).
        QuarantineEntry final;
        using (EngineTimings.Measure("q.describe")) final = Describe(entry, stored, CancellationToken.None);
        using (EngineTimings.Measure("q.manifest-write"))
            WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Complete, Entry = final, SameVolume = true }, flushToDisk: durable);
        return final;
    }

    private async Task<QuarantineEntry> CopyIntoAsync(string storeRoot, string display, FileSystemInfo info, TreeManifest manifest, string runId, string id, CancellationToken ct)
    {
        if (manifest.CopyProblem is { } problem) throw new SafetyRefusalException(problem);
        var (entryDir, stored) = EntryPaths(storeRoot, runId, id, display);
        if (FsOps.Exists(entryDir)) throw new IOException("That quarantine entry already exists.");
        Directory.CreateDirectory(LongPath.Io(Path.GetDirectoryName(stored)!));
        var entry = NewEntry(id, runId, display, stored, info);
        // A copy is slow anyway, so its manifests are always flushed: they decide whether the original may go.
        WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Pending, Entry = entry }, flushToDisk: true);

        CopyResult copied;
        try { copied = await VerifiedCopier.CopyAsync(display, stored, ct).ConfigureAwait(false); }
        catch
        {
            FsOps.DeleteTree(entryDir); // pending entry + partial copy only; the original is untouched
            throw;
        }

        entry = entry with
        {
            SizeBytes = copied.Copy.TotalBytes,
            FileCount = copied.Copy.FileCount,
            // The copy's own manifest: its file times are as this volume stores them (FAT rounds them).
            Hash = copied.Copy.IsDirectory ? copied.Copy.MetadataHash : copied.FileHash,
            LastWriteUtc = copied.Copy.IsDirectory ? entry.LastWriteUtc : new DateTime(copied.Copy.Entries[0].LastWriteTicks, DateTimeKind.Utc),
        };
        WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Complete, Entry = entry }, flushToDisk: true);

        // Commit phase: the verified copy exists, so removing the original is not cancellable.
        var removal = VerifiedCopier.RemoveVerified(display, copied.Source);
        if (removal.Complete) return entry;
        if (removal.NothingRemoved)
        {
            // The original is completely intact: our copy is a duplicate, so it goes again.
            FsOps.DeleteTree(entryDir);
            throw new IOException(removal.Kept.Count > 0
                ? "It's in use by another app right now, so it was left alone."
                : "It changed after PC Tidy copied it, so it was left alone.");
        }
        var left = removal.LeftBehind.ToList();
        WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Partial, Entry = entry, LeftBehind = left }, flushToDisk: true);
        throw new PartialQuarantineException(entry, left, removal.Note());
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Quarantine page: puts an entry back where its record says it came from. Not available for the admin store,
    /// whose items go back only through Undo (the journaled plan says where).
    /// </summary>
    public Task<string> RestoreAsync(QuarantineEntry entry, CancellationToken ct) => RestoreAsync(entry, force: false, ct);

    /// <summary>"Restore anyway" in the Quarantine page skips the payload check (<paramref name="force"/>).</summary>
    public Task<string> RestoreAsync(QuarantineEntry entry, bool force, CancellationToken ct)
    {
        if (IsAdminStore) throw new InvalidOperationException("Items PC Tidy's admin helper quarantined go back only with Undo.");
        return Task.Run(async () =>
        {
            var man = ReadManifest(EntryDirOf(entry)) ?? throw new FileNotFoundException("This quarantine entry no longer exists.");
            return await RestoreCoreAsync(entry, man.Entry.OriginalPath, force, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Undo: puts an entry back to <paramref name="expectedOriginalPath"/> (from the journaled plan) and nowhere else.
    /// Refused if the entry's own record names a different place. Never overwrites: if the path is taken it restores as
    /// "name (restored).ext". Checks first that the quarantined copy is still exactly what was stored.
    /// </summary>
    internal Task<string> RestoreToAsync(QuarantineEntry entry, string expectedOriginalPath, CancellationToken ct)
        => Task.Run(() => RestoreCoreAsync(entry, expectedOriginalPath, force: false, ct), ct);

    private async Task<string> RestoreCoreAsync(QuarantineEntry entry, string expectedOriginalPath, bool force, CancellationToken ct)
    {
        VerifyAdminFolder();
        var entryDir = EntryDirOf(entry);
        var man = ReadManifest(entryDir) ?? throw new FileNotFoundException("This quarantine entry no longer exists.");
        var e = man.Entry;
        var expected = LongPath.Display(LongPath.Io(expectedOriginalPath));
        if (!LongPath.Same(e.OriginalPath, expected) || !LongPath.Same(e.StoredPath, Path.Combine(entryDir, PayloadName, Path.GetFileName(expected))))
            throw new QuarantineIntegrityException("Its quarantine record doesn't match where it came from, so PC Tidy didn't put it back.");
        if (e.Purged) throw new InvalidOperationException("It was deleted for good, so it can't be restored.");
        if (e.Restored) throw new InvalidOperationException($"It was already restored to {man.RestoredTo ?? expected}.");
        if (!FsOps.Exists(e.StoredPath)) throw new FileNotFoundException("The quarantined copy is missing.", e.StoredPath);
        if (!force) VerifyPayload(e, ct);

        var target = FsOps.FreeRestoreName(expected);
        var leftInQuarantine = new List<string>();
        if (IsAdminStore)
        {
            // The helper runs as admin: creating missing folders by path name could be redirected (a junction planted
            // on the way) into a place the user can't normally write. It only puts items back into a folder that is
            // still there; MoveVerified then opens that folder by handle and confirms its real location.
            if (FsOps.Stat(Path.GetDirectoryName(target) ?? "") is not DirectoryInfo)
                throw new IOException("The folder it came from is gone, so PC Tidy's admin helper didn't put it back.");
            if (HandleFs.MoveVerified(e.StoredPath, target) == RenameOutcome.NotSameVolume)
                throw new IOException("Its original drive isn't the drive of PC Tidy's admin quarantine, so it wasn't put back.");
        }
        else
        {
            FsOps.CreateParents(target);
            if (FsOps.TryRename(e.StoredPath, target) == RenameOutcome.NotSameVolume)
            {
                var copy = await VerifiedCopier.CopyAsync(e.StoredPath, target, ct).ConfigureAwait(false);
                leftInQuarantine = VerifiedCopier.RemoveVerified(e.StoredPath, copy.Source).LeftBehind.ToList();
            }
        }
        WriteManifest(entryDir, man with
        {
            Entry = e with { Restored = true },
            RestoredTo = target,
            LeftBehind = leftInQuarantine.Count > 0 ? leftInQuarantine : null,
        }, flushToDisk: false);
        return target;
    }

    private static void VerifyPayload(QuarantineEntry e, CancellationToken ct)
    {
        const string changed = "The copy in quarantine doesn't match what PC Tidy stored, so it wasn't put back automatically.";
        var now = TreeManifest.Build(e.StoredPath, ct);
        if (now.IsDirectory != e.IsDirectory || now.TotalBytes != e.SizeBytes || now.FileCount != e.FileCount)
            throw new QuarantineIntegrityException(changed);
        if (e.IsDirectory)
        {
            if (e.Hash is not null && now.MetadataHash != e.Hash) throw new QuarantineIntegrityException(changed);
            return;
        }
        if (now.Entries[0].LastWriteTicks != e.LastWriteUtc.Ticks) throw new QuarantineIntegrityException(changed);
        if (e.Hash is not null && VerifiedCopier.HashFile(e.StoredPath, ct) != e.Hash) throw new QuarantineIntegrityException(changed);
    }

    // ------------------------------------------------------------------ crash recovery

    /// <summary>
    /// After a crash: the entry a durable journal intent planned (<paramref name="entryId"/>) for <paramref name="originalPath"/>.
    /// If the move happened but its manifest never reached the disk, the manifest is rebuilt from the payload (the journal
    /// is the record of where it came from). An unfinished entry (nothing moved) is tidied away. Null = it didn't happen.
    /// </summary>
    internal Task<QuarantineEntry?> RecoverAsync(string runId, string entryId, string originalPath, CancellationToken ct) => Task.Run(() =>
    {
        RunIds.Validate(runId);
        if (!IsValidEntryId(entryId)) return null;
        VerifyAdminFolder();
        var original = LongPath.Display(LongPath.Io(originalPath));
        foreach (var store in AllStoreRoots())
        {
            ct.ThrowIfCancellationRequested();
            var (entryDir, stored) = EntryPaths(store, runId, entryId, original);
            if (!Directory.Exists(LongPath.Io(entryDir))) continue;
            var man = ReadManifest(entryDir);
            if (man is { State: not QuarantineManifest.Pending } && LongPath.Same(man.Entry.OriginalPath, original))
                return man.Entry.Restored || man.Entry.Purged || FsOps.Exists(man.Entry.StoredPath) ? man.Entry : null;
            if (man is null && FsOps.Stat(stored) is { } payload)
            {
                var rebuilt = Describe(NewEntry(entryId, runId, original, stored, payload), stored, ct);
                WriteManifest(entryDir, new QuarantineManifest { State = QuarantineManifest.Complete, Entry = rebuilt, SameVolume = true }, flushToDisk: true);
                EngineLog.Write(_dataRoot, $"Rebuilt the quarantine record of {original} after an interruption.");
                return rebuilt;
            }
            if (man is null || man.State == QuarantineManifest.Pending) FsOps.DeleteTree(entryDir); // nothing moved: the original is untouched
        }
        return (QuarantineEntry?)null;
    }, ct);

    // ------------------------------------------------------------------ list / purge / totals

    /// <summary>Entries (newest first). Runs off the caller's thread: it reads every manifest.</summary>
    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(string? runId, CancellationToken ct) => Task.Run(() => List(runId, ct), ct);

    private IReadOnlyList<QuarantineEntry> List(string? runId, CancellationToken ct)
    {
        if (runId is not null) RunIds.Validate(runId);
        VerifyAdminFolder();
        var list = new List<QuarantineEntry>();
        foreach (var (_, man) in EnumerateManifests(runId, ct))
        {
            if (man.State == QuarantineManifest.Pending) continue;
            var e = man.Entry;
            if (!e.Restored && !e.Purged && !FsOps.Exists(e.StoredPath)) continue; // move never happened (crash)
            list.Add(e);
        }
        return list.OrderByDescending(e => e.QuarantinedUtc).ToList();
    }

    /// <summary>Permanently deletes a run's quarantined files (only after the user confirmed). Manifests are kept, marked Purged.</summary>
    public Task PurgeRunAsync(string runId, CancellationToken ct) => Task.Run(() =>
    {
        RunIds.Validate(runId);
        VerifyAdminFolder();
        var failures = new List<string>();
        foreach (var store in AllStoreRoots())
        {
            var runDir = Path.Combine(store, runId);
            if (!Directory.Exists(LongPath.Io(runDir))) continue;
            foreach (var entryDir in SafeEnumerateDirs(LongPath.Io(runDir)))
            {
                ct.ThrowIfCancellationRequested();
                var man = ReadManifest(entryDir);
                if (man is null || man.State == QuarantineManifest.Pending)
                {
                    failures.AddRange(FsOps.DeleteTree(entryDir)); // unfinished copy: the original was never removed
                    continue;
                }
                var left = FsOps.DeleteTree(Path.Combine(entryDir, PayloadName));
                failures.AddRange(left);
                if (left.Count == 0 && !man.Entry.Restored)
                    WriteManifest(entryDir, man with { Entry = man.Entry with { Purged = true } }, flushToDisk: false);
            }
        }
        if (failures.Count > 0)
            throw new IOException($"{failures.Count} item(s) couldn't be deleted yet (in use?). Try again later.");
    }, ct);

    public async Task<long> TotalBytesAsync(CancellationToken ct)
        => (await ListAsync(null, ct).ConfigureAwait(false)).Where(e => !e.Restored && !e.Purged).Sum(e => e.SizeBytes);

    /// <summary>Runs that still hold quarantined items, oldest first.</summary>
    public async Task<IReadOnlyList<QuarantineRunInfo>> ListRunsAsync(CancellationToken ct)
        => (await ListAsync(null, ct).ConfigureAwait(false)).Where(e => !e.Restored && !e.Purged)
            .GroupBy(e => e.RunId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new QuarantineRunInfo(g.Key, g.Sum(e => e.SizeBytes), g.Count(), g.Min(e => e.QuarantinedUtc), g.Max(e => e.QuarantinedUtc)))
            .OrderBy(r => r.NewestUtc).ToList();

    /// <summary>Age mode: runs whose newest quarantined item is older than <paramref name="age"/> (for "delete these for good?").</summary>
    public async Task<IReadOnlyList<QuarantineRunInfo>> RunsOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - age;
        return (await ListRunsAsync(ct).ConfigureAwait(false)).Where(r => r.NewestUtc < cutoff).ToList();
    }

    /// <summary>
    /// Size-cap mode: the oldest runs to purge so what's left fits in <paramref name="capBytes"/>.
    /// Only a suggestion for the UI to ask about; nothing is deleted.
    /// </summary>
    public async Task<IReadOnlyList<QuarantineRunInfo>> RunsToPurgeForSizeCapAsync(long capBytes, CancellationToken ct)
    {
        var runs = await ListRunsAsync(ct).ConfigureAwait(false);
        var total = runs.Sum(r => r.Bytes);
        var pick = new List<QuarantineRunInfo>();
        foreach (var r in runs)
        {
            if (total <= capBytes) break;
            pick.Add(r);
            total -= r.Bytes;
        }
        return pick;
    }

    /// <summary>
    /// Permanently deletes every run older than <paramref name="age"/>. Call ONLY after the user said yes
    /// to the list from <see cref="RunsOlderThanAsync"/>; PC Tidy never purges on its own. Returns the purged run ids.
    /// </summary>
    public async Task<IReadOnlyList<string>> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        var runs = await RunsOlderThanAsync(age, ct).ConfigureAwait(false);
        foreach (var r in runs) await PurgeRunAsync(r.RunId, ct).ConfigureAwait(false);
        return runs.Select(r => r.RunId).ToList();
    }

    /// <summary>Entry by id within a run (null if unknown). Reads only that entry's manifest.</summary>
    public Task<QuarantineEntry?> FindAsync(string runId, string entryId, CancellationToken ct) => Task.Run(() =>
    {
        RunIds.Validate(runId);
        if (!IsValidEntryId(entryId)) return null;
        VerifyAdminFolder();
        foreach (var store in AllStoreRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (ReadManifest(Path.Combine(store, runId, entryId)) is { State: not QuarantineManifest.Pending } man
                && (man.Entry.Restored || man.Entry.Purged || FsOps.Exists(man.Entry.StoredPath)))
                return man.Entry;
        }
        return (QuarantineEntry?)null;
    }, ct);

    /// <summary>Extra detail for one entry (state, where it was restored to, what was left behind).</summary>
    internal QuarantineManifest? ReadManifestFor(QuarantineEntry entry) => ReadManifest(EntryDirOf(entry));

    // ------------------------------------------------------------------ store selection

    /// <summary>Every store root that may hold entries: the data folder and each fixed drive's private folder.</summary>
    public IEnumerable<string> AllStoreRoots()
    {
        yield return DataStoreRoot;
        if (IsAdminStore) yield break;
        foreach (var d in _locations.FixedDrives)
        {
            var root = Path.Combine(d, VolumeFolderName);
            // Another account's (or a planted) folder is never read: its records could point anywhere.
            if (Directory.Exists(LongPath.Io(root)) && VolumeStoreProblem(root) is null) yield return root;
        }
    }

    /// <summary>
    /// The per-drive store for this path, if the setting allows it, the path is on a different fixed drive
    /// than the data folder, and the folder is (or can be made) private to this user. Otherwise null (use the data folder).
    /// </summary>
    internal string? VolumeStoreFor(string display)
    {
        var mode = EngineSettings.From(_settings).PerVolumeQuarantine;
        if (mode == PerVolumeQuarantine.Off) return null;
        var drive = FixedDriveOf(display);
        if (drive is null) return null;
        var dataDrive = FixedDriveOf(_dataRoot);
        if (dataDrive is not null && LongPath.Same(drive, dataDrive)) return null;
        if (mode == PerVolumeQuarantine.NonSystemDrives && LongPath.Same(drive, _locations.SystemDrive)) return null;

        var root = Path.Combine(drive, VolumeFolderName);
        try
        {
            var io = LongPath.Io(root);
            if (!Directory.Exists(io) && !File.Exists(io))
            {
                var me = FolderTrust.CurrentUser();
                new DirectoryInfo(io).Create(FolderTrust.PrivateSecurity(new[] { me, FolderTrust.LocalSystem, FolderTrust.Administrators }));
                File.SetAttributes(io, FileAttributes.Directory | FileAttributes.Hidden);
                _volumeTrust.TryRemove(root, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        if (VolumeStoreProblem(root) is { } problem)
        {
            if (LastVolumeStoreProblem != problem)
                EngineLog.Write(_dataRoot, $"Not using {root}: {problem}. Quarantine goes to the data folder instead.");
            LastVolumeStoreProblem = problem;
            return null;
        }
        return root;
    }

    /// <summary>Null when a per-drive folder is safe to use: a real folder, owned by this user (or Administrators), private.</summary>
    private string? VolumeStoreProblem(string root)
    {
        var now = DateTime.UtcNow;
        if (_volumeTrust.TryGetValue(root, out var cached) && now - cached.CheckedUtc < TrustCacheLifetime) return cached.Problem;
        var me = FolderTrust.CurrentUser();
        var why = FolderTrust.WhyNotPrivate(root,
            owners: new[] { me, FolderTrust.Administrators },
            allowed: new[] { me, FolderTrust.LocalSystem, FolderTrust.Administrators });
        _volumeTrust[root] = (now, why);
        return why;
    }

    private string? FixedDriveOf(string path)
        => _locations.FixedDrives.Where(d => LongPath.IsUnder(path, d)).OrderByDescending(d => d.Length).FirstOrDefault();

    private void VerifyAdminFolder() => _verifyAdminFolder?.Invoke();

    // ------------------------------------------------------------------ manifests

    /// <summary>A fresh entry id (12 hex characters). The executor picks it before the step's intent is journaled.</summary>
    internal static string NewEntryId() => Guid.NewGuid().ToString("N")[..12];

    private static bool IsValidEntryId(string? id)
        => id is { Length: 12 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static (string EntryDir, string Stored) EntryPaths(string storeRoot, string runId, string id, string display)
    {
        var entryDir = Path.Combine(storeRoot, runId, id);
        return (entryDir, Path.Combine(entryDir, PayloadName, Path.GetFileName(display)));
    }

    private static QuarantineEntry NewEntry(string id, string runId, string display, string stored, FileSystemInfo info)
    {
        var size = info is FileInfo f ? f.Length : 0;
        return new QuarantineEntry
        {
            Id = id,
            RunId = runId,
            OriginalPath = display,
            StoredPath = stored,
            IsDirectory = info is DirectoryInfo,
            SizeBytes = size,
            FileCount = info is FileInfo ? 1 : 0,
            Attributes = info.Attributes,
            CreationUtc = info.CreationTimeUtc,
            LastWriteUtc = info.LastWriteTimeUtc,
            QuarantinedUtc = DateTime.UtcNow,
        };
    }

    private static QuarantineEntry Describe(QuarantineEntry entry, string stored, CancellationToken ct)
    {
        var info = FsOps.Stat(stored) ?? throw new FileNotFoundException("The quarantined copy is missing.", stored);
        if (info is FileInfo file)
        {
            // A rename doesn't touch the bytes, so a same-volume entry is identified by size and modified time (checked
            // again before a restore); reading every file back just to hash it made big cleanups slow on hard drives.
            // Copies across drives keep the XXH3 computed while copying.
            const string? hash = null;
            return entry with
            {
                SizeBytes = file.Length, FileCount = 1, Hash = hash,
                Attributes = file.Attributes, CreationUtc = file.CreationTimeUtc, LastWriteUtc = file.LastWriteTimeUtc,
            };
        }
        var m = TreeManifest.Build(stored, ct);
        return entry with
        {
            SizeBytes = m.TotalBytes, FileCount = m.FileCount, Hash = m.MetadataHash,
            Attributes = info.Attributes, CreationUtc = info.CreationTimeUtc, LastWriteUtc = info.LastWriteTimeUtc,
        };
    }

    /// <summary>
    /// The entry's folder, derived from its stored path and required to be exactly &lt;store&gt;\&lt;run&gt;\&lt;id&gt;
    /// of one of this store's roots (a record from anywhere else is refused).
    /// </summary>
    private string EntryDirOf(QuarantineEntry entry)
    {
        var entryDir = Path.GetDirectoryName(Path.GetDirectoryName(LongPath.Display(LongPath.Io(entry.StoredPath))))
                       ?? throw new ArgumentException("Bad quarantine entry.");
        RunIds.Validate(entry.RunId);
        if (!IsValidEntryId(entry.Id) || !AllStoreRoots().Any(r => LongPath.Same(entryDir, Path.Combine(r, entry.RunId, entry.Id))))
            throw new QuarantineIntegrityException("This quarantine record isn't one of PC Tidy's, so it wasn't used.");
        return entryDir;
    }

    private IEnumerable<(string Dir, QuarantineManifest Manifest)> EnumerateManifests(string? runId, CancellationToken ct)
    {
        foreach (var store in AllStoreRoots())
        {
            var storeIo = LongPath.Io(store);
            if (!Directory.Exists(storeIo)) continue;
            IEnumerable<string> runDirs = runId is null
                ? SafeEnumerateDirs(storeIo)
                : new[] { Path.Combine(storeIo, runId) }.Where(Directory.Exists);
            foreach (var runDir in runDirs)
            foreach (var entryDir in SafeEnumerateDirs(runDir))
            {
                ct.ThrowIfCancellationRequested();
                if (ReadManifest(entryDir) is { } man) yield return (entryDir, man);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string io)
    {
        try { return Directory.EnumerateDirectories(io).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static QuarantineManifest? ReadManifest(string entryDir)
    {
        try
        {
            var p = Path.Combine(LongPath.Io(entryDir), ManifestName);
            return File.Exists(p) ? JsonSerializer.Deserialize<QuarantineManifest>(File.ReadAllText(p), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteManifest(string entryDir, QuarantineManifest man, bool flushToDisk)
        => FsOps.WriteAllTextAtomic(Path.Combine(entryDir, ManifestName), JsonSerializer.Serialize(man, Json), flushToDisk);
}

/// <summary>One run's live quarantine contents (for the purge questions in the UI).</summary>
public sealed record QuarantineRunInfo(string RunId, long Bytes, int Entries, DateTime OldestUtc, DateTime NewestUtc);

/// <summary>On-disk manifest (entry.json) of one quarantine entry.</summary>
internal sealed record QuarantineManifest
{
    public const string Pending = "pending";   // cross-drive copy in progress: the original is untouched
    public const string Complete = "complete";
    public const string Partial = "partial";   // full verified copy, but some originals stayed (changed or in use)

    public int Version { get; init; } = 1;
    public required string State { get; init; }
    public required QuarantineEntry Entry { get; init; }
    public bool SameVolume { get; init; }
    public string? RestoredTo { get; init; }
    public List<string>? LeftBehind { get; init; }
}
