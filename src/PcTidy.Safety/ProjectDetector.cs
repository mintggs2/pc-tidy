using System.IO.Enumeration;
using PcTidy.Core;
using PcTidy.Safety.Paths;

namespace PcTidy.Safety;

/// <summary>
/// Spots code projects and app/game folders, which PC Tidy treats as one unit: never organized or cleaned inside.
/// Markers: .git, .svn, .hg, package.json, *.sln, *.csproj, pyproject.toml, Cargo.toml, go.mod, node_modules,
/// .venv, steamapps, or an .exe next to .dll files. Only folder listings are read (names, never contents),
/// so OneDrive online-only files are never downloaded. Also finds Syncthing folders (".stfolder"). Results are cached briefly per folder.
/// </summary>
public sealed class ProjectDetector
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    /// <summary>Folder listings remembered at most (two generations of this many; see BoundedCache).</summary>
    private const int CacheGenerationSize = 25_000;

    private static readonly Dictionary<string, string> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".git"] = "It has a .git folder, so it's a code project.",
        [".svn"] = "It has a .svn folder, so it's a code project.",
        [".hg"] = "It has a .hg folder, so it's a code project.",
        ["package.json"] = "It has a package.json, so it's a code project.",
        ["pyproject.toml"] = "It has a pyproject.toml, so it's a Python project.",
        ["Cargo.toml"] = "It has a Cargo.toml, so it's a Rust project.",
        ["go.mod"] = "It has a go.mod, so it's a Go project.",
        ["node_modules"] = "It has a node_modules folder, so it's a code project.",
        [".venv"] = "It has a .venv folder, so it's a Python project.",
        ["steamapps"] = "It has a steamapps folder, so it's a Steam game library.",
    };

    private static readonly Dictionary<string, string> MarkerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".sln"] = "It has a Visual Studio solution (.sln), so it's a code project.",
        [".csproj"] = "It has a .csproj file, so it's a code project.",
    };

    private const string SyncthingMarker = ".stfolder";

    private const string AppFolderReason = "It has .exe and .dll files side by side, so it looks like an app or game.";

    private readonly BoundedCache<DirMarkers> _cache = new(CacheGenerationSize, CacheLifetime);
    private readonly HashSet<string> _containers;

    private readonly HashSet<string> _drives;

    /// <param name="containerFolders">
    /// Folders that hold the user's stuff and are never themselves "a project", even if a stray marker lands in them
    /// (a package.json in the profile root must not lock the whole profile).
    /// </param>
    /// <param name="driveRoots">Where upward walks stop (this PC's drives; in the sandbox, its fake drives). Real drive roots always stop a walk.</param>
    public ProjectDetector(IEnumerable<string> containerFolders, IEnumerable<string> driveRoots)
    {
        _drives = new HashSet<string>(driveRoots.Select(SafePath.TryNormalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _containers = new HashSet<string>(containerFolders.Select(SafePath.TryNormalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _containers.UnionWith(_drives);
    }

    /// <summary>Containers = profile, user libraries, Downloads and cloud roots; walks stop at the fixed drives.</summary>
    public ProjectDetector(IKnownLocations locations) : this(ContainersOf(locations), locations.FixedDrives) { }

    public static IEnumerable<string> ContainersOf(IKnownLocations k)
        => new[] { k.UserProfile, k.Desktop, k.Documents, k.Downloads, k.Pictures, k.Videos, k.Music }.Concat(k.CloudRoots);

    /// <summary>Does this folder itself look like a code project or an app/game folder?</summary>
    public bool IsProjectOrAppFolder(string dir) => IsProjectOrAppFolder(dir, out _);

    public bool IsProjectOrAppFolder(string dir, out string? why)
    {
        why = null;
        var p = SafePath.TryNormalize(dir);
        if (p is null || IsContainer(p)) return false;
        why = Markers(p).ProjectWhy;
        return why is not null;
    }

    /// <summary>
    /// Nearest folder at or above <paramref name="startDir"/> that is a project/app folder, stopping at drive roots
    /// and container folders. Null when there is none.
    /// </summary>
    public string? FindProjectRoot(string startDir, out string? why)
    {
        why = null;
        var p = SafePath.TryNormalize(startDir);
        return p is null ? null : FindProjectRootNormalized(p, out why);
    }

    public string? FindProjectRoot(string startDir) => FindProjectRoot(startDir, out _);

    /// <summary>Same as <see cref="FindProjectRoot(string, out string?)"/> for a path already normalised by <see cref="SafePath"/>.</summary>
    /// <param name="stopAt">Optional extra boundary (not itself examined), e.g. a cleanup target's own folder.</param>
    internal string? FindProjectRootNormalized(string normalized, out string? why, string? stopAt = null)
    {
        why = null;
        for (string? p = normalized; p is not null && !IsContainer(p) && (stopAt is null || !SafePath.SameAs(p, stopAt)); p = SafePath.Parent(p))
        {
            why = Markers(p).ProjectWhy;
            if (why is not null) return p;
        }
        return null;
    }

    /// <summary>
    /// Nearest folder at or above <paramref name="startDir"/> that Syncthing syncs (it holds a ".stfolder" marker).
    /// Unlike projects this also checks container folders: Documents itself may be a Syncthing folder.
    /// </summary>
    public string? FindSyncthingRoot(string startDir)
        => SafePath.TryNormalize(startDir) is { } p ? FindSyncthingRootNormalized(p) : null;

    internal string? FindSyncthingRootNormalized(string normalized)
    {
        for (string? p = normalized; p is not null; p = _drives.Contains(p) ? null : SafePath.Parent(p))
            if (Markers(p).IsSyncthingRoot) return p;
        return null;
    }

    /// <summary>Forget cached answers (e.g. right after a scan finished and the user starts acting).</summary>
    public void ClearCache() => _cache.Clear();

    private bool IsContainer(string normalized) => normalized.Length <= 3 || _containers.Contains(normalized);

    /// <summary>What one folder listing says about the folder.</summary>
    private readonly record struct DirMarkers(string? ProjectWhy, bool IsSyncthingRoot);

    private DirMarkers Markers(string dir)
    {
        if (_cache.TryGet(dir, out var hit)) return hit;
        var markers = ReadMarkers(dir);
        _cache.Set(dir, markers);
        return markers;
    }

    private static DirMarkers ReadMarkers(string dir)
    {
        string? why = null;
        bool hasExe = false, hasDll = false, syncthing = false;
        try
        {
            // Literal \\?\ form, like every other probe: the folder is read exactly as named.
            var literal = SafePath.Extended(dir);
            if (!Directory.Exists(literal)) return default;
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            var names = new FileSystemEnumerable<string>(literal, (ref FileSystemEntry e) => e.FileName.ToString(), options);
            foreach (var name in names)
            {
                if (name.Equals(SyncthingMarker, StringComparison.OrdinalIgnoreCase)) syncthing = true;
                else if (why is null)
                {
                    var ext = Path.GetExtension(name);
                    if (MarkerNames.TryGetValue(name, out var w) || MarkerExtensions.TryGetValue(ext, out w)) why = w;
                    else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) hasExe = true;
                    else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) hasDll = true;
                    if (why is null && hasExe && hasDll) why = AppFolderReason;
                }
                if (why is not null && syncthing) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return default;
        }
        return new DirMarkers(why, syncthing);
    }
}
