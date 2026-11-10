using PcTidy.Catalog.Developer;
using PcTidy.Core;
using PcTidy.Features.Developer.Common;

namespace PcTidy.Features.Developer.Projects;

/// <summary>
/// One read-only walk through a project: finds its rebuildable folders (each flagged only next to its own marker) and
/// works out when you last worked on it. Activity = the newest of git's own files (.git\logs\HEAD, .git\index,
/// .git\FETCH_HEAD: they change on every commit, checkout, stage or fetch) and every file of the project outside its
/// build folders. Git is never run. Junctions and symlinks are never followed (pnpm builds node_modules from them).
/// </summary>
internal static class ProjectInspector
{
    public const int MaxDepth = 14;
    /// <summary>A project bigger than this is not looked through fully, so it's treated as in use (never cleaned).</summary>
    public const int MaxEntries = 400_000;

    public static ProjectReport Inspect(DevContext dev, DevProject project, CancellationToken ct)
    {
        var newest = DateTime.MinValue;
        var source = "nothing dated";
        foreach (var vcs in ProjectMarkers.VersionControlFolders)
        {
            var dir = Path.Combine(project.Root, vcs);
            foreach (var f in ProjectMarkers.GitActivityFiles)
                if (FolderMeasure.FileWriteUtc(Path.Combine(dir, f)) is { } t && t > newest) { newest = t; source = "git"; }
        }

        var candidates = new List<RebuildCandidate>();
        int entries = 0, links = 0;
        bool truncated = false;
        var rootHasDependencyList = HasDependencyList(FolderMeasure.Children(dev.Walker, project.Root, ct).Select(c => c.Name));
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((project.Root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            var children = FolderMeasure.Children(dev.Walker, dir, ct);
            entries += children.Count;
            if (entries > MaxEntries) { truncated = true; break; }
            var names = children.Select(c => c.Name).ToList();
            foreach (var c in children)
            {
                if (!c.IsDirectory)
                {
                    if (c.LastTouchedUtc > newest) { newest = c.LastTouchedUtc; source = "a file"; }
                    continue;
                }
                if (c.IsReparsePoint) { links++; continue; }
                if (ProjectMarkers.VersionControlFolders.Contains(c.Name)) continue;
                if (Match(dev, c, names, rootHasDependencyList, ct) is { } candidate)
                {
                    candidates.Add(candidate);
                    continue; // its contents belong to the tool, not to you: they don't count as your activity
                }
                if (depth + 1 < MaxDepth) stack.Push((c.FullPath, depth + 1));
            }
        }

        DateTime? last = newest > DateTime.MinValue ? newest : project.FolderWriteUtc > DateTime.MinValue ? project.FolderWriteUtc : null;
        if (newest == DateTime.MinValue && last is not null) source = "the folder";
        return new ProjectReport(project, last, source, candidates, links, truncated);
    }

    /// <summary>The first rule this folder satisfies (marker next to it, proof inside if the rule needs it), or null.</summary>
    internal static RebuildCandidate? Match(DevContext dev, FileEntry folder, IReadOnlyList<string> siblingNames, bool projectHasDependencyList, CancellationToken ct)
    {
        IReadOnlyList<FileEntry>? inside = null;
        foreach (var rule in RebuildableFolders.For(folder.Name))
        {
            if (!rule.HasSiblingMarker(siblingNames)) continue;
            inside ??= FolderMeasure.Children(dev.Walker, folder.FullPath, ct);
            var proof = rule.ProofInside.Any(p => inside.Any(e => e.Name.Equals(p, StringComparison.OrdinalIgnoreCase)));
            if (rule.ProofRequired && !proof) continue;
            if (rule.OnlyExtensions is { } exts &&
                inside.Any(e => e.IsDirectory || !exts.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)))
                continue;
            var confidence = rule.ProofInside.Count == 0 || proof ? RebuildConfidence.Certain : RebuildConfidence.Likely;
            var size = FolderMeasure.Measure(dev.Walker, folder.FullPath, ct);
            var deps = rule.Kind == RebuildKind.PythonVenv && (projectHasDependencyList || HasDependencyList(siblingNames));
            return new RebuildCandidate(folder.FullPath, rule, confidence, folder.LastWriteUtc, size, deps);
        }
        return null;
    }

    private static bool HasDependencyList(IEnumerable<string> names)
        => names.Any(n => ProjectMarkers.PythonDependencyFiles.Contains(n, StringComparer.OrdinalIgnoreCase));
}
