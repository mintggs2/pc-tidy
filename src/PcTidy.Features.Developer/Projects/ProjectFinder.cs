using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Projects;

/// <summary>
/// Finds project roots under the chosen folders by their markers (names only, from folder listings). A project is one
/// unit: the walk never goes inside a project it found. Never follows junctions or symlinks, skips Windows, Program
/// Files, app and game folders, PC Tidy's data, AppData and hidden tool folders (".vscode\extensions" is full of
/// package.json files that aren't your projects).
/// </summary>
internal static class ProjectFinder
{
    public const int MaxDepth = 10;
    public const int MaxFolders = 250_000;
    /// <summary>Near the top of each root every folder is also asked about the safety rules (cheap enough there).</summary>
    private const int GuardCheckDepth = 4;
    /// <summary>A name that never exists, used to ask the guard about a folder's contents.</summary>
    private const string ProbeName = "pc-tidy-probe";

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs",
        "System Volume Information", "WindowsApps", "XboxGames", "steamapps", "SteamLibrary", "PC Tidy Quarantine",
        "node_modules", "Application Data", "Local Settings",
    };

    public sealed record Result(IReadOnlyList<DevProject> Projects, IReadOnlyList<string> Notes);

    public static Result Find(DevContext dev, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var k = dev.Locations;
        var notes = new List<string>();
        var containers = Containers(k);
        var never = NeverFolders(k, dev.Scan.Settings);
        var projects = new List<DevProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int folders = 0;
        bool capped = false;

        foreach (var root in DistinctRoots(dev.Roots))
        {
            if (!FolderMeasure.DirectoryExists(root)) continue;
            // The root itself isn't asked about: a drive or your user folder is "never moved" as a whole, which says
            // nothing about what's inside. Every folder below it is checked on the way down.
            if (never.Any(n => PathUtil.IsUnder(root, n)))
            {
                notes.Add($"'{root}' is a protected place, so PC Tidy didn't look for projects there.");
                continue;
            }
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = stack.Pop();
                if (++folders > MaxFolders) { capped = true; break; }
                if (folders % 500 == 0) progress?.Report(new ScanProgress($"Looking for projects in {dir}…", null, folders));

                var children = FolderMeasure.Children(dev.Walker, dir, ct);
                var markers = children.Where(c => ProjectMarkers.IsMarker(c.Name)).Select(c => c.Name).ToList();
                if (markers.Count > 0 && !containers.Contains(PathUtil.Normalize(dir)))
                {
                    if (seen.Add(PathUtil.Normalize(dir)))
                        projects.Add(new DevProject(PathUtil.Normalize(dir), Path.GetFileName(PathUtil.Normalize(dir)), markers, OwnWriteTime(dev, dir, ct)));
                    continue; // a project is one unit: nothing inside it is a separate project
                }
                if (depth >= MaxDepth) continue;
                foreach (var c in children)
                {
                    if (!c.IsDirectory || c.IsReparsePoint || ShouldSkip(c)) continue;
                    if (never.Any(n => PathUtil.IsUnder(c.FullPath, n))) continue;
                    if (depth < GuardCheckDepth && IsNeverInside(dev.Safety, c.FullPath)) continue;
                    stack.Push((c.FullPath, depth + 1));
                }
            }
            if (capped) break;
        }
        if (capped) notes.Add($"PC Tidy stopped looking after {MaxFolders:N0} folders. Pick fewer or smaller folders to look in to see the rest.");
        return new Result(projects.OrderBy(p => p.Root, StringComparer.OrdinalIgnoreCase).ToList(), notes);
    }

    private static bool ShouldSkip(FileEntry e)
        => SkipNames.Contains(e.Name) || e.Name.StartsWith('.') || e.Name.StartsWith('$') || (e.IsHidden && e.IsSystem);

    /// <summary>
    /// Is what's INSIDE this folder never touched? Asked about a child path, not the folder: Downloads or a OneDrive root
    /// is "never moved" as a folder, which says nothing about the projects inside it.
    /// </summary>
    private static bool IsNeverInside(ISafetyGuard safety, string folder)
    {
        try
        {
            var v = safety.ClassifyPath(Path.Combine(folder, ProbeName));
            return !v.Allowed && v.Protection == PathProtection.Never;
        }
        catch (Exception) { return true; }
    }

    private static DateTime OwnWriteTime(DevContext dev, string dir, CancellationToken ct)
        => FolderMeasure.Self(dev.Walker, dir, ct)?.LastWriteUtc ?? DateTime.MinValue;

    /// <summary>Folders that hold your stuff and are never themselves "a project", even with a stray package.json in them.</summary>
    internal static HashSet<string> Containers(IKnownLocations k)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { k.UserProfile, k.Desktop, k.Documents, k.Downloads, k.Pictures, k.Videos, k.Music }.Concat(k.CloudRoots).Concat(k.FixedDrives))
        {
            try { set.Add(PathUtil.Normalize(p)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return set;
    }

    /// <summary>Places that never hold projects worth cleaning: Windows, Program Files, apps, games, PC Tidy's data, exclusions.</summary>
    private static List<string> NeverFolders(IKnownLocations k, AppSettings settings)
    {
        var list = new List<string> { k.WindowsDir, k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.LocalAppData, k.RoamingAppData, k.DataRoot };
        list.AddRange(k.GameInstallDirs);
        list.AddRange(k.SteamLibraries);
        list.AddRange(k.AppInstallDirs);
        list.AddRange(settings.ScanExclusions);
        list.AddRange(settings.ExtraProtectedFolders);
        return list.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).ToList();
    }

    /// <summary>Normalised roots, without ones that sit inside another root (each folder is looked at once).</summary>
    internal static IReadOnlyList<string> DistinctRoots(IEnumerable<string> roots)
    {
        var normal = roots.Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return normal.Where(r => !normal.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(r, o))).ToList();
    }
}
