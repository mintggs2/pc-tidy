using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Planning;

/// <summary>
/// Turns folders + rules into an <see cref="OrganizePreview"/>. READ-ONLY: it lists folders, reads small parts of
/// files (installer markers, EXIF, download mark) and hashes same-size name clashes; it never changes anything.
/// The engine carries out the resulting plan items and re-checks every one of them right before acting.
/// </summary>
public sealed class OrganizePlanner
{
    private const int MaxFolderEntries = 5000;

    private readonly ISafetyGuard _safety;
    private readonly IInUseChecker _inUse;
    private readonly IFileWalker _walker;
    private readonly AppSettings _appSettings;
    private readonly IClock _clock;
    private readonly IKnownLocations? _locations;

    /// <param name="locations">The PC's known folders (3.3 safety rules: off-limits and app-owned folders, Desktop/Documents,
    /// Downloads, recent files). Without them only the path-name checks of <see cref="SafetyFolders"/> apply.</param>
    public OrganizePlanner(ISafetyGuard safety, IInUseChecker inUse, IFileWalker walker, AppSettings appSettings, IClock? clock = null, IKnownLocations? locations = null)
    {
        _safety = safety;
        _inUse = inUse;
        _walker = walker;
        _appSettings = appSettings;
        _clock = clock ?? SystemClock.Instance;
        _locations = locations;
    }

    /// <summary>Why a folder can never be organized (off-limits or app-owned), or null. Also used by the folder picker.</summary>
    public string? NeverSortReason(string folder)
        => SafetyFolders.ForbiddenReason(folder, _locations)
           ?? (SafetyFolders.AppOwnedPartAnywhere(folder, _locations) is { } app ? $"\"{app}\" belongs to an app or game, so PC Tidy never sorts inside it." : null);

    private bool IsKnown(string root, Func<IKnownLocations, string> pick)
        => _locations is { } k && pick(k) is { Length: > 0 } known && PathUtil.Normalize(known).Equals(PathUtil.Normalize(root), StringComparison.OrdinalIgnoreCase);

    public Task<OrganizePreview> PlanAsync(OrganizeRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => PlanCoreAsync(request, progress, ct), ct);

    private async Task<OrganizePreview> PlanCoreAsync(OrganizeRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var roots = request.Roots.Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var run = new PlanRun(request, roots, _clock.UtcNow);

        var (blocked, problems) = RuleValidator.ValidateAll(request.Settings.Rules, roots, _safety, _appSettings);
        run.Blocked.UnionWith(blocked);
        foreach (var p in problems.Where(p => p.Blocking).DistinctBy(p => (p.RuleId, p.Message)))
            run.Notes.Add($"Rule \"{p.RuleName}\" is paused: {p.Message}");

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress($"Looking at {Path.GetFileName(root)}…"));
            await PlanRootAsync(run, root, progress, ct).ConfigureAwait(false);
        }

        return new OrganizePreview
        {
            Roots = run.Summaries,
            Actions = run.Actions,
            Skipped = run.Skipped,
            Notes = run.Notes.Distinct().ToList(),
            Problems = problems,
            Depth = request.Depth,
            Duration = sw.Elapsed,
        };
    }

    /// <summary>Mutable state of one planning pass (names already taken, folders already planned…).</summary>
    private sealed class PlanRun(OrganizeRequest request, IReadOnlyList<string> roots, DateTime nowUtc)
    {
        public OrganizeRequest Request { get; } = request;
        public IReadOnlyList<string> Roots { get; } = roots;
        public DateTime NowUtc { get; } = nowUtc;
        public HashSet<string> Blocked { get; } = new();
        public List<PlannedAction> Actions { get; } = new();
        public List<SkippedEntry> Skipped { get; } = new();
        public List<RootSummary> Summaries { get; } = new();
        public List<string> Notes { get; } = new();
        /// <summary>Destination names claimed by earlier items in this batch (case-insensitive, like NTFS).</summary>
        public HashSet<string> TakenNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Files PC Tidy itself put where they are: never picked up again.</summary>
        public Dictionary<string, LedgerEntry> Ledger { get; } = OrganizeLedger.Index(request.Settings.Ledger ?? new());
        /// <summary>Source names that will be free once their file moves away (lets renames swap).</summary>
        public HashSet<string> LeavingNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Names in Windows' Recent items list, read once per scan.</summary>
        public HashSet<string>? Recent { get; set; }
        /// <summary>The rules are (or include) "Sort by type": then every rule in the list only moves (code review N13).</summary>
        public bool ByTypeList { get; } = request.Settings.Rules.Any(r => r.Enabled && RulePresets.IsByTypeRule(r));
    }

    private async Task PlanRootAsync(PlanRun run, string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var displayName = Path.GetFileName(root);
        if (!Directory.Exists(root))
        {
            run.Summaries.Add(new RootSummary(root, displayName, 0, 0, 0, "Folder not found."));
            run.Notes.Add($"{displayName}: folder not found.");
            return;
        }

        var entries = new List<FileEntry>();
        try
        {
            _walker.Walk(root, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = true }, e => { entries.Add(e); return false; }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Summaries.Add(new RootSummary(root, displayName, 0, 0, 0, "Couldn't open this folder."));
            return;
        }
        if (run.Request.OnlyPaths is { } only) entries = entries.Where(e => only.Contains(e.FullPath)).ToList();

        var files = entries.Where(e => !e.IsDirectory).ToList();
        var folders = entries.Where(e => e.IsDirectory).ToList();
        var rootSkip = RootSkipReason(run, root);
        run.Summaries.Add(new RootSummary(root, displayName, files.Count, folders.Count, files.Sum(f => f.Size), rootSkip?.Reason));
        if (rootSkip is { } rs)
        {
            // Every file is still listed, so the preview explains why nothing in this folder moves.
            foreach (var e in entries) Skip(run, e, root, rs.Kind, rs.Reason);
            run.Notes.Add($"{displayName}: {rs.Reason}");
            return;
        }

        // 1) Entry-only checks, then rules (no Restart Manager yet: it's the slowest step).
        var matched = new List<(FileFacts Facts, OrganizeRule Rule)>();
        int seen = 0;
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 100 == 0) progress?.Report(new ScanProgress($"Checking {displayName}…", (double)seen / files.Count, seen));

            if (EntrySkipReason(run, e) is { } skip) { Skip(run, e, root, skip.Kind, skip.Reason); continue; }
            try
            {
                var facts = new FileFacts(e, root, run.Request.Depth) { RootIsDownloads = IsKnown(root, k => k.Downloads) };
                // A program dressed up as a document or picture is never sorted, whatever the rules say (3.3).
                if (Rules.SortTypes.DisguiseProblem(facts) is { } disguise) { Skip(run, e, root, SkipKind.Disguised, disguise); continue; }
                // A portable app's own files (Tool.exe + Tool.ini / Tool.json / Tool.dll) stay next to it (review S3).
                if (BelongsToProgramNextToIt(e, files) is { } exe) { Skip(run, e, root, SkipKind.PortableApp, $"It belongs to {exe} next to it, so it stays with it."); continue; }
                var rule = RuleEvaluator.FirstMatch(run.Request.Settings.Rules, facts, run.NowUtc, run.Blocked);
                run.Notes.AddRange(facts.Warnings);
                if (rule is null) { Skip(run, e, root, SkipKind.NoRule, "No rule matched. It stays put."); continue; }
                if (rule.Action.Kind == RuleActionKind.LeaveInPlace) { SkipLeftInPlace(run, facts, rule); continue; }
                matched.Add((facts, rule));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(run, e, root, SkipKind.Unreadable, "Couldn't read this file right now.");
            }
        }

        // 2) Files held open by an app stay put (the preview says which app).
        var inUse = run.Request.CheckInUse ? await CheckInUseAsync(matched.Select(m => m.Facts.Path).ToList(), ct).ConfigureAwait(false) : new();
        foreach (var (facts, rule) in matched)
        {
            ct.ThrowIfCancellationRequested();
            if (inUse.TryGetValue(facts.Path, out var apps))
            {
                Skip(run, facts.Entry, root, SkipKind.InUse, apps.Count > 0 ? $"Open in {string.Join(", ", apps)} right now." : "In use right now.");
                continue;
            }
            try
            {
                PlanFile(run, facts, rule, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(run, facts.Entry, root, SkipKind.Unreadable, "Couldn't read this file right now.");
            }
        }

        // 3) Loose folders: stay put unless "move whole folders" is on.
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (OrganizeCategories.FolderNames.Contains(folder.Name) || IsRuleFolder(run, folder.Name))
                Skip(run, folder, root, SkipKind.AlreadySorted, "PC Tidy's sorting folder.");
            else if (PathExclusions.IsExcluded(folder.FullPath, _appSettings.ScanExclusions.Concat(_appSettings.ExtraProtectedFolders)))
                Skip(run, folder, root, SkipKind.Protected, "You excluded or protected this folder.");
            else if (SafetyFolders.IsAppOwnedName(folder.Name) || SafetyFolders.ForbiddenReason(folder.FullPath, _locations) is not null)
                Skip(run, folder, root, SkipKind.ProjectOrApp, $"\"{folder.Name}\" belongs to an app or game, so it stays exactly where it is.");
            else if (!run.Request.Settings.MoveWholeFolders)
                Skip(run, folder, root, SkipKind.Folder, "Folders stay put. Only loose files are sorted.");
            else if (run.Request.Settings.Rules.Any(r => r.Enabled && RulePresets.IsByTypeRule(r)))
                Skip(run, folder, root, SkipKind.Folder, "Folders stay put with Sort by type. Only loose files are sorted by what they are.");
            else if (IsKnown(root, k => k.Desktop) || IsKnown(root, k => k.Documents))
                Skip(run, folder, root, SkipKind.Folder, "Folders on your Desktop and in Documents always stay put (apps and games keep their files there).");
            else
                PlanFolder(run, folder, root, ct);
        }
    }

    // ---------------------------------------------------------------- skip checks

    private (SkipKind Kind, string Reason)? RootSkipReason(PlanRun run, string root)
    {
        bool allowed = IsAllowedByUser(root);
        if (PathExclusions.IsExcluded(root, _appSettings.ScanExclusions)) return (SkipKind.Protected, "You excluded this folder from scans.");
        if (PathExclusions.IsExcluded(root, _appSettings.ExtraProtectedFolders)) return (SkipKind.Protected, "You protected this folder.");
        // 3.3: AppData, ProgramData, game and launcher folders and app-owned folders, even when picked by hand (review S1/S2).
        if (SafetyFolders.ForbiddenReason(root, _locations) is { } forbidden) return (SkipKind.Protected, forbidden);
        if (SafetyFolders.AppOwnedPartAnywhere(root, _locations) is { } app) return (SkipKind.ProjectOrApp, $"\"{app}\" belongs to an app or game, so PC Tidy never sorts inside it.");
        // Ask about the folder's CONTENTS: a user folder like Downloads can't itself be moved (the guard says Never
        // for the folder), but its files may be sorted. A probe path inside it answers "may things in here change?".
        if (_safety.ClassifyPath(Path.Combine(root, "pc-tidy-probe.tmp")).Protection == PathProtection.Never)
            return (SkipKind.Protected, "Protected folder. PC Tidy never changes it.");
        if (!allowed && _safety.IsInCloudRoot(root)) return (SkipKind.CloudFolder, "This folder is in OneDrive. Allow it in Settings to sort it.");
        if (_safety.IsInsideProjectOrAppFolder(root, out var why))
            return (SkipKind.ProjectOrApp, "Looks like a code project or app folder" + (why is null ? "." : $" ({why}).") + " Never sorted inside.");
        // A category folder PC Tidy made (Downloads\Pictures) is already sorted; sorting it again would nest folders.
        var parent = Path.GetDirectoryName(root);
        if (parent is not null && (OrganizeCategories.FolderNames.Contains(Path.GetFileName(root)) || Rules.SortTypes.TopFolderNames.Contains(Path.GetFileName(root)))
            && run.Roots.Concat(_appSettings.AllowedAskFirstFolders).Any(r => PathUtil.Normalize(r).Equals(PathUtil.Normalize(parent), StringComparison.OrdinalIgnoreCase)))
            return (SkipKind.AlreadySorted, "Already a PC Tidy sorting folder.");
        return null;
    }

    private (SkipKind Kind, string Reason)? EntrySkipReason(PlanRun run, FileEntry e)
    {
        if (SkipRules.Check(e, run.NowUtc) is { } basic) return basic;
        // Firefox makes an empty file next to "name.part" while it downloads (review S14).
        if (e.Size == 0 && File.Exists(e.FullPath + ".part")) return (SkipKind.StillDownloading, "Still downloading (Firefox).");
        if (PathExclusions.IsExcluded(e.FullPath, _appSettings.ScanExclusions)) return (SkipKind.Protected, "You excluded this from scans.");
        if (PathExclusions.IsExcluded(e.FullPath, _appSettings.ExtraProtectedFolders)) return (SkipKind.Protected, "You protected this.");
        if (run.Ledger.TryGetValue(PathUtil.Normalize(e.FullPath), out var done) && (done.Size == 0 || done.Size == e.Size))
        {
            var rule = run.Request.Settings.Rules.FirstOrDefault(r => r.Id == done.RuleId)?.Name ?? "a rule";
            return (SkipKind.AlreadySorted, $"PC Tidy already sorted this with \"{rule}\". It won't be moved again.");
        }
        if (run.Request.NotQuietPaths?.Contains(e.FullPath) == true) return (SkipKind.RecentlyChanged, "Changed in the last 10 minutes. It may still be in use.");
        if (_safety.ClassifyPath(e.FullPath).Protection == PathProtection.Never) return (SkipKind.Protected, "Protected file. Never moved.");
        return null;
    }

    private bool IsAllowedByUser(string path) => _appSettings.AllowedAskFirstFolders.Any(f => PathUtil.IsUnder(path, f));

    private static bool IsRuleFolder(PlanRun run, string folderName)
        => run.Request.Settings.Rules.Any(r => r.Enabled && r.Action.Kind == RuleActionKind.MoveToFolder
            && !Path.IsPathFullyQualified(r.Action.Destination ?? "")
            && string.Equals((r.Action.Destination ?? "").Split('\\', '/').FirstOrDefault()?.Trim(), folderName, StringComparison.OrdinalIgnoreCase));

    private static void Skip(PlanRun run, FileEntry e, string root, SkipKind kind, string reason)
        => run.Skipped.Add(new SkippedEntry(e.FullPath, root, e.IsDirectory, kind, reason, e.Size));

    private static void SkipLeftInPlace(PlanRun run, FileFacts facts, OrganizeRule rule)
    {
        if (facts.Extension == "exe")
            Skip(run, facts.Entry, facts.Root, SkipKind.PortableApp, "Looks like a portable app, so it stays where it is.");
        else if (rule.Id == DefaultRules.OtherId)
            Skip(run, facts.Entry, facts.Root, SkipKind.LeaveInPlace, "Other file types stay where they are.");
        else
            Skip(run, facts.Entry, facts.Root, SkipKind.LeaveInPlace, $"Rule \"{rule.Name}\" leaves it in place.");
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
            // Restart Manager unavailable: fall back to the quick exclusive-open test per file.
            foreach (var p in paths)
                if (_inUse.IsLocked(p)) result[p] = Array.Empty<string>();
        }
        return result;
    }

    // ---------------------------------------------------------------- per-file planning


    private void PlanFile(PlanRun run, FileFacts facts, OrganizeRule rule, CancellationToken ct)
    {
        // Plain-English "why it matched" with the file's real values, shown on every preview row.
        var why = $"Rule \"{rule.Name}\": " + string.Join(", ", RuleEvaluator.Explain(rule.Conditions, facts, run.NowUtc)) + ".";
        // "Sort by type" only moves files (owner: nothing is ever deleted), even if one of its rules ("Other" included) was
        // edited, or a rule was duplicated or added next to them.
        if (run.ByTypeList && rule.Action.Kind != RuleActionKind.MoveToFolder)
        {
            Skip(run, facts.Entry, facts.Root, SkipKind.RuleBlocked, $"Sort by type only moves files, so the rule \"{rule.Name}\" doesn't run while it does something else.");
            return;
        }
        switch (rule.Action.Kind)
        {
            case RuleActionKind.MoveToFolder: PlanMove(run, facts, rule, why, ct); break;
            case RuleActionKind.Quarantine: PlanQuarantine(run, facts, rule, why); break;
            case RuleActionKind.Rename: PlanRename(run, facts, rule, why); break;
        }
    }

    private void PlanMove(PlanRun run, FileFacts facts, OrganizeRule rule, string why, CancellationToken ct)
    {
        var root = facts.Root;
        var destFolder = DestinationResolver.ResolveFolder(rule.Action, root, facts.FolderDate, run.Request.Settings.UseDateFolders, run.Request.Culture);
        if (destFolder is null) { Skip(run, facts.Entry, root, SkipKind.RuleBlocked, $"Rule \"{rule.Name}\" has a folder that isn't valid."); return; }
        if (destFolder.Equals(PathUtil.Normalize(facts.Folder), StringComparison.OrdinalIgnoreCase))
        { Skip(run, facts.Entry, root, SkipKind.AlreadyThere, "Already in the right folder."); return; }
        if (DestinationProblem(destFolder, root) is { } destProblem) { Skip(run, facts.Entry, root, SkipKind.Protected, destProblem); return; }

        var desired = Path.Combine(destFolder, facts.Name);
        var shortDest = DisplayPaths.Short(destFolder, run.Roots);
        var clash = run.TakenNames.Contains(desired) || File.Exists(desired) || Directory.Exists(desired);
        string final = desired;
        bool renumbered = false;
        bool byType = RulePresets.IsByTypeRule(rule) || run.ByTypeList; // never a quarantine of an identical copy in a Sort by type list
        if (clash)
        {
            if (!run.TakenNames.Contains(desired) && IsIdenticalFile(facts, desired, ct))
            {
                // "Sort by type" never quarantines anything (review C1): an identical copy just stays where it is.
                if (byType) { Skip(run, facts.Entry, root, SkipKind.AlreadyThere, $"An identical copy is already in {shortDest}, so this one stays where it is."); return; }
                AddDuplicate(run, facts, rule, desired, why);
                return;
            }
            if (rule.ClashPolicy == NameClashPolicy.Skip)
            { Skip(run, facts.Entry, root, SkipKind.NameClash, $"A different file called \"{facts.Name}\" is already in {shortDest}."); return; }
            final = NextFreeName(run, desired);
            renumbered = true;
        }
        run.TakenNames.Add(final);

        var move = new PlanItem
        {
            Kind = ActionKind.MovePath,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Move \"{facts.Name}\" to {shortDest}" + (renumbered ? $" as \"{Path.GetFileName(final)}\"" : ""),
            SourcePath = facts.Path,
            DestinationPath = final,
            ExpectedSize = facts.Entry.Size,
            ExpectedLastWriteUtc = facts.Entry.LastWriteUtc,
            // Never overwrite (Overwrite is blocked by the validator).
            ClashPolicy = rule.ClashPolicy == NameClashPolicy.Skip ? NameClashPolicy.Skip : NameClashPolicy.AutoNumber,
            Args = RuleArgs(rule),
        };
        if (!SafetyAllows(run, facts, move)) return;

        // Moving a file out of OneDrive removes it from OneDrive: the user's call, never automatic.
        bool leavesCloud = LeavesCloud(facts.Path, destFolder);
        // "Sort by type": types that need a look (configs, MSI sources, fonts, game files outside Downloads) and files
        // you opened recently start unticked, with the reason (review S5/S9). Watch mode never runs them (S20).
        string? checkWhy = !byType ? null
            : facts.Sort is { CheckFirst: true } v ? v.Why
            : OpenedRecently(run, facts.Name) ? "You opened it recently, so an app may look for it here. Check first." : null;
        if (checkWhy is not null) why = why + " " + checkWhy;
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.Move,
            SourcePath = facts.Path,
            DestinationPath = final,
            Root = root,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Group = GroupFor(rule, destFolder, root),
            Size = facts.Entry.Size,
            LastWriteUtc = facts.Entry.LastWriteUtc,
            Renumbered = renumbered,
            Why = why,
            Reason = why + " " + MoveReason(destFolder, root, renumbered, leavesCloud),
            Badge = leavesCloud || checkWhy is not null ? Badge.Check : Badge.Safe,
            DefaultSelected = !leavesCloud && checkWhy is null,
            Automatic = rule.RunAutomatically && !leavesCloud && checkWhy is null,
            // "Sort by type" (owner: folders only when a file goes there): no separate "create folder" steps. The move step
            // creates its folder itself, records it for Undo, and removes it again if the move doesn't happen.
            Items = byType ? new[] { move } : MissingFolders(destFolder).Append(move).ToList(),
        });
    }

    /// <summary>
    /// "name.ext" has a "name.exe" next to it and is one of the files a program keeps beside itself (settings, libraries,
    /// data). Returns that program's name, or null.
    /// </summary>
    private static string? BelongsToProgramNextToIt(FileEntry e, IReadOnlyList<FileEntry> siblings)
    {
        var ext = OrganizeCategories.ExtensionOf(e.Name);
        if (ext is not ("ini" or "cfg" or "json" or "xml" or "config" or "conf" or "dll" or "dat" or "db" or "yaml" or "yml" or "toml" or "log" or "txt")) return null;
        var stem = Path.GetFileNameWithoutExtension(e.Name);
        return siblings.Where(s => !s.IsDirectory && s.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                   && Path.GetFileNameWithoutExtension(s.Name).Equals(stem, StringComparison.OrdinalIgnoreCase))
                       .Select(s => s.Name).FirstOrDefault();
    }

    /// <summary>Windows' Recent items list has a shortcut named after this file (name only: nothing is opened).</summary>
    private bool OpenedRecently(PlanRun run, string fileName)
    {
        if (_locations is not { } k || string.IsNullOrEmpty(k.RoamingAppData)) return false;
        if (run.Recent is null)
        {
            var recent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.Combine(k.RoamingAppData, "Microsoft", "Windows", "Recent");
                if (Directory.Exists(dir))
                    foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk")) recent.Add(Path.GetFileNameWithoutExtension(lnk));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            run.Recent = recent;
        }
        return run.Recent.Contains(fileName);
    }

    private void AddDuplicate(PlanRun run, FileFacts facts, OrganizeRule rule, string existing, string why)
    {
        var shortDest = DisplayPaths.Short(Path.GetDirectoryName(existing)!, run.Roots);
        var item = new PlanItem
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Quarantine \"{facts.Name}\" (an identical copy is already in {shortDest})",
            SourcePath = facts.Path,
            ExpectedSize = facts.Entry.Size,
            ExpectedLastWriteUtc = facts.Entry.LastWriteUtc,
        };
        if (!SafetyAllows(run, facts, item)) return;
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.DuplicateQuarantine,
            SourcePath = facts.Path,
            Root = facts.Root,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Group = "Duplicates",
            Size = facts.Entry.Size,
            LastWriteUtc = facts.Entry.LastWriteUtc,
            DuplicateOf = existing,
            Why = why,
            Reason = $"An identical copy (same size and content) is already in {shortDest}. Quarantine this one? You can restore it." + CloudQuarantineNote(facts),
            Badge = Badge.Check,
            DefaultSelected = false,
            Automatic = false,
            Items = new[] { item },
        });
    }

    private void PlanQuarantine(PlanRun run, FileFacts facts, OrganizeRule rule, string why)
    {
        var item = new PlanItem
        {
            Kind = ActionKind.QuarantinePath,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Quarantine \"{facts.Name}\" (restorable)",
            SourcePath = facts.Path,
            ExpectedSize = facts.Entry.Size,
            ExpectedLastWriteUtc = facts.Entry.LastWriteUtc,
        };
        if (!SafetyAllows(run, facts, item)) return;
        var cloudNote = CloudQuarantineNote(facts);
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.Quarantine,
            SourcePath = facts.Path,
            Root = facts.Root,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Group = "Quarantine",
            Size = facts.Entry.Size,
            LastWriteUtc = facts.Entry.LastWriteUtc,
            Why = why,
            Reason = why + " Your call: it goes to quarantine and can be restored." + cloudNote,
            Badge = Badge.Check,
            DefaultSelected = false,
            Automatic = rule.RunAutomatically && rule.AllowAutomaticQuarantine && cloudNote.Length == 0,
            Items = new[] { item },
        });
    }

    private void PlanRename(PlanRun run, FileFacts facts, OrganizeRule rule, string why)
    {
        var root = facts.Root;
        var ext = Path.GetExtension(facts.Name);
        var stem = Path.GetFileNameWithoutExtension(facts.Name);
        var newStem = RenameTokens.Apply(rule.Action.RenamePattern, stem, facts.FolderDate, run.Request.Culture);
        var newName = newStem + ext;
        if (WindowsNames.Problem(newName) is { } problem) { Skip(run, facts.Entry, root, SkipKind.RuleBlocked, problem); return; }
        if (string.Equals(newName, facts.Name, StringComparison.Ordinal)) { Skip(run, facts.Entry, root, SkipKind.AlreadyThere, "The name is already right."); return; }

        // Loop guard: if the renamed file would match this rule again and get yet another name, stop the rule.
        var renamedEntry = facts.Entry with { FullPath = Path.Combine(facts.Folder, newName), Name = newName };
        var again = new FileFacts(renamedEntry, root, ScanDepth.Quick);
        if (RuleEvaluator.Matches(rule.Conditions, again, run.NowUtc)
            && !string.Equals(RenameTokens.Apply(rule.Action.RenamePattern, newStem, facts.FolderDate, run.Request.Culture), newStem, StringComparison.Ordinal))
        {
            run.Blocked.Add(rule.Id);
            run.Notes.Add($"Rule \"{rule.Name}\" is paused: renamed files would match it again and be renamed forever.");
            Skip(run, facts.Entry, root, SkipKind.RuleBlocked, $"Rule \"{rule.Name}\" would rename it again and again, so it's paused.");
            return;
        }

        var desired = Path.Combine(facts.Folder, newName);
        bool caseOnly = string.Equals(desired, facts.Path, StringComparison.OrdinalIgnoreCase);
        string final = desired;
        bool renumbered = false;
        if (!caseOnly && (run.TakenNames.Contains(desired) || ((File.Exists(desired) || Directory.Exists(desired)) && !run.LeavingNames.Contains(desired))))
        {
            if (rule.ClashPolicy == NameClashPolicy.Skip)
            { Skip(run, facts.Entry, root, SkipKind.NameClash, $"\"{newName}\" is already taken."); return; }
            final = NextFreeName(run, desired);
            renumbered = true;
        }
        run.TakenNames.Add(final);
        run.LeavingNames.Add(facts.Path);

        var item = new PlanItem
        {
            Kind = ActionKind.RenamePath,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Rename \"{facts.Name}\" to \"{Path.GetFileName(final)}\"",
            SourcePath = facts.Path,
            DestinationPath = final,
            ExpectedSize = facts.Entry.Size,
            ExpectedLastWriteUtc = facts.Entry.LastWriteUtc,
            ClashPolicy = NameClashPolicy.Skip,
            Args = RuleArgs(rule),
        };
        if (!SafetyAllows(run, facts, item)) return;
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.Rename,
            SourcePath = facts.Path,
            DestinationPath = final,
            Root = root,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Group = "Renamed",
            Size = facts.Entry.Size,
            LastWriteUtc = facts.Entry.LastWriteUtc,
            Renumbered = renumbered,
            Why = why,
            Reason = why + " Stays in the same folder.",
            Automatic = rule.RunAutomatically,
            Items = new[] { item },
        });
    }

    private static Dictionary<string, string> RuleArgs(OrganizeRule rule) => new() { [OrganizeLedger.RuleIdArg] = rule.Id };

    /// <summary>Source inside a cloud folder, destination outside every cloud folder.</summary>
    private bool LeavesCloud(string source, string destFolder) => _safety.IsInCloudRoot(source) && !_safety.IsInCloudRoot(destFolder);

    private string CloudQuarantineNote(FileFacts facts)
        => _safety.IsInCloudRoot(facts.Path) ? " It's in OneDrive: quarantining removes it from OneDrive on all your devices." : "";

    private void PlanFolder(PlanRun run, FileEntry folder, string root, CancellationToken ct)
    {
        if (folder.IsReparsePoint) { Skip(run, folder, root, SkipKind.Link, "It's a link to somewhere else, so it's never moved."); return; }
        if (folder.IsSystem || folder.IsHidden) { Skip(run, folder, root, SkipKind.Hidden, "Hidden or system folder. Left alone."); return; }
        if (_safety.IsInsideProjectOrAppFolder(folder.FullPath, out _))
        { Skip(run, folder, root, SkipKind.ProjectOrApp, "Code project or app folder. Kept as one unit and left in place."); return; }

        // Read-only look inside: counts per category, newest change, any online-only files.
        var counts = new Dictionary<string, int>();
        int files = 0; long bytes = 0; DateTime newest = folder.LastTouchedUtc; bool cloud = false, tooBig = false, gameFiles = false;
        _walker.Walk(folder.FullPath, new WalkOptions { IncludeHidden = true, IncludeSystem = true }, e =>
        {
            if (files >= MaxFolderEntries) { tooBig = true; return false; }
            if (e.LastTouchedUtc > newest) newest = e.LastTouchedUtc;
            if (e.IsCloudPlaceholder) cloud = true;
            if (e.IsDirectory) return !e.IsReparsePoint;
            files++; bytes += e.Size;
            var ext = OrganizeCategories.ExtensionOf(e.Name);
            if (ext is "gsc" or "gscbin" or "csc" or "ff" or "ipak" or "iwd" or "gpc" or "gpj" or "pak" or "esp" or "esm") gameFiles = true;
            var cat = OrganizeCategories.All.FirstOrDefault(c => c.Extensions.Contains(ext));
            if (cat is not null) counts[cat.Id] = counts.GetValueOrDefault(cat.Id) + 1;
            return true;
        }, ct);

        if (cloud) { Skip(run, folder, root, SkipKind.OnlineOnly, "Has online-only OneDrive files, so it stays put."); return; }
        if (gameFiles) { Skip(run, folder, root, SkipKind.ProjectOrApp, "Has game mod or script files inside. Kept as one unit and left in place."); return; }
        if (tooBig) { Skip(run, folder, root, SkipKind.Folder, "Very large folder. Move it yourself if you want."); return; }
        if (run.NowUtc - newest < SkipRules.QuietPeriod) { Skip(run, folder, root, SkipKind.RecentlyChanged, "Something inside changed in the last 10 minutes."); return; }
        var top = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (files == 0 || top.Value * 2 <= files) { Skip(run, folder, root, SkipKind.Folder, "Mixed contents, so the folder stays put."); return; }

        var category = OrganizeCategories.Find(top.Key)!;
        var date = folder.LastWriteUtc.ToLocalTime();
        var parentFolder = DestinationResolver.ResolveFolder(new RuleAction { Destination = category.FolderName }, root, date, run.Request.Settings.UseDateFolders, run.Request.Culture)!;
        var desired = Path.Combine(parentFolder, folder.Name);
        var final = run.TakenNames.Contains(desired) || Directory.Exists(desired) || File.Exists(desired) ? NextFreeName(run, desired) : desired;
        run.TakenNames.Add(final);
        var shortDest = DisplayPaths.Short(parentFolder, run.Roots);

        var move = new PlanItem
        {
            Kind = ActionKind.MovePath,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Move folder \"{folder.Name}\" to {shortDest}",
            SourcePath = folder.FullPath,
            DestinationPath = final,
            IsDirectory = true,
            ExpectedSize = bytes,
            ExpectedLastWriteUtc = folder.LastWriteUtc,
            ClashPolicy = NameClashPolicy.AutoNumber,
            Args = new Dictionary<string, string> { [OrganizeLedger.RuleIdArg] = DefaultRules.CategoryRuleId(category.Id) },
        };
        var verdict = _safety.Check(move);
        if (!verdict.Allowed) { Skip(run, folder, root, SkipKind.Protected, verdict.Reason ?? "PC Tidy's safety check said no."); return; }
        bool leavesCloud = LeavesCloud(folder.FullPath, parentFolder);
        run.Actions.Add(new PlannedAction
        {
            Kind = PlannedActionKind.MoveFolder,
            SourcePath = folder.FullPath,
            DestinationPath = final,
            Root = root,
            RuleId = DefaultRules.CategoryRuleId(category.Id),
            RuleName = category.Id,
            Group = category.Id,
            Size = bytes,
            LastWriteUtc = folder.LastWriteUtc,
            IsDirectory = true,
            Renumbered = !string.Equals(final, desired, StringComparison.OrdinalIgnoreCase),
            Why = $"Mostly {category.Id.ToLowerInvariant()} ({top.Value} of {files} files).",
            Reason = $"Mostly {category.Id.ToLowerInvariant()} ({top.Value} of {files} files). Moves as one folder, never split. " + (leavesCloud ? MoveReason(parentFolder, root, false, true) : ""),
            Badge = leavesCloud ? Badge.Check : Badge.Safe,
            DefaultSelected = !leavesCloud,
            Automatic = false,
            Items = MissingFolders(parentFolder).Append(move).ToList(),
        });
    }

    // ---------------------------------------------------------------- helpers

    private bool SafetyAllows(PlanRun run, FileFacts facts, PlanItem item)
    {
        var verdict = _safety.Check(item);
        if (verdict.Allowed) return true;
        Skip(run, facts.Entry, facts.Root, SkipKind.Protected, verdict.Reason ?? "PC Tidy's safety check said no.");
        return false;
    }

    /// <summary>Runtime check of a (possibly token-expanded) destination outside the sorted folder.</summary>
    private string? DestinationProblem(string destFolder, string root)
    {
        if (SafetyFolders.ForbiddenReason(destFolder, _locations) is { } forbidden) return forbidden;
        // A type folder that is really a link to somewhere else is never filled (3.3 code review L1).
        if (PathUtil.IsUnder(destFolder, root) && SafetyFolders.LinkOnTheWay(destFolder) is { } link && !PathUtil.IsUnder(root, link))
            return $"\"{Path.GetFileName(link)}\" is a link to another folder, so PC Tidy doesn't move files into it.";
        if (PathUtil.IsUnder(destFolder, root)) return null;
        if (_safety.ClassifyPath(destFolder).Protection == PathProtection.Never) return "The rule's folder is protected. PC Tidy never moves files there.";
        if (IsAllowedByUser(destFolder)) return null;
        if (_safety.IsInCloudRoot(destFolder)) return "The rule's folder is in OneDrive. Allow it in Settings first.";
        if (_safety.IsInsideProjectOrAppFolder(destFolder, out _)) return "The rule's folder is inside a code project or app folder.";
        return null;
    }

    private bool IsIdenticalFile(FileFacts facts, string existing, CancellationToken ct)
    {
        try
        {
            var other = new FileInfo(existing);
            if (!other.Exists || other.Length != facts.Entry.Size) return false;
            if (facts.Entry.IsCloudPlaceholder || CloudFile.IsPlaceholder(other.Attributes)) return false;
            return string.Equals(FileHash.Full(facts.Path, ct), FileHash.Full(existing, ct), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"name.ext" → "name (2).ext"… skipping names on disk and names claimed earlier in this batch.</summary>
    private static string NextFreeName(PlanRun run, string desiredPath)
    {
        var dir = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (int i = 2; i < 100_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!run.TakenNames.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("No free name for " + desiredPath);
    }

    /// <summary>CreateDirectory items for every missing level, parents first (undo removes them only if empty).</summary>
    private static IEnumerable<PlanItem> MissingFolders(string folder)
    {
        var missing = new Stack<string>();
        for (var d = folder; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        return missing.Select(d => new PlanItem
        {
            Kind = ActionKind.CreateDirectory,
            FeatureId = OrganizeModule.FeatureId,
            Description = $"Create folder \"{Path.GetFileName(d)}\"",
            DestinationPath = d,
            IsDirectory = true,
        }).ToList();
    }

    private static string GroupFor(OrganizeRule rule, string destFolder, string root)
    {
        if (RulePresets.IsByTypeRule(rule)) return rule.Name;
        if (!PathUtil.IsUnder(destFolder, root)) return Path.GetFileName(destFolder);
        var first = Path.GetRelativePath(root, destFolder).Split('\\')[0];
        return first.Length > 0 ? first : rule.Name;
    }

    private static string MoveReason(string destFolder, string root, bool renumbered, bool leavesCloud)
    {
        if (leavesCloud)
            return "Careful: this moves it out of OneDrive, so OneDrive deletes its cloud copy on your other devices. Your call.";
        var where = PathUtil.IsUnder(destFolder, root) ? "Stays in the same folder tree, so it's instant." : "Goes to the folder your rule picked.";
        var clash = renumbered ? " A different file already has this name, so it gets a number." : "";
        return $"{where} Put back any time from History.{clash}";
    }
}
