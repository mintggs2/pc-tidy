using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>
/// Decides whether a path or plan item may be touched (Plan §6). Runs when a plan is built and again right
/// before each step, so every check reads the file system fresh (links, attributes) and the user's settings live.
/// Paths are judged exactly as the engine will use them (the literal \\?\ form: nothing trimmed, nothing guessed).
/// Order for a path: understandable → on this PC's drives → walked (long names, no links on the way, readable) →
/// carve-out bounds → not a system file → never-touched areas → other accounts → AI config →
/// (for removals) doesn't contain a protected area → ask-first.
/// Safe to call from many threads at once: all state is immutable or held in concurrent, size-capped caches.
/// </summary>
public sealed partial class SafetyGuard : ISafetyGuard
{
    private static readonly TimeSpan ParentCacheLifetime = TimeSpan.FromSeconds(30);
    private const int ParentCacheGeneration = 10_000;
    private const int SettingsCacheGeneration = 512;

    private readonly IKnownLocations _locations;
    private readonly AppSettings _settings;
    private readonly IServiceImageSource _serviceImages;
    private readonly object _mapLock = new();
    private readonly BoundedCache<string?> _settingsPaths = new(SettingsCacheGeneration, TimeSpan.FromMinutes(5));
    private readonly BoundedCache<WalkResult> _parents = new(ParentCacheGeneration, ParentCacheLifetime);
    private ProtectionMap _map;
    private int _mapGeneration;

    /// <param name="serviceImages">Where services' programs live (tests: a fake). Default: read-only from the registry.</param>
    public SafetyGuard(IKnownLocations locations, AppSettings settings, SafetyPolicy? policy = null, ProjectDetector? projects = null,
        IServiceImageSource? serviceImages = null)
    {
        _serviceImages = serviceImages ?? RegistryServiceImages.Instance;
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Policy = policy ?? SafetyPolicy.Default;
        Projects = projects ?? new ProjectDetector(locations);
        _map = new ProtectionMap(locations);
        _mapGeneration = (locations as WindowsKnownLocations)?.Generation ?? 0;
    }

    public SafetyPolicy Policy { get; }
    public ProjectDetector Projects { get; }

    /// <summary>Rebuilds the protected-area list (after <see cref="WindowsKnownLocations.Refresh"/> this happens automatically).</summary>
    public void Reload()
    {
        lock (_mapLock)
        {
            Volatile.Write(ref _map, new ProtectionMap(_locations));
            Volatile.Write(ref _mapGeneration, (_locations as WindowsKnownLocations)?.Generation ?? 0);
        }
        _parents.Clear();
        _settingsPaths.Clear();
        Projects.ClearCache();
    }

    private ProtectionMap Map
    {
        get
        {
            if (_locations is WindowsKnownLocations w && w.Generation != Volatile.Read(ref _mapGeneration)) Reload();
            return Volatile.Read(ref _map);
        }
    }

    // ------------------------------------------------------------------ ISafetyGuard

    public SafetyVerdict ClassifyPath(string path)
    {
        // Scanners call this for every item, so the parent folder's walk (long names, links) is cached briefly and
        // only the item itself is probed. Check() never uses this shortcut: it re-reads everything right before a change.
        // Odd last names (trailing dot, "nul") are classified like a quarantine source, which may handle them.
        var parsed = SafePath.Parse(path, allowOddLeaf: true);
        if (!parsed.Ok) return SafetyVerdict.Block(parsed.Problem!);
        var map = Map;
        if (!OnThisPcDriveLetter(map, parsed.Path!)) return SafetyVerdict.Block(Reasons.NotThisPc);
        return EvaluateLocated(map, LocateViaParentCache(parsed.Path!), PathUse.Remove, featureId: null, carveOut: null).Verdict;
    }

    /// <summary>The parent's walk comes from the cache; the item itself is always probed fresh.</summary>
    private Located LocateViaParentCache(string canonical)
    {
        if (SafePath.Parent(canonical) is not { } parentLexical) return Located.From(PathProbe.Walk(canonical));
        var parent = _parents.GetOrAdd(parentLexical, PathProbe.Walk);
        var leaf = SafePath.Leaf(canonical);
        var full = SafePath.Join(parent.Path, leaf);
        if (parent.LinkAt is not null) return new(full, PathFacts.Missing, parent.LinkAt, false);
        if (parent.Unreadable || !parent.Self.Exists) return new(full, PathFacts.Missing, null, parent.Unreadable);
        var (facts, longName, error) = PathProbe.ProbeOne(full);
        if (!facts.Exists) return new(full, PathFacts.Missing, null, !PathProbe.IsNotFound(error));
        var exact = SafePath.Join(parent.Path, longName ?? leaf);
        return new(exact, facts, facts.IsLink ? exact : null, false);
    }

    public bool IsInCloudRoot(string path)
    {
        var p = SafePath.TryNormalize(path);
        return p is not null && (Map.CloudRoots.Any(c => SafePath.IsSameOrUnder(p, c)) || Projects.FindSyncthingRootNormalized(p) is not null);
    }

    /// <summary>True if the path is inside a folder the user excluded from scans (whole-folder match: "Projects" never matches "Projects2").</summary>
    public bool IsExcludedFromScans(string path)
    {
        var p = SafePath.TryNormalize(path);
        return p is not null && _settings.ScanExclusions.ToArray().Select(NormalizeSetting).OfType<string>().Any(e => SafePath.IsSameOrUnder(p, e));
    }

    /// <summary>
    /// The cloud/sync folder holding <paramref name="path"/> (strictly inside it), or null. Covers detected roots
    /// (OneDrive, Dropbox, Google Drive, Syncthing's config) and any folder marked with Syncthing's ".stfolder".
    /// </summary>
    private string? CloudRootOf(ProtectionMap map, string path)
        => map.CloudRoots.FirstOrDefault(c => SafePath.IsStrictlyUnder(path, c))
           ?? (SafePath.Parent(path) is { } parent ? Projects.FindSyncthingRootNormalized(parent) : null);

    private bool IsAllowedAskFirst(string path)
        => _settings.AllowedAskFirstFolders.ToArray().Select(NormalizeSetting).OfType<string>().Any(a => SafePath.IsSameOrUnder(path, a));

    public bool IsInsideProjectOrAppFolder(string path, out string? why)
    {
        why = null;
        var p = SafePath.TryNormalize(path);
        if (p is null) return false;
        var start = PathProbe.Inspect(p).IsDirectory ? p : SafePath.Parent(p);
        return start is not null && Projects.FindProjectRootNormalized(start, out why) is not null;
    }

    // ------------------------------------------------------------------ path evaluation

    private enum PathUse
    {
        /// <summary>The item itself is moved, renamed, zipped or quarantined.</summary>
        Remove,
        /// <summary>Something is created or written here.</summary>
        Target,
        /// <summary>Only read (e.g. a zip being extracted).</summary>
        Read,
    }

    private readonly record struct PathResult(SafetyVerdict Verdict, string? Path, PathFacts Facts)
    {
        public bool Blocked => !Verdict.Allowed;
    }

    /// <summary>Where a path really is: canonical spelling plus what the walk to it found.</summary>
    private readonly record struct Located(string Path, PathFacts Facts, string? LinkAt, bool Unreadable)
    {
        public static Located From(WalkResult w) => new(w.Path, w.Self, w.LinkAt, w.Unreadable);
    }

    /// <param name="oddLeafOk">
    /// True only for rename and quarantine sources: their last segment may end in a dot or space, or be a reserved
    /// name like "nul", so such names can be fixed or cleaned. Every other rule still applies to the literal path.
    /// </param>
    private PathResult Evaluate(string? raw, PathUse use, string? featureId, CarveOut? carveOut, bool oddLeafOk = false)
    {
        var parsed = SafePath.Parse(raw, oddLeafOk);
        if (!parsed.Ok) return Fail(parsed.Problem!);
        var map = Map;
        // Cheap test before touching the disk, so a mapped network drive is never probed.
        if (!OnThisPcDriveLetter(map, parsed.Path!)) return Fail(Reasons.NotThisPc, parsed.Path);
        return EvaluateLocated(map, Located.From(PathProbe.Walk(parsed.Path!)), use, featureId, carveOut);
    }

    private PathResult EvaluateLocated(ProtectionMap map, Located at, PathUse use, string? featureId, CarveOut? carveOut)
    {
        var path = at.Path;
        var facts = at.Facts;
        if (!map.Drives.Any(d => SafePath.IsSameOrUnder(path, d))) return Fail(Reasons.NotThisPc, path);

        string? carveOutRoot = null;
        if (carveOut is not null)
        {
            var bound = CarveOutBound(carveOut, path, facts);
            if (bound.Problem is not null) return Fail(bound.Problem, path, facts);
            carveOutRoot = bound.Root;
        }

        if (at.LinkAt is not null) return Fail(Reasons.ThroughLink, path, facts);
        if (at.Unreadable) return Fail(Reasons.CantCheck, path, facts);
        if (facts.IsSystem) return Fail(Reasons.SystemAttribute, path, facts);
        if (IsSyncInternals(path)) return Fail(Reasons.SyncInternals, path, facts);

        foreach (var extra in SettingsAreas())
            if (extra.Covers(path)) return Fail(extra.Reason, path, facts);

        var carveOutRootIsProtected = false;
        foreach (var area in map.Areas)
        {
            if (!area.Covers(path)) continue;
            // A carve-out opens a protected area only when its own root lies inside that area
            // (Windows ⊇ Windows\Temp), never for a protected folder that happens to sit inside the root.
            if (carveOutRoot is not null && area.CarveOutsMayOpen && SafePath.IsSameOrUnder(carveOutRoot, area.Path))
            {
                carveOutRootIsProtected = true;
                continue;
            }
            return Fail(area.Reason, path, facts);
        }

        var inPublic = false;
        if (OtherAccountReason(map, path, featureId, out inPublic) is { } otherAccount) return Fail(otherAccount, path, facts);

        if (IsAiConfig(map, path)) return Fail(Reasons.AiConfig, path, facts);

        if (use == PathUse.Remove)
        {
            var inside = map.Areas.Concat(SettingsAreas()).FirstOrDefault(a => SafePath.IsStrictlyUnder(a.Path, path));
            if (inside is not null) return Fail(Reasons.Contains(inside.Label), path, facts);
            if (map.AiConfigDirs.Any(d => SafePath.IsStrictlyUnder(d, path))) return Fail(Reasons.Contains("your AI assistant's settings"), path, facts);
            // A Syncthing folder itself (it holds .stfolder) stays put, like any cloud folder root.
            if (facts.IsDirectory && Projects.FindSyncthingRootNormalized(path) is { } st && SafePath.SameAs(st, path))
                return Fail(Reasons.CloudRoot, path, facts);
        }

        if (use == PathUse.Read) return new(SafetyVerdict.Ok, path, facts);
        // Project/app detection stays on for carve-outs in ordinary places (Downloads, AppData caches): a carve-out only
        // narrows what's allowed there. It's off only where the carve-out opened a protected area (Windows\Temp, Steam's
        // caches), whose folders "look like apps" by nature. The carve-out's own folder ends the upward walk.
        var checkProjects = carveOutRoot is null || !carveOutRootIsProtected;
        return new(AskFirst(map, path, featureId, checkProjects, projectBoundary: carveOutRoot, inPublic), path, facts);
    }

    private static PathResult Fail(string reason, string? path = null, PathFacts facts = default)
        => new(SafetyVerdict.Block(reason), path, facts);

    private static bool OnThisPcDriveLetter(ProtectionMap map, string canonical)
        => map.Drives.Any(d => char.ToUpperInvariant(d[0]) == char.ToUpperInvariant(canonical[0]));

    /// <summary>
    /// A carve-out item may touch only paths strictly inside one of its roots, or its listed single files; never a
    /// denied name below a root; and, for extension-limited carve-outs (old installers), only files of those types.
    /// Returns the root (or file) that admits the path, or the reason it's outside.
    /// </summary>
    private (string? Root, string? Problem) CarveOutBound(CarveOut carveOut, string path, PathFacts facts)
    {
        foreach (var root in SafeRoots(carveOut))
        {
            if (SafePath.SameAs(path, root)) return (null, Reasons.CarveOutFolderItself(carveOut.Title));
            if (!SafePath.IsStrictlyUnder(path, root)) continue;
            // Logins, saved data and settings inside an allowed folder stay off-limits.
            if (carveOut.IsDenied(path[root.Length..])) return (null, Reasons.CarveOutDeniedName(carveOut.Title));
            if (carveOut.AllowedExtensions is { Count: > 0 })
            {
                if (facts.IsDirectory) return (null, Reasons.CarveOutFilesOnly(carveOut.Title));
                if (carveOut.ExtensionNotAllowed(path)) return (null, Reasons.CarveOutFileType(carveOut.Title));
            }
            return (root, null);
        }
        foreach (var (id, file) in CarveOuts.AllowedSingleFiles(_locations))
        {
            if (id != carveOut.Id) continue;
            var f = SafePath.TryNormalize(file);
            if (f is not null && SafePath.SameAs(path, f)) return (f, null);
        }
        return (null, Reasons.OutsideCarveOut(carveOut.Title));
    }

    private IEnumerable<string> SafeRoots(CarveOut carveOut)
    {
        IEnumerable<string> roots;
        try { roots = carveOut.Roots(_locations).ToList(); }
        catch { return Array.Empty<string>(); }
        return roots.Select(NormalizeSetting).OfType<string>().Where(r => r.Length > 3);
    }

    /// <summary>
    /// Everything in the Users folder except your own profile belongs to another account (or to Windows' shared
    /// profiles: Default, All Users): never touched. The one exception is Public, which the features in
    /// <see cref="SafetyPolicy.PublicFolderFeatures"/> may organize as an ask-first place (and ClassifyPath reports as ask-first).
    /// </summary>
    private string? OtherAccountReason(ProtectionMap map, string path, string? featureId, out bool inPublic)
    {
        inPublic = false;
        if (map.UsersFolder is not { } users || !SafePath.IsStrictlyUnder(path, users)) return null;
        if (map.OwnProfile is { } own && SafePath.IsSameOrUnder(path, own)) return null;
        var below = path[(users.EndsWith('\\') ? users.Length : users.Length + 1)..];
        var cut = below.IndexOf('\\');
        var account = cut < 0 ? below : below[..cut];
        if (account.Equals("Public", StringComparison.OrdinalIgnoreCase) && cut > 0
            && (featureId is null || Policy.PublicFolderFeatures.Contains(featureId)))
        {
            inPublic = true;
            return null;
        }
        return Reasons.OtherAccount;
    }

    private static readonly HashSet<string> SyncInternalNames = new(StringComparer.OrdinalIgnoreCase)
        { ".stfolder", ".stversions", ".stignore", ".dropbox", ".dropbox.cache" };

    /// <summary>Sync tools' own bookkeeping (Syncthing's .stfolder/.stversions, Dropbox's .dropbox.cache): never touched.</summary>
    private static bool IsSyncInternals(string path)
        => path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(SyncInternalNames.Contains);

    /// <summary>.claude / .codex: only chat transcripts may be touched; settings, logins and memory never.</summary>
    private static bool IsAiConfig(ProtectionMap map, string path)
    {
        // ~\.claude.json, ~\.claude.json.backup, ~\.claude.json.backup.1234...: Claude Code's main config.
        if (map.ClaudeJson is { } json && path.StartsWith(json, StringComparison.OrdinalIgnoreCase)
            && (path.Length == json.Length || path[json.Length] == '.'))
            return true;
        foreach (var claude in new[] { map.ClaudeDir, map.DefaultClaudeDir })
            if (claude is not null && SafePath.IsSameOrUnder(path, claude))
                return !IsTranscript(path, claude, "projects", ".jsonl");
        foreach (var codex in new[] { map.CodexDir, map.DefaultCodexDir })
            if (codex is not null && SafePath.IsSameOrUnder(path, codex))
                return !IsTranscript(path, codex, "sessions", ".jsonl", ".jsonl.zst");
        return false;
    }

    private static bool IsTranscript(string path, string configDir, string folder, params string[] extensions)
    {
        var transcripts = Path.Combine(configDir, folder);
        return SafePath.IsStrictlyUnder(path, transcripts)
               && extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Areas that come from the user's live settings (extra protected folders, the configured data folder).</summary>
    private IEnumerable<ProtectedArea> SettingsAreas()
    {
        foreach (var folder in _settings.ExtraProtectedFolders.ToArray())
            if (NormalizeSetting(folder) is { } p) yield return new ProtectedArea(p, AreaScope.Subtree, "a folder you protected", Reasons.ExtraProtected);
        foreach (var folder in _settings.ScanExclusions.ToArray())
            if (NormalizeSetting(folder) is { } p) yield return new ProtectedArea(p, AreaScope.Subtree, "a folder you excluded", Reasons.ScanExcluded);
        if (NormalizeSetting(_settings.DataRoot) is { } data) yield return new ProtectedArea(data, AreaScope.Subtree, "PC Tidy's data", Reasons.DataRoot);
    }

    /// <summary>Settings and carve-out roots, normalised once and remembered for a few minutes (they change rarely).</summary>
    private string? NormalizeSetting(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : _settingsPaths.GetOrAdd(raw, SafePath.TryNormalize);

    /// <summary>Build output folders that tools recreate (Developer cleanup may quarantine these inside a project, nothing else).</summary>
    private static readonly HashSet<string> RebuildableFolderNames = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", ".vs", "target", "__pycache__", ".venv", "build" };

    /// <summary>
    /// Developer cleanup only: the path is a rebuildable folder (or inside one) within the project — e.g.
    /// <c>MyApp\node_modules</c> or <c>MyApp\src\obj\Debug</c>. Source files and everything else in the project stay ask-first.
    /// </summary>
    private static bool IsRebuildableInsideProject(string? featureId, string projectRoot, string path)
    {
        if (!string.Equals(featureId, "developer", StringComparison.OrdinalIgnoreCase)) return false;
        if (!SafePath.IsStrictlyUnder(path, projectRoot)) return false;
        var relative = path[projectRoot.Length..].TrimStart('\\');
        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // Never .git or anything inside it, even if a folder below it is called "build".
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) return false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!RebuildableFolderNames.Contains(segments[i])) continue;
            var folder = Path.Combine(projectRoot, string.Join('\\', segments[..(i + 1)]));
            // The matching name must be a real folder (not a file called "build") with build-output evidence.
            if (Directory.Exists(@"\\?\" + folder)) { if (HasBuildEvidence(segments[i], folder)) return true; continue; }
            // Not there at all: only putting back what PC Tidy quarantined from here (Undo) can target it, and that
            // can't remove or overwrite anything in the project (a quarantine of a missing path fails earlier).
            if (!File.Exists(@"\\?\" + folder) && !Directory.Exists(@"\\?\" + folder)) return true;
        }
        return false;
    }

    /// <summary>Proof that a folder is build output its tool recreates, not a hand-made folder with the same name.</summary>
    private static bool HasBuildEvidence(string name, string folder)
    {
        static bool Any(string dir, params string[] patterns)
        {
            try { return patterns.Any(p => Directory.EnumerateFiles(@"\\?\" + dir, p).Any()); }
            catch { return false; }
        }
        var parent = Path.GetDirectoryName(folder);
        if (parent is null) return false;
        return name.ToLowerInvariant() switch
        {
            "node_modules" => Any(parent, "package.json"),
            "bin" or "obj" => Any(parent, "*.csproj", "*.fsproj", "*.vbproj"),
            ".vs" => Any(parent, "*.sln", "*.slnx"),
            "target" => Any(parent, "Cargo.toml") || Any(folder, "CACHEDIR.TAG"),
            "__pycache__" => Any(folder, "*.pyc"),
            ".venv" => Any(folder, "pyvenv.cfg"),
            "build" => Any(parent, "build.gradle", "build.gradle.kts", "CMakeLists.txt", "package.json"),
            _ => false,
        };
    }

    /// <param name="checkProjects">False for carve-outs that open a protected area (their folders look like apps by nature).</param>
    /// <param name="projectBoundary">A carve-out's own folder: the project walk stops below it.</param>
    /// <param name="inPublic">Inside the shared Public profile: ask-first until the user allows the folder.</param>
    private SafetyVerdict AskFirst(ProtectionMap map, string path, string? featureId, bool checkProjects, string? projectBoundary, bool inPublic)
    {
        var featureAllows = featureId is null ? AskFirstCategory.None : Policy.AllowedFor(featureId);
        var cloudRoot = CloudRootOf(map, path);
        var library = map.Libraries.FirstOrDefault(l => SafePath.IsStrictlyUnder(path, l.Path));
        string? projectRoot = null, why = null;
        if (checkProjects && SafePath.Parent(path) is { } parent)
            projectRoot = Projects.FindProjectRootNormalized(parent, out why, projectBoundary);

        if (cloudRoot is null && library.Path is null && projectRoot is null && !inPublic) return SafetyVerdict.Ok;

        string? reason = null;
        if (cloudRoot is not null && !featureAllows.HasFlag(AskFirstCategory.CloudFolder)) reason = Reasons.Cloud;
        if (reason is null && library.Path is not null && !featureAllows.HasFlag(AskFirstCategory.UserLibrary)) reason = Reasons.Library(library.Name);
        if (reason is null && projectRoot is not null && !featureAllows.HasFlag(AskFirstCategory.ProjectOrApp)
            && !IsRebuildableInsideProject(featureId, projectRoot, path))
            reason = Reasons.Project(projectRoot, why!);
        if (reason is null && inPublic) reason = Reasons.PublicFolder;

        if (reason is null || IsAllowedAskFirst(path)) return new SafetyVerdict(true, null, PathProtection.AskFirst);
        return SafetyVerdict.Block(reason, PathProtection.AskFirst);
    }
}
