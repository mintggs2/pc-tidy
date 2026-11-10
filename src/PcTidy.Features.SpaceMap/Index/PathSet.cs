using PcTidy.Core;

namespace PcTidy.Features.SpaceMap.Index;

/// <summary>A few folder roots, matched against already-full paths without re-normalizing them (hot path: once per folder).</summary>
internal sealed class PathSet
{
    private readonly string[] _roots;

    public PathSet(IEnumerable<string> roots)
        => _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool IsEmpty => _roots.Length == 0;

    public bool ContainsOrIsUnder(string fullPath)
    {
        var path = fullPath.AsSpan().TrimEnd('\\');
        foreach (var r in _roots)
        {
            if (path.Length < r.Length || !path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == r.Length || path[r.Length] == '\\') return true;
        }
        return false;
    }

    private static string Normalize(string p)
    {
        try { return PathUtil.Normalize(p).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p.TrimEnd('\\'); }
    }
}
