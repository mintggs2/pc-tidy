using System.Diagnostics;
using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Detection;
using PcTidy.Features.Organize.Planning;

namespace PcTidy.Features.Organize.Takeout;

/// <summary>What to sort from a Google Takeout export, and where to.</summary>
public sealed record TakeoutRequest
{
    /// <summary>The unpacked export: the folder holding "Takeout", the "Takeout" folder, or its "Google Photos" folder.</summary>
    public required string SourceFolder { get; init; }
    /// <summary>Where the sorted photos go (default: Pictures\Google Photos).</summary>
    public required string DestinationFolder { get; init; }
    /// <summary>Photos from an album go to Albums\&lt;album&gt; instead of the date folders.</summary>
    public bool KeepAlbums { get; init; }
    /// <summary>Time zone for "which month was it taken in" (default: this PC's).</summary>
    public TimeZoneInfo? TimeZone { get; init; }
    public bool CheckInUse { get; init; } = true;
}

/// <summary>What sorting the export would do. Read-only; nothing has moved.</summary>
public sealed class TakeoutPreview
{
    public required string SourceFolder { get; init; }
    public required string DestinationFolder { get; init; }
    public required IReadOnlyList<string> PhotoFolders { get; init; }
    public required IReadOnlyList<PlannedAction> Actions { get; init; }
    public required IReadOnlyList<SkippedEntry> Skipped { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public int SidecarsMoved { get; init; }
    public TimeSpan Duration { get; init; }

    public IEnumerable<PlannedAction> Moves => Actions.Where(a => a.Kind == PlannedActionKind.Move);
}

/// <summary>
/// Google Takeout mode (Plan census v3). READ-ONLY: finds the Google Photos part of an unpacked export, reads each
/// photo's sidecar (Google's <c>.json</c> info file) for the date it was taken (falling back to the EXIF date, then
/// the sidecar's upload date, then the file date), and plans: photo → <c>Destination\2019\2019-09\</c> (or
/// <c>Destination\Albums\&lt;album&gt;\</c>), with its sidecar moved along so it stays next to the photo. A photo that
/// lands next to an identical copy (Google exports album photos twice) is offered for quarantine instead, unticked.
/// Every plan item passes the safety guard; the standard flow confirms, runs and undoes it.
/// </summary>
public sealed class TakeoutPlanner
{
    public const string RuleId = "takeout";
    public const string RuleName = "Google Takeout";
    public const string AlbumsFolder = "Albums";

    private readonly ISafetyGuard _safety;
    private readonly IInUseChecker _inUse;
    private readonly IFileWalker _walker;
    private readonly IClock _clock;

    public TakeoutPlanner(ISafetyGuard safety, IInUseChecker inUse, IFileWalker walker, IClock? clock = null)
    {
        _safety = safety;
        _inUse = inUse;
        _walker = walker;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The default destination: a "Google Photos" folder in the user's Pictures.</summary>
    public static string DefaultDestination(IKnownLocations k) => Path.Combine(k.Pictures, "Google Photos");

    /// <summary>
    /// What the Takeout tab starts with: <see cref="DefaultDestination"/> when Pictures is a local folder (or a cloud
    /// folder the user allowed), otherwise null so the user picks one. Pictures in OneDrive is very common on
    /// Windows 11, and a cloud destination would make every photo "stay put" (sorting 0) until the user allowed it.
    /// </summary>
    public static string? SuggestedDestination(IKnownLocations k, ISafetyGuard safety, AppSettings settings)
    {
        var dest = DefaultDestination(k);
        return DestinationNeedsPermission(dest, k, safety, settings) ? null : dest;
    }

    /// <summary>True when photos can't go into <paramref name="folder"/> yet: it's in OneDrive (or another sync folder) and not allowed in Settings.</summary>
    public static bool DestinationNeedsPermission(string folder, IKnownLocations k, ISafetyGuard safety, AppSettings settings)
    {
        // Ask about the folder's contents (a probe path inside it), never the folder itself.
        bool cloud = safety.IsInCloudRoot(Path.Combine(folder, "pc-tidy-probe.jpg")) || k.CloudRoots.Any(r => PathUtil.IsUnder(folder, r));
        return cloud && !settings.AllowedAskFirstFolders.ToArray().Any(f => PathUtil.IsUnder(folder, f));
    }

    /// <summary>The Google Photos folders inside an export (searches up to two levels down, so any of the usual picks works).</summary>
    public static IReadOnlyList<string> FindPhotoFolders(string source)
    {
        var found = new List<string>();
        void Look(string dir, int depth)
        {
            if (TakeoutLayout.PhotosFolderNames.Contains(Path.GetFileName(dir.TrimEnd('\\')))) { found.Add(dir); return; }
            if (depth == 0) return;
            try
            {
                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) Look(sub.FullName, depth - 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (Directory.Exists(source)) Look(PathUtil.Normalize(source), 2);
        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<TakeoutPreview> PlanAsync(TakeoutRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => PlanCoreAsync(request, progress, ct), ct);

    // ------------------------------------------------------------------ planning

    private sealed class Run(TakeoutRequest request, DateTime nowUtc)
    {
        public TakeoutRequest Request { get; } = request;
        public DateTime NowUtc { get; } = nowUtc;
        public TimeZoneInfo Zone { get; } = request.TimeZone ?? TimeZoneInfo.Local;
        public string Destination { get; } = PathUtil.Normalize(request.DestinationFolder);
        public List<PlannedAction> Actions { get; } = new();
        public List<SkippedEntry> Skipped { get; } = new();
        public List<string> Notes { get; } = new();
        /// <summary>Destination names claimed earlier in this batch (case-insensitive, like NTFS).</summary>
        public HashSet<string> TakenNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Planned photos by destination path: an identical later copy becomes a duplicate.</summary>
        public Dictionary<string, (long Size, string Source, string Final, string MoveId)> Planned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SidecarsClaimed { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Full content hashes computed in this run, by path.</summary>
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SidecarsMoved;
        public string Root = "";
    }

    /// <summary>One photo folder's files, with the sidecars indexed by the photo name they describe.</summary>
    private sealed record FolderFiles(string Path, TakeoutFolderKind Kind, List<FileEntry> Media, Dictionary<(string, int), TakeoutSidecar> Sidecars, List<TakeoutSidecar> AllSidecars);

    private async Task<TakeoutPreview> PlanCoreAsync(TakeoutRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var run = new Run(request, _clock.UtcNow);
        var source = PathUtil.Normalize(request.SourceFolder);
        var photoFolders = FindPhotoFolders(source);

        TakeoutPreview Result() => new()
        {
            SourceFolder = source, DestinationFolder = run.Destination, PhotoFolders = photoFolders,
            Actions = run.Actions, Skipped = run.Skipped, Notes = run.Notes.Distinct().ToList(), SidecarsMoved = run.SidecarsMoved, Duration = sw.Elapsed,
        };

        if (photoFolders.Count == 0)
        {
            run.Notes.Add("No Google Photos folder found here. Pick the folder you unpacked your Takeout download into.");
            return Result();
        }
        if (DestinationProblem(run.Destination, source) is { } problem)
        {
            run.Notes.Add(problem);
            return Result();
        }
        run.Notes.Add("Only the Google Photos part of your Takeout is sorted. Everything else in it stays where it is.");

        foreach (var photos in photoFolders)
        {
            ct.ThrowIfCancellationRequested();
            run.Root = photos;
            if (_safety.ClassifyPath(Path.Combine(photos, "pc-tidy-probe.tmp")).Protection == PathProtection.Never)
            {
                run.Notes.Add($"{photos}: protected folder. PC Tidy never changes it.");
                continue;
            }
            var folders = ListFolders(run, photos, ct);
            // Year folders first: their copy of a photo is the one kept when an album has the same photo.
            foreach (var folder in folders.OrderBy(f => f.Kind == TakeoutFolderKind.Year ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ScanProgress($"Reading {Path.GetFileName(folder.Path)}…"));
                await PlanFolderAsync(run, folder, ct).ConfigureAwait(false);
            }
        }
        if (run.Actions.Count(a => a.Kind == PlannedActionKind.DuplicateQuarantine) is > 0 and var dups)
            run.Notes.Add($"{dups:N0} photo{(dups == 1 ? " is" : "s are")} both in an album and in a year folder (Google exports them twice). The extra copy is offered for quarantine, unticked.");
        return Result();
    }

    private string? DestinationProblem(string destination, string source)
    {
        if (PathUtil.IsUnder(destination, source)) return "Pick a destination outside the Takeout folder, so sorted photos aren't picked up again.";
        if (_safety.ClassifyPath(destination).Protection == PathProtection.Never) return "That destination is protected. PC Tidy never moves files there.";
        if (_safety.IsInsideProjectOrAppFolder(destination, out _)) return "That destination is inside a code project or app folder. Pick another one.";
        return null;
    }

    /// <summary>Lists every folder under a Google Photos folder (read-only, links never followed) and reads the sidecars.</summary>
    private List<FolderFiles> ListFolders(Run run, string photosRoot, CancellationToken ct)
    {
        var byFolder = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        _walker.Walk(photosRoot, new WalkOptions { Recurse = true, IncludeHidden = true, IncludeSystem = true }, e =>
        {
            if (e.IsDirectory)
            {
                if (e.IsReparsePoint) { Skip(run, e, SkipKind.Link, "It's a link to somewhere else, so it's never followed."); return false; }
                return true;
            }
            var dir = Path.GetDirectoryName(e.FullPath)!;
            if (!byFolder.TryGetValue(dir, out var list)) byFolder[dir] = list = new List<FileEntry>();
            list.Add(e);
            return false;
        }, ct);

        var result = new List<FolderFiles>();
        foreach (var (dir, entries) in byFolder)
        {
            var kind = PathUtil.Normalize(dir).Equals(PathUtil.Normalize(photosRoot), StringComparison.OrdinalIgnoreCase)
                ? TakeoutFolderKind.Year
                : TakeoutLayout.Classify(Path.GetFileName(dir));
            var media = new List<FileEntry>();
            var sidecars = new List<TakeoutSidecar>();
            foreach (var e in entries)
            {
                if (TakeoutLayout.IsMedia(e.Name)) media.Add(e);
                else if (TakeoutLayout.IsSidecarCandidate(e.Name) && !e.IsCloudPlaceholder && TakeoutSidecar.Read(e.FullPath) is { } s) sidecars.Add(s);
                else Skip(run, e, SkipKind.LeaveInPlace, TakeoutLayout.InfoFileNames.Contains(e.Name) || e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "Takeout's own info file. It stays in the Takeout folder."
                    : "Not a photo or video. It stays where it is.");
            }
            var index = new Dictionary<(string, int), TakeoutSidecar>();
            foreach (var s in sidecars)
                index.TryAdd(TakeoutLayout.SidecarKey(Path.GetFileName(s.Path)), s);
            result.Add(new FolderFiles(dir, kind, media, index, sidecars));
        }
        return result;
    }

    private async Task PlanFolderAsync(Run run, FolderFiles folder, CancellationToken ct)
    {
        var albumName = Path.GetFileName(folder.Path);
        if (folder.Kind == TakeoutFolderKind.Bin)
        {
            foreach (var m in folder.Media) Skip(run, m, SkipKind.LeaveInPlace, "In your Google Photos bin (deleted photos), so it stays where it is.");
            foreach (var s in folder.AllSidecars) run.SidecarsClaimed.Add(s.Path);
            return;
        }

        var candidates = new List<(FileEntry Media, TakeoutSidecar? Sidecar, bool SharedSidecar, string? EditedOf)>();
        var mediaNames = folder.Media.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in folder.Media)
        {
            if (SkipRules.Check(m, run.NowUtc, quietByWriteTimeOnly: true) is { } skip) { Skip(run, m, skip.Kind, skip.Reason); continue; }
            if (_safety.ClassifyPath(m.FullPath).Protection == PathProtection.Never) { Skip(run, m, SkipKind.Protected, "Protected file. Never moved."); continue; }
            var original = TakeoutLayout.OriginalOfEdited(m.Name);
            var editedOf = original is not null && mediaNames.Contains(original) ? original : null;
            var sidecar = FindSidecar(folder, original is null ? m.Name : original, m.Name);
            // An edited copy uses its original's sidecar; the sidecar travels with the original when both are here.
            candidates.Add((m, sidecar, SharedSidecar: editedOf is not null, editedOf));
        }

        var inUse = run.Request.CheckInUse ? await CheckInUseAsync(candidates.Select(c => c.Media.FullPath).ToList(), ct).ConfigureAwait(false) : new();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (inUse.TryGetValue(c.Media.FullPath, out var apps))
            {
                Skip(run, c.Media, SkipKind.InUse, apps.Count > 0 ? $"Open in {string.Join(", ", apps)} right now." : "In use right now.");
                continue;
            }
            try
            {
                PlanPhoto(run, folder, albumName, c.Media, c.Sidecar, c.SharedSidecar, c.EditedOf, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(run, c.Media, SkipKind.Unreadable, "Couldn't read this file right now.");
            }
        }

        foreach (var s in folder.AllSidecars.Where(s => !run.SidecarsClaimed.Contains(s.Path)))
            run.Skipped.Add(new SkippedEntry(s.Path, run.Root, false, SkipKind.LeaveInPlace, "Google's info file for a photo that isn't here. It stays in the Takeout folder.", 0));
    }

    private static TakeoutSidecar? FindSidecar(FolderFiles folder, string lookupName, string mediaName)
    {
        foreach (var key in TakeoutLayout.MediaKeys(lookupName))
            if (folder.Sidecars.TryGetValue(key, out var s)) return s;
        // Long names: Google cut the photo name short in the sidecar's name.
        var truncated = folder.Sidecars.Where(kv => kv.Key.Item2 == 0 && TakeoutLayout.IsTruncatedKeyOf(kv.Key.Item1, lookupName)).Select(kv => kv.Value).ToList();
        if (truncated.Count == 1) return truncated[0];
        // Last resort: the sidecar whose "title" is this photo's name, if exactly one says so.
        var byTitle = folder.AllSidecars.Where(s => string.Equals(s.Title, lookupName, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Title, mediaName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    private void PlanPhoto(Run run, FolderFiles folder, string albumName, FileEntry media, TakeoutSidecar? sidecar, bool sharedSidecar, string? editedOf, CancellationToken ct)
    {
        var (takenLocal, dateSource) = DateOf(run, media, sidecar);
        bool toAlbum = run.Request.KeepAlbums && folder.Kind == TakeoutFolderKind.Album;
        var destFolder = toAlbum
            ? Path.Combine(run.Destination, AlbumsFolder, WindowsNames.CleanFolderSegment(albumName))
            : Path.Combine(run.Destination, takenLocal.ToString("yyyy", CultureInfo.InvariantCulture), takenLocal.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var group = toAlbum ? $@"{AlbumsFolder}\{albumName}" : takenLocal.ToString("yyyy", CultureInfo.InvariantCulture);
        var shortDest = DisplayPaths.Short(destFolder, new[] { run.Destination });
        var desired = Path.Combine(destFolder, media.Name);
        var dateText = $"Taken {takenLocal.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} ({DateSourceText(dateSource)}).";
        var where = folder.Kind == TakeoutFolderKind.Album ? $"From the album \"{albumName}\"." : $"From \"{albumName}\".";

        // The same photo already planned for this place (album copy of a year-folder photo): offer the extra copy for quarantine.
        if (run.Planned.TryGetValue(desired, out var earlier) && earlier.Size == media.Size && SameContent(run, media.FullPath, earlier.Source, ct))
        {
            // It relies on that copy: only once its move has run, and only while the moved file still has the same bytes.
            AddDuplicate(run, media, sidecar, sharedSidecar, shortDest, where, keepPath: earlier.Final, dependsOn: earlier.MoveId, ct);
            return;
        }

        bool clash = run.TakenNames.Contains(desired) || File.Exists(desired) || Directory.Exists(desired);
        if (clash && !run.TakenNames.Contains(desired) && File.Exists(desired) && new FileInfo(desired).Length == media.Size && SameContent(run, media.FullPath, desired, ct))
        {
            AddDuplicate(run, media, sidecar, sharedSidecar, shortDest, where, keepPath: desired, dependsOn: null, ct, alreadyThere: true);
            return;
        }
        var final = clash ? NextFreeName(run, desired) : desired;
        run.TakenNames.Add(final);
        var moveId = Guid.NewGuid().ToString("N");
        run.Planned[desired] = (media.Size, media.FullPath, final, moveId);

        var moves = new List<PlanItem>
        {
            new()
            {
                Id = moveId, Kind = ActionKind.MovePath, FeatureId = OrganizeModule.FeatureId,
                Description = $"Move \"{media.Name}\" to {shortDest}" + (clash ? $" as \"{Path.GetFileName(final)}\"" : ""),
                SourcePath = media.FullPath, DestinationPath = final, ExpectedSize = media.Size, ExpectedLastWriteUtc = media.LastWriteUtc,
                ClashPolicy = NameClashPolicy.AutoNumber, Args = Args(),
            },
        };
        string sidecarText = "";
        // The sidecar moves along with the photo it belongs to (an edited copy's sidecar goes with its original).
        if (sidecar is not null && !sharedSidecar && run.SidecarsClaimed.Add(sidecar.Path))
        {
            var sidecarName = clash ? Path.GetFileName(final) + ".json" : Path.GetFileName(sidecar.Path);
            var sidecarDest = Path.Combine(destFolder, sidecarName);
            if (run.TakenNames.Contains(sidecarDest) || File.Exists(sidecarDest)) sidecarDest = NextFreeName(run, sidecarDest);
            run.TakenNames.Add(sidecarDest);
            var info = new FileInfo(sidecar.Path);
            moves.Add(new PlanItem
            {
                Kind = ActionKind.MovePath, FeatureId = OrganizeModule.FeatureId,
                Description = $"Move Google's info file \"{info.Name}\" next to the photo in {shortDest}",
                SourcePath = sidecar.Path, DestinationPath = sidecarDest, ExpectedSize = info.Length, ExpectedLastWriteUtc = info.LastWriteTimeUtc,
                ClashPolicy = NameClashPolicy.AutoNumber, Args = Args(),
            });
            sidecarText = " Google's info file moves along and stays next to the photo.";
        }
        else if (sidecar is not null && sharedSidecar) sidecarText = $" Edited copy of \"{editedOf}\": both are kept; the info file stays with the original.";

        foreach (var item in moves)
        {
            var verdict = _safety.Check(item);
            if (verdict.Allowed) continue;
            Skip(run, media, SkipKind.Protected, verdict.Reason ?? "PC Tidy's safety check said no.");
            if (moves.Count > 1) run.SidecarsClaimed.Remove(moves[1].SourcePath!);
            return;
        }
        if (moves.Count > 1) run.SidecarsMoved++;
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.Move, SourcePath = media.FullPath, DestinationPath = final, Root = run.Root,
            RuleId = RuleId, RuleName = RuleName, Group = group, Size = media.Size, LastWriteUtc = media.LastWriteUtc, Renumbered = clash,
            Why = dateText,
            Reason = $"{dateText} {where}{sidecarText}" + (clash ? " A different file already has this name, so it gets a number." : "") + " Put back any time from History.",
            Badge = Badge.Safe, DefaultSelected = true, Automatic = false,
            Items = MissingFolders(destFolder).Concat(moves).ToList(),
        });
    }

    /// <summary>
    /// The extra copy goes to quarantine only while the copy it relies on is really there (ORG-1): the engine re-hashes
    /// both right before (<c>verifyHash</c> + <c>keepPath</c>, the copy's final place), and when that copy is moved in
    /// the same run, only after its move finished (<see cref="PlanItem.DependsOn"/>; an unticked move keeps this one too).
    /// Its sidecar goes only together with it.
    /// </summary>
    private void AddDuplicate(Run run, FileEntry media, TakeoutSidecar? sidecar, bool sharedSidecar, string shortDest, string where,
        string keepPath, string? dependsOn, CancellationToken ct, bool alreadyThere = false)
    {
        var verify = Args();
        verify[DuplicateCheck.HashArg] = Hash(run, media.FullPath, ct);
        verify[DuplicateCheck.KeepArg] = keepPath;
        verify[DuplicateCheck.ModeArg] = "file";
        var photo = new PlanItem
        {
            Kind = ActionKind.QuarantinePath, FeatureId = OrganizeModule.FeatureId,
            Description = $"Quarantine \"{media.Name}\" (an identical copy goes to {shortDest})",
            SourcePath = media.FullPath, ExpectedSize = media.Size, ExpectedLastWriteUtc = media.LastWriteUtc, Args = verify,
            DependsOn = dependsOn is null ? null : new[] { dependsOn },
        };
        var items = new List<PlanItem> { photo };
        if (sidecar is not null && !sharedSidecar && run.SidecarsClaimed.Add(sidecar.Path))
        {
            var info = new FileInfo(sidecar.Path);
            items.Add(new PlanItem
            {
                Kind = ActionKind.QuarantinePath, FeatureId = OrganizeModule.FeatureId,
                Description = $"Quarantine Google's info file \"{info.Name}\" with its extra photo",
                SourcePath = sidecar.Path, ExpectedSize = info.Length, ExpectedLastWriteUtc = info.LastWriteTimeUtc, Args = Args(),
                DependsOn = new[] { photo.Id },
            });
        }
        if (items.Any(i => !_safety.Check(i).Allowed))
        {
            Skip(run, media, SkipKind.Protected, "An identical copy is sorted already. This one stays where it is.");
            return;
        }
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.DuplicateQuarantine, SourcePath = media.FullPath, Root = run.Root, RuleId = RuleId, RuleName = RuleName,
            Group = "Duplicates", Size = media.Size, LastWriteUtc = media.LastWriteUtc, DuplicateOf = shortDest,
            Why = where,
            Reason = $"{where} An identical copy (same size and content) {(alreadyThere ? "is already" : "goes")} in {shortDest}. Quarantine this one? You can restore it.",
            Badge = Badge.Check, DefaultSelected = false, Automatic = false, Items = items,
        });
    }

    private static Dictionary<string, string> Args() => new() { [OrganizeLedger.RuleIdArg] = RuleId };

    /// <summary>The Args the engine's duplicate re-check reads (PcTidy.Engine DuplicateVerifier; the same names Duplicates uses).</summary>
    internal static class DuplicateCheck
    {
        public const string HashArg = "verifyHash";
        public const string KeepArg = "keepPath";
        public const string ModeArg = "verifyMode";
    }

    /// <summary>Sidecar "taken" time → EXIF date taken → sidecar upload time → file date.</summary>
    private static (DateTime Local, TakeoutDateSource Source) DateOf(Run run, FileEntry media, TakeoutSidecar? sidecar)
    {
        if (sidecar?.PhotoTakenUtc is { } taken) return (TimeZoneInfo.ConvertTimeFromUtc(taken, run.Zone), TakeoutDateSource.Sidecar);
        var isPicture = Rules.OrganizeCategories.Find(Rules.OrganizeCategories.Pictures)!.Extensions.Contains(Rules.OrganizeCategories.ExtensionOf(media.Name));
        // EXIF holds the camera's own local time: used as it is.
        if (isPicture && !media.IsCloudPlaceholder && ExifReader.ReadDateTaken(media.FullPath) is { } exif) return (exif, TakeoutDateSource.Exif);
        if (sidecar?.CreationUtc is { } created) return (TimeZoneInfo.ConvertTimeFromUtc(created, run.Zone), TakeoutDateSource.Uploaded);
        return (TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(media.LastWriteUtc, DateTimeKind.Utc), run.Zone), TakeoutDateSource.FileDate);
    }

    private static string DateSourceText(TakeoutDateSource s) => s switch
    {
        TakeoutDateSource.Sidecar => "from Google's info file",
        TakeoutDateSource.Exif => "from the photo itself",
        TakeoutDateSource.Uploaded => "upload date, the photo has no date taken",
        _ => "file date, no date taken was found",
    };

    /// <summary>
    /// Full hashes are kept for the run (by path): a year-folder photo that sits in several albums, and the album copy
    /// that's then planned as a duplicate, are each read once, not once per comparison.
    /// </summary>
    private static string Hash(Run run, string path, CancellationToken ct)
    {
        if (run.Hashes.TryGetValue(path, out var known)) return known;
        var hash = FileHash.Full(path, ct);
        run.Hashes[path] = hash;
        return hash;
    }

    private static bool SameContent(Run run, string a, string b, CancellationToken ct)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            if (CloudFile.IsPlaceholder(fa.Attributes) || CloudFile.IsPlaceholder(fb.Attributes)) return false;
            return string.Equals(Hash(run, a, ct), Hash(run, b, ct), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> CheckInUseAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return result;
        try
        {
            foreach (var info in await _inUse.CheckAsync(paths, ct).ConfigureAwait(false))
                if (info.InUse) result[info.Path] = info.Apps;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var p in paths)
                if (_inUse.IsLocked(p)) result[p] = Array.Empty<string>();
        }
        return result;
    }

    private static string NextFreeName(Run run, string desiredPath)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileName(desiredPath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; i < 100_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!run.TakenNames.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("No free name for " + desiredPath);
    }

    private static IEnumerable<PlanItem> MissingFolders(string folder)
    {
        var missing = new Stack<string>();
        for (var d = folder; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        return missing.Select(d => new PlanItem
        {
            Kind = ActionKind.CreateDirectory, FeatureId = OrganizeModule.FeatureId,
            Description = $"Create folder \"{Path.GetFileName(d)}\"", DestinationPath = d, IsDirectory = true,
        }).ToList();
    }

    private static void Skip(Run run, FileEntry e, SkipKind kind, string reason)
        => run.Skipped.Add(new SkippedEntry(e.FullPath, run.Root, e.IsDirectory, kind, reason, e.Size));
}

/// <summary>Where a photo's date came from (shown on every preview row).</summary>
public enum TakeoutDateSource { Sidecar, Exif, Uploaded, FileDate }
