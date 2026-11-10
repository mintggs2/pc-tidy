using PcTidy.Core;

namespace PcTidy.Features.Organize;

/// <summary>
/// Exclusion matching by whole path segment. A full path ("C:\Work") excludes that folder and everything inside;
/// a bare name ("Projects", or "Work\Projects") excludes any folder with exactly that name, so excluding
/// "Projects" never excludes "MyProjects2".
/// </summary>
public static class PathExclusions
{
    public static bool IsExcluded(string path, IEnumerable<string> exclusions)
        => exclusions.Any(x => IsExcluded(path, x));

    public static bool IsExcluded(string path, string exclusion)
    {
        if (string.IsNullOrWhiteSpace(exclusion) || string.IsNullOrWhiteSpace(path)) return false;
        var x = exclusion.Trim();
        if (Path.IsPathFullyQualified(x)) return PathUtil.IsUnder(path, x);

        var want = x.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        var have = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        if (want.Length == 0) return false;
        for (int start = 0; start + want.Length <= have.Length; start++)
        {
            bool all = true;
            for (int i = 0; i < want.Length && all; i++)
                all = string.Equals(have[start + i], want[i], StringComparison.OrdinalIgnoreCase);
            if (all) return true;
        }
        return false;
    }
}
