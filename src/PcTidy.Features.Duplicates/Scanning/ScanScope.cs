using System.Text.RegularExpressions;
using PcTidy.Features.Duplicates.Model;

namespace PcTidy.Features.Duplicates.Scanning;

/// <summary>Why a folder was not looked inside.</summary>
public enum SkipKind { None, Link, Protected, Project, SyncOrToolInternals, Excluded }

/// <summary>
/// Where a duplicates scan looks and what it never enters (Plan §8.3): Windows, Program Files, ProgramData, app and
/// game folders, AppData, PC Tidy's own data, .git / node_modules and other tool internals, sync-version folders
/// (.stversions…), code projects and app-looking folders (via the safety guard's project detector), junctions.
/// Also answers keep rule 2: is a path in a library folder, Downloads/Temp/backup-looking, or elsewhere.
/// </summary>
public sealed class ScanScope
{
    /// <summary>Tool and sync internals: version folders, repositories, package folders, recycle bins.</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".bzr", "node_modules", ".venv", "__pycache__", ".tox", ".gradle", ".m2",
        // Sync tools' own version / bookkeeping folders.
        ".stversions", ".stfolder", ".dropbox.cache", ".SynologyWorkingDirectory", "#recycle", "@eaDir", "#snapshot",
        ".tmp.drivedownload", ".tmp.driveupload", ".Trashes", ".Spotlight-V100", ".fseventsd",
        "$RECYCLE.BIN", "System Volume Information",
    };

    /// <summary>Folders Windows (or PC Tidy) keeps at the top of a drive.</summary>
    private static readonly string[] DriveRootSystemFolders =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps", "XboxGames", "Boot", "EFI", "Recovery",
        "Config.Msi", "$WinREAgent", "$SysReset", "$GetCurrent", "Windows.old", "$Windows.~BT", "$Windows.~WS",
        "PC Tidy Quarantine", "FileHistory", "OneDriveTemp",
    };

    private static readonly Regex BackupLooking = new(
        @"^(back ?ups?|.*\bbackups?\b.*|.*\bbak\b.*|old|old .*|.* old|temp|tmp|temp .*|.* temp|trash|archive[sd]?|.*\barchived?\b.*|copies|copy of .*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IKnownLocations _k;
    private readonly ISafetyGuard _guard;
    private readonly List<string> _protectedTrees;
    private readonly List<string> _excluded;
    private readonly List<(string Path, string Label)> _libraries;
    private readonly List<(string Path, string Label)> _downloadsLike;
    private readonly List<string> _otherProfiles;
    private readonly List<string> _drives;

    public ScanScope(ScanContext ctx, DuplicatesSettings settings)
    {
        _k = ctx.Locations;
        _guard = ctx.Safety;
        Depth = ctx.Depth;
        _protectedTrees = Norm(ProtectedTrees(_k, ctx.Settings)).ToList();
        _excluded = Norm(ctx.Settings.ScanExclusions).ToList();
        _drives = Norm(_k.FixedDrives).Select(d => d.EndsWith('\\') ? d : d + "\\").ToList();
        ReferenceFolders = Norm(settings.ReferenceFolders.Concat(ctx.Settings.ExtraProtectedFolders)).ToList();

        _libraries = new List<(string, string)>
        {
            (_k.Desktop, "your Desktop"), (_k.Documents, "your Documents folder"), (_k.Pictures, "your Pictures folder"),
            (_k.Videos, "your Videos folder"), (_k.Music, "your Music folder"),
        };
        // A cloud folder keeps its own Desktop / Documents / Pictures even when Windows' folders aren't redirected there.
        foreach (var cloud in _k.CloudRoots)
            foreach (var (name, label) in new[] { ("Desktop", "your cloud Desktop"), ("Documents", "your cloud Documents"), ("Pictures", "your cloud Pictures"), ("Music", "your cloud Music"), ("Videos", "your cloud Videos") })
                _libraries.Add((Path.Combine(cloud, name), label));
        _libraries = _libraries.Where(l => !string.IsNullOrWhiteSpace(l.Item1)).Select(l => (PathUtil.Normalize(l.Item1), l.Item2)).ToList();
        _downloadsLike = new List<(string, string)> { (_k.Downloads, "Downloads"), (_k.UserTemp, "your temp folder"), (_k.WindowsTemp, "Windows' temp folder") }
            .Where(l => !string.IsNullOrWhiteSpace(l.Item1)).Select(l => (PathUtil.Normalize(l.Item1), l.Item2)).ToList();

        var usersDir = Path.GetDirectoryName(PathUtil.Normalize(_k.UserProfile));
        _otherProfiles = new List<string>();
        if (usersDir is not null)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(usersDir))
                    if (!PathUtil.IsUnder(_k.UserProfile, d) && !string.Equals(Path.GetFileName(d), "Public", StringComparison.OrdinalIgnoreCase))
                        _otherProfiles.Add(PathUtil.Normalize(d));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no listing: nothing extra to skip */ }
        }

        Roots = ctx.Roots is { Count: > 0 } chosen ? Dedupe(chosen) : Dedupe(DefaultRoots(_k, ctx.Depth));
    }

    public ScanDepth Depth { get; }
    /// <summary>Top folders the scan walks (no folder inside another).</summary>
    public IReadOnlyList<string> Roots { get; }
    /// <summary>User-marked reference folders plus the folders the user protected in Settings: scanned, never removed.</summary>
    public IReadOnlyList<string> ReferenceFolders { get; }

    /// <summary>
    /// Quick: your user folders (Desktop, Documents, Downloads, Pictures, Videos, Music, cloud folders) plus the other
    /// fixed drives. Deep: every fixed drive (still never entering the protected places).
    /// </summary>
    public static IEnumerable<string> DefaultRoots(IKnownLocations k, ScanDepth depth)
    {
        var system = PathUtil.Normalize(k.SystemDrive);
        if (depth == ScanDepth.Deep)
        {
            foreach (var d in k.FixedDrives) yield return d;
            yield break;
        }
        foreach (var p in new[] { k.Desktop, k.Documents, k.Downloads, k.Pictures, k.Videos, k.Music }.Concat(k.CloudRoots))
            if (!string.IsNullOrWhiteSpace(p)) yield return p;
        foreach (var d in k.FixedDrives)
            if (!string.Equals(PathUtil.Normalize(d), system, StringComparison.OrdinalIgnoreCase)) yield return d;
    }

    /// <summary>
    /// Existing folders only, outermost first, none inside another, judged by where each folder REALLY is
    /// (<see cref="FinalPath"/>): a root that is, or goes through, a junction into another root is the same folder seen
    /// twice, so it's dropped (and its files can never be offered as duplicates of themselves). Of two roots that are
    /// the same folder, the one reached without a link is kept, because links are never walked into.
    /// </summary>
    internal static IReadOnlyList<string> Dedupe(IEnumerable<string> roots)
    {
        var list = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(PathUtil.Normalize)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => (Root: r, Real: FinalPath.Of(r) ?? r))
            .OrderBy(r => r.Real.Length)
            .ThenBy(r => string.Equals(r.Root, r.Real, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Root.Length)
            .ToList();
        var kept = new List<(string Root, string Real)>();
        foreach (var r in list)
            if (!kept.Any(outer => PathUtil.IsUnder(r.Real, outer.Real) || PathUtil.IsUnder(r.Root, outer.Root))) kept.Add(r);
        return kept.Select(k => k.Root).ToList();
    }

    /// <summary>Areas a duplicates scan never enters (it still compares against reference folders, which are entered).</summary>
    private static IEnumerable<string?> ProtectedTrees(IKnownLocations k, AppSettings settings)
    {
        yield return k.WindowsDir;
        yield return k.ProgramFiles;
        yield return k.ProgramFilesX86;
        yield return k.ProgramData;
        // App data is owned by apps: their copies are on purpose, and Disk cleanup handles temp files.
        yield return Path.Combine(k.UserProfile, "AppData");
        yield return k.LocalAppData;
        yield return k.RoamingAppData;
        yield return k.DataRoot;
        yield return settings.DataRoot;
        yield return AppSettings.DefaultDataRoot;
        yield return k.ClaudeDir;
        yield return k.CodexDir;
        yield return k.SteamRoot;
        foreach (var lib in k.SteamLibraries) yield return lib;
        foreach (var g in k.GameInstallDirs) yield return g;
        foreach (var a in k.AppInstallDirs) yield return a;
        foreach (var drive in k.FixedDrives)
            foreach (var name in DriveRootSystemFolders)
                yield return Path.Combine(drive, name);
    }

    private static IEnumerable<string> Norm(IEnumerable<string?> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string? n = null;
            try { n = PathUtil.Normalize(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (n is not null) yield return n;
        }
    }

    /// <summary>Should the walk go inside this folder? Never through links, never into protected or project folders.</summary>
    public SkipKind ShouldSkip(in FileEntry dir)
    {
        if (dir.IsReparsePoint) return SkipKind.Link;
        if (SkipNames.Contains(dir.Name) || dir.Name.StartsWith(".849C9593", StringComparison.OrdinalIgnoreCase)) return SkipKind.SyncOrToolInternals;
        var path = dir.FullPath;
        if (_protectedTrees.Any(p => PathUtil.IsUnder(path, p)) || _otherProfiles.Any(p => PathUtil.IsUnder(path, p))) return SkipKind.Protected;
        if (_excluded.Any(e => PathUtil.IsUnder(path, e))) return SkipKind.Excluded;
        // Code projects and app/game-looking folders are one unit: their copies are on purpose.
        try
        {
            if (_guard.IsInsideProjectOrAppFolder(path, out _)) return SkipKind.Project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return SkipKind.Protected; }
        return SkipKind.None;
    }

    public bool IsReference(string path) => ReferenceFolders.Any(r => PathUtil.IsUnder(path, r));

    /// <summary>Keep rule 2: library folders beat "other", which beats Downloads, temp and backup-looking folders.</summary>
    public LocationClass Classify(string path, out string label)
    {
        foreach (var (lib, name) in _libraries.OrderByDescending(l => l.Path.Length))
            if (PathUtil.IsUnder(path, lib)) { label = name; return LocationClass.Library; }
        foreach (var (dl, name) in _downloadsLike)
            if (PathUtil.IsUnder(path, dl)) { label = name; return LocationClass.DownloadsTempOrBackup; }
        var folder = Path.GetDirectoryName(path) ?? path;
        // Only the part below this PC's drive (or the sandbox's fake drive) says anything about the folder.
        var root = _drives.Where(d => PathUtil.IsUnder(folder, d)).OrderByDescending(d => d.Length).FirstOrDefault()
                   ?? Path.GetPathRoot(folder) ?? "";
        var below = folder.Length > root.Length ? folder[root.Length..] : "";
        foreach (var segment in below.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("Downloads", StringComparison.OrdinalIgnoreCase)) { label = "a Downloads folder"; return LocationClass.DownloadsTempOrBackup; }
            if (BackupLooking.IsMatch(segment)) { label = $"a backup-looking folder (\"{segment}\")"; return LocationClass.DownloadsTempOrBackup; }
        }
        label = FriendlyFolder(path);
        return LocationClass.Other;
    }

    /// <summary>"Pictures\Holiday 2025", "Downloads", or the full folder path when it's outside the user folders.</summary>
    public string FriendlyFolder(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        return FriendlyPath(folder);
    }

    public string FriendlyPath(string path)
    {
        var known = new (string? Path, string Name)[]
        {
            (_k.Desktop, "Desktop"), (_k.Documents, "Documents"), (_k.Pictures, "Pictures"), (_k.Videos, "Videos"),
            (_k.Music, "Music"), (_k.Downloads, "Downloads"),
        }.Concat(_k.CloudRoots.Select(c => (Path: (string?)c, Name: "OneDrive")))
         .Where(x => !string.IsNullOrWhiteSpace(x.Path))
         .OrderByDescending(x => x.Path!.Length);
        foreach (var (p, name) in known)
        {
            var n = PathUtil.Normalize(p!);
            if (PathUtil.IsUnder(path, n))
            {
                var rest = PathUtil.Normalize(path).Length > n.Length ? PathUtil.Normalize(path)[(n.Length + 1)..] : "";
                return rest.Length == 0 ? name : name + "\\" + rest;
            }
        }
        return path;
    }

    /// <summary>Named like a copy: "x - Copy.txt", "x - Copy (2).txt", "x (1).txt", "Copy of x.txt", "x copy.txt".</summary>
    public static bool HasCopyName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
        return CopyName.IsMatch(name);
    }

    private static readonly Regex CopyName = new(
        @"(\s-\s(copy|kopie|copie|copia)(\s\(\d+\))?$)|(\s\(\d+\)$)|(^copy( \(\d+\))? of )|(\s(copy|\(copy\))$)|(_copy$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
