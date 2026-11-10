using PcTidy.Core;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Planning;

/// <summary>One row of the before/after tree (flattened for a virtualized list).</summary>
public sealed record TreeRow(int Indent, string Name, string CountText, string Icon, bool IsFolder, bool IsNew)
{
    public double IndentWidth => Indent * 24;
}

/// <summary>Before → after tree for the preview: loose files now, and the folders they'll end up in.</summary>
public static class PreviewTree
{
    public static IReadOnlyList<TreeRow> Before(OrganizePreview preview, int filesPerRoot = 8)
    {
        var rows = new List<TreeRow>();
        foreach (var root in preview.Roots)
        {
            var count = root.LooseFiles + root.LooseFolders;
            rows.Add(new TreeRow(0, root.DisplayName, Plural(count, "item"), "FolderOpen24", true, false));
            // Show the files that will change first, biggest first: they're the interesting ones.
            var changing = preview.Actions.Where(a => a.Root == root.Root).OrderByDescending(a => a.Size)
                .Select(a => (a.Name, a.Size, a.IsDirectory))
                .Concat(preview.Skipped.Where(s => s.Root == root.Root).OrderByDescending(s => s.Size).Select(s => (s.Name, s.Size, s.IsDirectory)))
                .ToList();
            foreach (var (name, size, isDir) in changing.Take(filesPerRoot))
                rows.Add(new TreeRow(1, name, isDir ? "folder" : Sizes.Format(size), isDir ? "Folder24" : IconFor(name), false, false));
            if (changing.Count > filesPerRoot)
                rows.Add(new TreeRow(1, $"…and {changing.Count - filesPerRoot:N0} more", "", "MoreHorizontal24", false, false));
        }
        return rows;
    }

    public static IReadOnlyList<TreeRow> After(OrganizePreview preview)
    {
        var rows = new List<TreeRow>();
        foreach (var root in preview.Roots)
        {
            var actions = preview.Actions.Where(a => a.Root == root.Root).ToList();
            var stay = preview.Skipped.Count(s => s.Root == root.Root && s.Kind is not (SkipKind.AlreadySorted));
            rows.Add(new TreeRow(0, root.DisplayName, stay > 0 ? $"{stay:N0} stay" : "", "FolderOpen24", true, false));

            // Folder tree of destinations inside this root, with "+N" arrivals per folder.
            var arrivals = actions.Where(a => a.Kind is PlannedActionKind.Move or PlannedActionKind.MoveFolder && a.DestinationPath is not null)
                .GroupBy(a => Path.GetDirectoryName(a.DestinationPath!)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var inside = arrivals.Keys.Where(k => PathUtil.IsUnder(k, root.Root)).ToList();
            var allFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in inside)
                for (var d = folder; d is not null && !d.Equals(root.Root, StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(d, root.Root); d = Path.GetDirectoryName(d))
                    allFolders.Add(d);
            foreach (var folder in allFolders)
            {
                var depth = Path.GetRelativePath(root.Root, folder).Split('\\').Length;
                var n = arrivals.GetValueOrDefault(folder);
                rows.Add(new TreeRow(depth, Path.GetFileName(folder), n > 0 ? $"+{n:N0}" : "", "Folder24", true, !Directory.Exists(folder) || n > 0));
            }

            foreach (var outside in arrivals.Where(kv => !PathUtil.IsUnder(kv.Key, root.Root)))
                rows.Add(new TreeRow(0, outside.Key, $"+{outside.Value:N0}", "Folder24", true, true));
            var renamed = actions.Count(a => a.Kind == PlannedActionKind.Rename);
            if (renamed > 0) rows.Add(new TreeRow(1, "Renamed in place", $"{renamed:N0}", "Rename24", false, true));
            var quarantined = actions.Count(a => a.Kind is PlannedActionKind.Quarantine or PlannedActionKind.DuplicateQuarantine);
            if (quarantined > 0) rows.Add(new TreeRow(0, "Quarantine (restorable)", $"+{quarantined:N0}", "BoxArrowLeft24", true, true));
        }
        return rows;
    }

    /// <summary>WPF-UI symbol for a file, by category.</summary>
    public static string IconFor(string fileName)
    {
        var ext = OrganizeCategories.ExtensionOf(fileName);
        if (ext == "pdf") return "DocumentPdf24";
        if (ext == "exe") return "Apps24";
        if (Planning.SkipRules.ShortcutExtensions.Contains(ext)) return "Link24";
        return OrganizeCategories.All.FirstOrDefault(c => c.Extensions.Contains(ext))?.Icon ?? "Document24";
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";
}
