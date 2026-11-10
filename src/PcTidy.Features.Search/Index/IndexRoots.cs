using PcTidy.Core;

namespace PcTidy.Features.Search.Index;

/// <summary>Which folders PC Tidy's own index covers.</summary>
public static class IndexRoots
{
    /// <summary>
    /// The user's chosen roots, or the defaults: the user profile plus every other fixed drive (its own folders;
    /// protected areas such as game libraries and Windows folders are skipped while walking). Nested roots are
    /// dropped (the outer one covers them) and missing folders are ignored, so any PC works.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IKnownLocations locations, SearchSettings settings)
    {
        IEnumerable<string> candidates = settings.IndexRoots.Count > 0
            ? settings.IndexRoots
            : new[] { locations.UserProfile }.Concat(locations.FixedDrives.Where(d => !SameFolder(d, locations.SystemDrive)));
        var roots = new List<string>();
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string root;
            try { root = PathUtil.Normalize(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!Directory.Exists(root)) continue;
            if (roots.Any(r => PathUtil.IsUnder(root, r))) continue;
            roots.RemoveAll(r => PathUtil.IsUnder(r, root));
            roots.Add(root);
        }
        return roots;
    }

    private static bool SameFolder(string a, string b)
    {
        try { return string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}
