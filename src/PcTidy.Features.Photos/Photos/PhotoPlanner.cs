using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Photos.Shared;

namespace PcTidy.Features.Photos.Photos;

/// <summary>
/// Turns photo folders into a <see cref="PhotoPreview"/>: every photo and video goes to <c>&lt;folder&gt;\{year}\{year}-{month}</c>
/// (ISO, the same in every language) by its date taken, else its modified date; blurry shots and burst near-duplicates
/// are flagged for the user to look at. READ-ONLY: it lists folders, reads EXIF / Windows' media dates and decodes small
/// thumbnails. Online-only OneDrive files are never opened. The engine re-checks every plan item before acting.
/// </summary>
public sealed partial class PhotoPlanner
{
    public const string PermissionReason = "This folder is in OneDrive. Allow PC Tidy to sort it first (see the note at the top).";

    private readonly ISafetyGuard _safety;
    private readonly IInUseChecker _inUse;
    private readonly IFileWalker _walker;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly MediaDateResolver _dates;

    public PhotoPlanner(ISafetyGuard safety, IInUseChecker inUse, IFileWalker walker, AppSettings settings,
        IClock? clock = null, MediaDateResolver? dates = null)
    {
        _safety = safety;
        _inUse = inUse;
        _walker = walker;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _dates = dates ?? new MediaDateResolver(nowLocal: () => _clock.UtcNow.ToLocalTime());
    }

    /// <summary>Year folders PC Tidy made (or that look like them): already sorted, not looked into again.</summary>
    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearFolder();

    public Task<PhotoPreview> PlanAsync(PhotoRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => PlanCoreAsync(request, progress, ct), ct);

    private sealed class Pass(PhotoRequest request, IReadOnlyList<string> roots, DateTime nowUtc)
    {
        public PhotoRequest Request { get; } = request;
        public IReadOnlyList<string> Roots { get; } = roots;
        public DateTime NowUtc { get; } = nowUtc;
        public List<RootSummary> Summaries { get; } = new();
        public List<PhotoMove> Moves { get; } = new();
        public List<PhotoFlag> Flags { get; } = new();
        public List<SkippedEntry> Skipped { get; } = new();
        public List<string> Notes { get; } = new();
        public List<string> NeedsPermission { get; } = new();
        public DestinationNames Names { get; } = new();
        public int Checked;
    }

    /// <summary>One media file that passed the entry checks, with what was read about it.</summary>
    private sealed record Candidate(FileEntry Entry, string Root, bool NeedsPermission)
    {
        public MediaDate Date { get; set; }
        public ImageMetrics? Metrics { get; set; }
        public bool IsVideo => PhotoFormats.IsVideo(Entry.Name);
    }

    private async Task<PhotoPreview> PlanCoreAsync(PhotoRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var roots = request.Roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(PathUtil.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A folder inside another chosen folder is covered by it (with subfolders) or is its own root (without).
        if (request.IncludeSubfolders) roots = roots.Where(r => !roots.Any(o => o != r && PathUtil.IsUnder(r, o))).ToList();
        var pass = new Pass(request, roots, _clock.UtcNow);

        var candidates = new List<Candidate>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress($"Looking at {Path.GetFileName(root)}…"));
            candidates.AddRange(ListRoot(pass, root, ct));
        }

        candidates = await DropInUseAsync(pass, candidates, ct).ConfigureAwait(false);
        Measure(pass, candidates, progress, ct);

        foreach (var c in candidates.OrderBy(c => c.Entry.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            PlanMove(pass, c);
        }
        if (request.FindBursts || request.FindBlurry) PlanFlags(pass, candidates);

        return new PhotoPreview
        {
            Roots = pass.Summaries,
            Moves = pass.Moves,
            Flags = pass.Flags,
            Skipped = pass.Skipped,
            Notes = pass.Notes.Distinct().ToList(),
            NeedsPermission = pass.NeedsPermission,
            Checked = pass.Checked,
            Depth = request.Depth,
            Duration = sw.Elapsed,
        };
    }

    // ---------------------------------------------------------------- listing

    private List<Candidate> ListRoot(Pass pass, string root, CancellationToken ct)
    {
        var name = Path.GetFileName(root);
        var result = new List<Candidate>();
        if (!Directory.Exists(root))
        {
            pass.Summaries.Add(new RootSummary(root, name, 0, 0, 0, "Folder not found."));
            pass.Notes.Add($"{name}: folder not found.");
            return result;
        }
        if (RootProblem(root) is { } problem)
        {
            pass.Summaries.Add(new RootSummary(root, name, 0, 0, 0, problem));
            pass.Notes.Add($"{name}: {problem}");
            return result;
        }

        var access = CloudFolders.AccessFor(root, _safety, _settings);
        bool needsPermission = access == FolderAccess.NeedsPermission;
        if (needsPermission) pass.NeedsPermission.Add(root);

        var files = new List<FileEntry>();
        int folders = 0;
        bool skippedYears = false;
        try
        {
            _walker.Walk(root, new WalkOptions { Recurse = pass.Request.IncludeSubfolders, IncludeHidden = true, IncludeSystem = true }, e =>
            {
                if (!e.IsDirectory) { files.Add(e); return false; }
                folders++;
                if (!pass.Request.IncludeSubfolders) return false;
                if (e.Depth == 1 && YearFolder().IsMatch(e.Name)) { skippedYears = true; return false; }
                return MayLookInside(e);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            pass.Summaries.Add(new RootSummary(root, name, 0, 0, 0, "Couldn't open this folder."));
            return result;
        }
        if (skippedYears) pass.Notes.Add($"{name}: photos already in year folders (like {name}\\2025) are left as they are.");

        var media = 0;
        long bytes = 0;
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            bool loose = e.Depth == 1;
            if (!PhotoFormats.IsMedia(e.Name))
            {
                // Only loose files are listed: inside albums the other files are simply not ours to talk about.
                if (loose) Skip(pass, e, root, SkipKind.NoRule, "Not a photo or video. It stays put.");
                continue;
            }
            media++;
            bytes += e.Size;
            if (EntryProblem(e, pass.NowUtc) is { } skip) { Skip(pass, e, root, skip.Kind, skip.Reason); continue; }
            result.Add(new Candidate(e, root, needsPermission));
        }
        pass.Summaries.Add(new RootSummary(root, name, media, folders, bytes, null));
        return result;
    }

    /// <summary>Why nothing in this folder may be sorted at all, or null.</summary>
    private string? RootProblem(string root)
    {
        if (PathExclusions.IsExcluded(root, _settings.ScanExclusions)) return "You excluded this folder from scans.";
        if (PathExclusions.IsExcluded(root, _settings.ExtraProtectedFolders)) return "You protected this folder.";
        // Ask about the folder's contents: the user folder itself (Pictures) always "stays where it is".
        if (_safety.ClassifyPath(Path.Combine(root, "pc-tidy-probe.tmp")).Protection == PathProtection.Never) return "Protected folder. PC Tidy never changes it.";
        if (_safety.IsInsideProjectOrAppFolder(root, out var why))
            return "Looks like a code project or app folder" + (why is null ? "." : $" ({why}).") + " Never sorted inside.";
        return null;
    }

    private bool MayLookInside(FileEntry folder)
    {
        if (folder.IsReparsePoint || folder.IsHidden || folder.IsSystem) return false;
        if (PathExclusions.IsExcluded(folder.FullPath, _settings.ScanExclusions.Concat(_settings.ExtraProtectedFolders))) return false;
        return !_safety.IsInsideProjectOrAppFolder(folder.FullPath, out _);
    }

    /// <summary>Plan §9 "never moved": links, hidden/system, online-only, unfinished, changed in the last 10 minutes, excluded.</summary>
    private (SkipKind Kind, string Reason)? EntryProblem(FileEntry e, DateTime nowUtc)
    {
        if (SkipRules.Check(e, nowUtc) is { } basic) return basic;
        if (PathExclusions.IsExcluded(e.FullPath, _settings.ScanExclusions)) return (SkipKind.Protected, "You excluded this from scans.");
        if (PathExclusions.IsExcluded(e.FullPath, _settings.ExtraProtectedFolders)) return (SkipKind.Protected, "You protected this.");
        if (_safety.ClassifyPath(e.FullPath).Protection == PathProtection.Never) return (SkipKind.Protected, "Protected file. Never moved.");
        return null;
    }

    private static void Skip(Pass pass, FileEntry e, string root, SkipKind kind, string reason)
        => pass.Skipped.Add(new SkippedEntry(e.FullPath, root, e.IsDirectory, kind, reason, e.Size));

    private async Task<List<Candidate>> DropInUseAsync(Pass pass, List<Candidate> candidates, CancellationToken ct)
    {
        if (!pass.Request.CheckInUse || candidates.Count == 0) return candidates;
        var busy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var paths = candidates.Select(c => c.Entry.FullPath).ToList();
        try
        {
            foreach (var info in await _inUse.CheckAsync(paths, ct).ConfigureAwait(false))
                if (info.InUse) busy[info.Path] = info.Apps;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Restart Manager unavailable: the quick exclusive-open test per file instead.
            foreach (var p in paths)
                if (_inUse.IsLocked(p)) busy[p] = Array.Empty<string>();
        }
        var free = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (busy.TryGetValue(c.Entry.FullPath, out var apps))
                Skip(pass, c.Entry, c.Root, SkipKind.InUse, apps.Count > 0 ? $"Open in {string.Join(", ", apps)} right now." : "In use right now.");
            else free.Add(c);
        }
        return free;
    }

    // ---------------------------------------------------------------- reading dates and measuring

    private void Measure(Pass pass, List<Candidate> candidates, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        bool measure = pass.Request.FindBlurry || pass.Request.FindBursts;
        int done = 0, total = candidates.Count, measured = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        Parallel.ForEach(candidates, options, c =>
        {
            c.Date = _dates.Resolve(c.Entry, pass.Request.Depth);
            if (measure && !c.IsVideo && (c.Metrics = ImageAnalyzer.TryAnalyze(c.Entry.FullPath, c.Entry.Size)) is not null)
                Interlocked.Increment(ref measured);
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == total)
                progress?.Report(new ScanProgress(measure ? "Reading dates and checking photos…" : "Reading dates…", (double)n / Math.Max(1, total), n));
        });
        pass.Checked = measured;
    }

    // ---------------------------------------------------------------- moves

    private void PlanMove(Pass pass, Candidate c)
    {
        var e = c.Entry;
        var monthFolder = Path.Combine(c.Root, DestinationResolver.ExpandTokens(DestinationResolver.DateSuffix, c.Date.Local, CultureInfo.InvariantCulture));
        var currentFolder = Path.GetDirectoryName(e.FullPath)!;
        if (PathUtil.Normalize(currentFolder).Equals(PathUtil.Normalize(monthFolder), StringComparison.OrdinalIgnoreCase))
        {
            Skip(pass, e, c.Root, SkipKind.AlreadyThere, "Already in its date folder.");
            return;
        }

        var final = pass.Names.Claim(Path.Combine(monthFolder, e.Name), out var renumbered);
        var shortDest = DisplayPaths.Short(monthFolder, pass.Roots);
        var move = new PlanItem
        {
            Kind = ActionKind.MovePath,
            FeatureId = SortPermissions.PlanItemFeatureId,
            Description = $"Move \"{e.Name}\" to {shortDest}" + (renumbered ? $" as \"{Path.GetFileName(final)}\"" : ""),
            SourcePath = e.FullPath,
            DestinationPath = final,
            ExpectedSize = e.Size,
            ExpectedLastWriteUtc = e.LastWriteUtc,
            ClashPolicy = NameClashPolicy.AutoNumber,
        };
        var (ok, disabled, refusal) = Judge(move, c.NeedsPermission);
        if (!ok) { Skip(pass, e, c.Root, SkipKind.Protected, refusal!); return; }

        var reason = $"{Capitalize(c.Date.Describe())}, so it goes to {shortDest}. "
                     + (c.NeedsPermission || _safety.IsInCloudRoot(e.FullPath)
                         ? "It stays inside the same OneDrive folder, so OneDrive only renames it: nothing is uploaded again."
                         : "Same folder tree, so it's instant.")
                     + " Put back any time from History."
                     + (renumbered ? " A different file already has this name there, so it gets a number." : "");
        pass.Moves.Add(new PhotoMove
        {
            SourcePath = e.FullPath,
            DestinationPath = final,
            Root = c.Root,
            Date = c.Date,
            IsVideo = c.IsVideo,
            Size = e.Size,
            LastWriteUtc = e.LastWriteUtc,
            Renumbered = renumbered,
            Reason = reason,
            DisabledReason = disabled,
            Items = PlanFolders.Missing(monthFolder).Append(move).ToList(),
        });
    }

    // ---------------------------------------------------------------- blurry shots and bursts

    private void PlanFlags(Pass pass, List<Candidate> candidates)
    {
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byPath = candidates.ToDictionary(c => c.Entry.FullPath, StringComparer.OrdinalIgnoreCase);

        if (pass.Request.FindBursts)
        {
            // Per chosen folder: a burst is one shooting session. The same photo copied into two folders is a
            // duplicate, which the Duplicates section handles.
            var bursts = candidates.Where(c => c.Metrics is not null && c.Date.Source == DateSource.Taken)
                .GroupBy(c => c.Root, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => BurstFinder.Find(g.Select(c => new BurstCandidate(c.Entry.FullPath, c.Date.Local, c.Metrics!, c.Entry.Size)),
                    BurstFinder.MaxDistance(pass.Request.Depth)));
            foreach (var burst in bursts)
            {
                // The shot the burst keeps is never offered again as "blurry": ticking both lists must still keep one.
                flagged.Add(burst.Keep.Path);
                var keepName = Path.GetFileName(burst.Keep.Path);
                foreach (var extra in burst.Extras)
                {
                    var c = byPath[extra.Path];
                    bool identical = ImageMath.Distance(extra.Metrics.Hash, burst.Keep.Metrics.Hash) == 0 && extra.Size == burst.Keep.Size;
                    var reason = identical
                        ? $"Looks identical to \"{keepName}\", taken at the same moment. PC Tidy keeps that one. Your call: quarantine this copy? It can be restored."
                        : $"One of {burst.Shots.Count} shots taken within 2 seconds that look almost the same. PC Tidy keeps the sharpest, \"{keepName}\". Your call: quarantine this one? It can be restored.";
                    if (pass.Request.FindBlurry && BlurCheck.IsBlurry(extra.Metrics)) reason += " It also looks blurry.";
                    if (AddFlag(pass, c, PhotoFlagKind.BurstExtra, reason, burst.Keep.Path, burst.Shots.Count))
                        flagged.Add(c.Entry.FullPath);
                }
            }
        }

        if (pass.Request.FindBlurry)
        {
            foreach (var c in candidates.Where(c => c.Metrics is { } m && BlurCheck.IsBlurry(m) && !flagged.Contains(c.Entry.FullPath)))
            {
                var reason = $"Looks blurry: sharpness {c.Metrics!.Sharpness:0} (PC Tidy flags photos under {BlurCheck.Threshold:0}). "
                             + "This is a best guess, so look at the preview first. Your call: quarantine it? It can be restored.";
                AddFlag(pass, c, PhotoFlagKind.Blurry, reason, null, 0);
            }
        }
    }

    private bool AddFlag(Pass pass, Candidate c, PhotoFlagKind kind, string reason, string? keptPath, int burstSize)
    {
        var e = c.Entry;
        var item = new PlanItem
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = SortPermissions.PlanItemFeatureId,
            Description = $"Quarantine \"{e.Name}\" ({(kind == PhotoFlagKind.Blurry ? "blurry" : "burst shot")}, restorable)",
            SourcePath = e.FullPath,
            ExpectedSize = e.Size,
            ExpectedLastWriteUtc = e.LastWriteUtc,
        };
        var (ok, disabled, _) = Judge(item, c.NeedsPermission);
        if (!ok) return false; // The move row (or its "stays put" row) already explains this file.
        if (_safety.IsInCloudRoot(e.FullPath)) reason += CloudFolders.QuarantineNote;
        pass.Flags.Add(new PhotoFlag
        {
            Kind = kind,
            SourcePath = e.FullPath,
            Root = c.Root,
            Size = e.Size,
            LastWriteUtc = e.LastWriteUtc,
            Metrics = c.Metrics!,
            KeptPath = keptPath,
            BurstSize = burstSize,
            Reason = reason,
            DisabledReason = disabled,
            Item = item,
        });
        return true;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The guard's verdict. In a OneDrive folder the user hasn't allowed yet the guard says "ask first": the row is
    /// still shown (so the preview is complete) but can't be ticked. Anything else the guard refuses is skipped.
    /// </summary>
    private (bool Ok, string? DisabledReason, string? Refusal) Judge(PlanItem item, bool needsPermission)
    {
        var verdict = _safety.Check(item);
        if (verdict.Allowed) return (true, null, null);
        if (needsPermission && verdict.Protection == PathProtection.AskFirst) return (true, PermissionReason, null);
        return (false, null, verdict.Reason ?? "PC Tidy's safety check said no.");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
