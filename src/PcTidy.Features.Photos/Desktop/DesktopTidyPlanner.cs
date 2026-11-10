using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;
using PcTidy.Features.Photos.Shared;

namespace PcTidy.Features.Photos.Desktop;

public enum DesktopTidyMode
{
    /// <summary>Loose files untouched for N days → Desktop\Old desktop\{year}-{month}.</summary>
    MoveOldClutter,
    /// <summary>Loose files → category folders on the Desktop (Pictures, Documents…), like Fences but as real folders.</summary>
    GroupByType,
}

/// <summary>What Desktop tidy remembers. Stored with <c>AppSettings.SetFeature("desktoptidy", …)</c>; the shell saves.</summary>
public sealed class DesktopTidySettings
{
    public const string FeatureKey = "desktoptidy";
    public const int DefaultOldAfterDays = 30;

    public DesktopTidyMode Mode { get; set; } = DesktopTidyMode.MoveOldClutter;
    public int OldAfterDays { get; set; } = DefaultOldAfterDays;
    /// <summary>Shortcuts are never moved unless the user ticks this.</summary>
    public bool IncludeShortcuts { get; set; }

    public static DesktopTidySettings Load(AppSettings settings)
    {
        DesktopTidySettings? stored = null;
        try { stored = settings.GetFeature<DesktopTidySettings>(FeatureKey); }
        catch (System.Text.Json.JsonException) { /* corrupt blob: defaults */ }
        stored ??= new DesktopTidySettings();
        stored.OldAfterDays = Math.Clamp(stored.OldAfterDays, 1, 3650);
        return stored;
    }

    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}

public sealed record DesktopTidyRequest
{
    public required string Desktop { get; init; }
    public DesktopTidyMode Mode { get; init; } = DesktopTidyMode.MoveOldClutter;
    public int OldAfterDays { get; init; } = DesktopTidySettings.DefaultOldAfterDays;
    public bool IncludeShortcuts { get; init; }
    /// <summary>Deep also opens .exe files to tell old installers (which may move) from programs (which stay).</summary>
    public ScanDepth Depth { get; init; } = ScanDepth.Quick;
    public bool CheckInUse { get; init; } = true;

    public static DesktopTidyRequest From(DesktopTidySettings s, string desktop, ScanDepth depth) => new()
    {
        Desktop = desktop, Mode = s.Mode, OldAfterDays = s.OldAfterDays, IncludeShortcuts = s.IncludeShortcuts, Depth = depth,
    };
}

/// <summary>Desktop tidy's preview: Organize's shape (so the shared before → after tree draws it) plus the OneDrive state.</summary>
public sealed class DesktopTidyPreview
{
    public required string Desktop { get; init; }
    public required OrganizePreview Preview { get; init; }
    public required FolderAccess Access { get; init; }
    public bool NeedsPermission => Access == FolderAccess.NeedsPermission;
}

/// <summary>
/// Plans a Desktop tidy. READ-ONLY. Only loose files move, and only into folders on the Desktop itself, so a
/// OneDrive-redirected Desktop stays inside the same OneDrive folder (OneDrive renames, it doesn't re-upload).
/// Never moved: shortcuts (unless the user says so), desktop.ini / thumbs.db, hidden or system files, links,
/// online-only files, unfinished downloads, anything changed in the last 10 minutes, files open in an app, folders.
/// </summary>
public sealed class DesktopTidyPlanner
{
    public const string OldDesktopFolder = "Old desktop";
    public const string ShortcutsFolder = "Shortcuts";
    public const string PermissionReason = "Your Desktop is in OneDrive. Allow PC Tidy to tidy it first (see the note at the top).";

    private static readonly IReadOnlySet<string> ShellFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db" };

    private readonly ISafetyGuard _safety;
    private readonly IInUseChecker _inUse;
    private readonly IFileWalker _walker;
    private readonly AppSettings _settings;
    private readonly IClock _clock;

    public DesktopTidyPlanner(ISafetyGuard safety, IInUseChecker inUse, IFileWalker walker, AppSettings settings, IClock? clock = null)
    {
        _safety = safety;
        _inUse = inUse;
        _walker = walker;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
    }

    public Task<DesktopTidyPreview> PlanAsync(DesktopTidyRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => PlanCoreAsync(request, progress, ct), ct);

    private async Task<DesktopTidyPreview> PlanCoreAsync(DesktopTidyRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = PathUtil.Normalize(request.Desktop);
        var name = Path.GetFileName(root);
        var nowUtc = _clock.UtcNow;
        var actions = new List<PlannedAction>();
        var skipped = new List<SkippedEntry>();
        var notes = new List<string>();
        var summaries = new List<RootSummary>();
        var access = CloudFolders.AccessFor(root, _safety, _settings);

        DesktopTidyPreview Result() => new()
        {
            Desktop = root,
            Access = access,
            Preview = new OrganizePreview
            {
                Roots = summaries, Actions = actions, Skipped = skipped, Notes = notes.Distinct().ToList(),
                Problems = Array.Empty<RuleProblem>(), Depth = request.Depth, Duration = sw.Elapsed,
            },
        };

        if (!Directory.Exists(root))
        {
            summaries.Add(new RootSummary(root, name, 0, 0, 0, "Folder not found."));
            notes.Add("Your Desktop folder wasn't found, so there's nothing to tidy.");
            return Result();
        }
        if (RootProblem(root) is { } problem)
        {
            summaries.Add(new RootSummary(root, name, 0, 0, 0, problem));
            notes.Add($"{name}: {problem}");
            return Result();
        }

        progress?.Report(new ScanProgress("Looking at your Desktop…"));
        var entries = new List<FileEntry>();
        try
        {
            _walker.Walk(root, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = true }, e => { entries.Add(e); return false; }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summaries.Add(new RootSummary(root, name, 0, 0, 0, "Couldn't open this folder."));
            return Result();
        }
        var files = entries.Where(e => !e.IsDirectory).ToList();
        var folders = entries.Where(e => e.IsDirectory).ToList();
        summaries.Add(new RootSummary(root, name, files.Count, folders.Count, files.Sum(f => f.Size), null));
        void Skip(FileEntry e, SkipKind kind, string reason) => skipped.Add(new SkippedEntry(e.FullPath, root, e.IsDirectory, kind, reason, e.Size));

        // 1) Entry checks and where each file would go (no Restart Manager yet: it's the slowest step).
        var planned = new List<(FileEntry Entry, string Folder, string Group, string Why)>();
        foreach (var e in files)
        {
            ct.ThrowIfCancellationRequested();
            if (EntryProblem(e, nowUtc, request.IncludeShortcuts) is { } skip) { Skip(e, skip.Kind, skip.Reason); continue; }
            try
            {
                if (Destination(e, root, nowUtc, request) is { } dest) planned.Add((e, dest.Folder, dest.Group, dest.Why));
                else if (Leave(e, nowUtc, request) is { } leave) Skip(e, leave.Kind, leave.Reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(e, SkipKind.Unreadable, "Couldn't read this file right now.");
            }
        }

        // 2) Files held open by an app stay put (the preview says which app).
        var busy = request.CheckInUse ? await InUseAsync(planned.Select(p => p.Entry.FullPath).ToList(), ct).ConfigureAwait(false) : new();
        var names = new DestinationNames();
        bool cloud = access != FolderAccess.Local;
        foreach (var (e, folder, group, why) in planned)
        {
            ct.ThrowIfCancellationRequested();
            if (busy.TryGetValue(e.FullPath, out var apps)) { Skip(e, SkipKind.InUse, apps.Count > 0 ? $"Open in {string.Join(", ", apps)} right now." : "In use right now."); continue; }
            var final = names.Claim(Path.Combine(folder, e.Name), out var renumbered);
            var shortDest = DisplayPaths.Short(folder, new[] { root });
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
            var verdict = _safety.Check(move);
            bool waiting = !verdict.Allowed && access == FolderAccess.NeedsPermission && verdict.Protection == PathProtection.AskFirst;
            if (!verdict.Allowed && !waiting) { Skip(e, SkipKind.Protected, verdict.Reason ?? "PC Tidy's safety check said no."); continue; }

            var reason = $"{why}, so it goes to {shortDest}. "
                         + (cloud ? "It stays inside your OneDrive Desktop, so OneDrive only renames it: nothing is uploaded again." : "It stays on your Desktop drive, so it's instant.")
                         + " Put back any time from History."
                         + (SkipRules.ShortcutExtensions.Contains(OrganizeCategories.ExtensionOf(e.Name)) ? " The shortcut still works from its new folder." : "")
                         + (renumbered ? " A different file already has this name there, so it gets a number." : "");
            actions.Add(new PlannedAction
            {
                Kind = PlannedActionKind.Move,
                SourcePath = e.FullPath,
                DestinationPath = final,
                Root = root,
                RuleId = "desktoptidy." + request.Mode,
                RuleName = request.Mode == DesktopTidyMode.MoveOldClutter ? "Old clutter" : "Group by type",
                Group = group,
                Size = e.Size,
                LastWriteUtc = e.LastWriteUtc,
                Renumbered = renumbered,
                Why = why,
                Reason = reason,
                Badge = Badge.Safe,
                DefaultSelected = !waiting,
                Items = PlanFolders.Missing(folder).Append(move).ToList(),
            });
        }

        // 3) Folders always stay put: only loose files are tidied.
        foreach (var f in folders)
        {
            bool ours = f.Name.Equals(OldDesktopFolder, StringComparison.OrdinalIgnoreCase) || f.Name.Equals(ShortcutsFolder, StringComparison.OrdinalIgnoreCase)
                        || OrganizeCategories.FolderNames.Contains(f.Name);
            Skip(f, ours ? SkipKind.AlreadySorted : SkipKind.Folder, ours ? "PC Tidy's tidy folder." : "Folders stay put. Only loose files are tidied.");
        }

        // No note for "asks first": the page shows one OneDrive bar with the Allow button, said once.
        return Result();
    }

    /// <summary>Why nothing on this Desktop may be tidied at all, or null.</summary>
    private string? RootProblem(string root)
    {
        if (PathExclusions.IsExcluded(root, _settings.ScanExclusions)) return "You excluded this folder from scans.";
        if (PathExclusions.IsExcluded(root, _settings.ExtraProtectedFolders)) return "You protected this folder.";
        // Ask about the Desktop's contents: the Desktop folder itself always "stays where it is".
        if (_safety.ClassifyPath(Path.Combine(root, "pc-tidy-probe.tmp")).Protection == PathProtection.Never) return "Protected folder. PC Tidy never changes it.";
        return null;
    }

    /// <summary>
    /// Plan §9 "never moved", in Organize's order, except that shortcuts may move when the user ticked
    /// "include shortcuts".
    /// </summary>
    private (SkipKind Kind, string Reason)? EntryProblem(FileEntry e, DateTime nowUtc, bool includeShortcuts)
    {
        var ext = OrganizeCategories.ExtensionOf(e.Name);
        bool shortcut = SkipRules.ShortcutExtensions.Contains(ext);
        if (e.IsReparsePoint) return (SkipKind.Link, "It's a link to somewhere else, so it's never moved.");
        if (shortcut && !includeShortcuts) return (SkipKind.Shortcut, "Shortcuts stay on your Desktop. Tick \"Include shortcuts\" to tidy them too.");
        if (ShellFiles.Contains(e.Name)) return (SkipKind.SystemFile, "Windows uses this file for the Desktop's look.");
        if (e.IsSystem) return (SkipKind.SystemFile, "System file. Never moved.");
        if (e.IsHidden) return (SkipKind.Hidden, "Hidden file. Left alone.");
        if (e.IsCloudPlaceholder) return (SkipKind.OnlineOnly, "Online-only OneDrive file. Never downloaded or moved.");
        if (SkipRules.PartialDownloadExtensions.Contains(ext) || e.Name.StartsWith("~$", StringComparison.Ordinal) || e.Name.StartsWith(".~lock", StringComparison.OrdinalIgnoreCase))
            return (SkipKind.StillDownloading, "Still downloading or a temporary file.");
        if (nowUtc - e.LastTouchedUtc < SkipRules.QuietPeriod) return (SkipKind.RecentlyChanged, "Changed in the last 10 minutes. It may still be in use.");
        if (PathExclusions.IsExcluded(e.FullPath, _settings.ScanExclusions)) return (SkipKind.Protected, "You excluded this from scans.");
        if (PathExclusions.IsExcluded(e.FullPath, _settings.ExtraProtectedFolders)) return (SkipKind.Protected, "You protected this.");
        if (_safety.ClassifyPath(e.FullPath).Protection == PathProtection.Never) return (SkipKind.Protected, "Protected file. Never moved.");
        return null;
    }

    /// <summary>Folder (inside the Desktop) this file goes to, its group label and the plain-English why; null = it stays.</summary>
    private static (string Folder, string Group, string Why)? Destination(FileEntry e, string root, DateTime nowUtc, DesktopTidyRequest request)
    {
        var ext = OrganizeCategories.ExtensionOf(e.Name);
        bool shortcut = SkipRules.ShortcutExtensions.Contains(ext);
        bool installer = ext == "exe" && request.Depth == ScanDepth.Deep && new FileFacts(e, root, request.Depth).Installer.IsInstaller;
        if (ext == "exe" && !installer) return null;

        if (request.Mode == DesktopTidyMode.MoveOldClutter)
        {
            var days = (int)(nowUtc - e.LastTouchedUtc).TotalDays;
            if (days < request.OldAfterDays) return null;
            var month = e.LastTouchedUtc.ToLocalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            return (Path.Combine(root, OldDesktopFolder, month), $"{OldDesktopFolder}\\{month}", $"Untouched for {days:N0} days");
        }

        if (shortcut) return (Path.Combine(root, ShortcutsFolder), ShortcutsFolder, "Shortcut");
        if (installer) return (Path.Combine(root, OrganizeCategories.Find(OrganizeCategories.Installers)!.FolderName), OrganizeCategories.Installers, "Installer");
        var category = OrganizeCategories.All.FirstOrDefault(c => c.Extensions.Contains(ext));
        if (category is null) return null;
        return (Path.Combine(root, category.FolderName), category.Id, $"{Singular(category.Id)} file");
    }

    /// <summary>Why a file that passed the "never moved" checks still stays, for the preview.</summary>
    private static (SkipKind Kind, string Reason)? Leave(FileEntry e, DateTime nowUtc, DesktopTidyRequest request)
    {
        var ext = OrganizeCategories.ExtensionOf(e.Name);
        if (ext == "exe")
            return request.Depth == ScanDepth.Deep
                ? (SkipKind.PortableApp, "Looks like a program, not an installer, so it stays where it is.")
                : (SkipKind.PortableApp, "Programs stay put, so their shortcuts keep working. A Deep scan checks whether it's just an old installer.");
        if (request.Mode == DesktopTidyMode.MoveOldClutter)
        {
            var days = (int)(nowUtc - e.LastTouchedUtc).TotalDays;
            var when = days switch { 0 => "today", 1 => "yesterday", _ => $"{days:N0} days ago" };
            return (SkipKind.LeaveInPlace, $"Changed {when}. Only files untouched for {request.OldAfterDays:N0} days move.");
        }
        return (SkipKind.LeaveInPlace, "Other file types stay where they are.");
    }

    private static string Singular(string category) => category switch
    {
        OrganizeCategories.Pictures => "Picture",
        OrganizeCategories.Videos => "Video",
        OrganizeCategories.Documents => "Document",
        OrganizeCategories.Archives => "Archive",
        OrganizeCategories.Installers => "Installer",
        _ => category,
    };

    private async Task<Dictionary<string, IReadOnlyList<string>>> InUseAsync(IReadOnlyList<string> paths, CancellationToken ct)
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
}
