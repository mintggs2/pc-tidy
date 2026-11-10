namespace PcTidy.Platform;

/// <summary>
/// The one check every path that arrives in a plan item's arguments must pass before a system handler uses it:
/// a plain local path "X:\...". Refused: UNC shares, device and raw paths (<c>\\.\</c>, <c>\\?\</c>, GLOBALROOT),
/// alternate data streams, forward slashes, "." / ".." segments, segments ending in a dot or space, control characters.
/// </summary>
public static class PathArgs
{
    private static readonly char[] Wildcards = { '*', '?', '"', '<', '>', '|' };

    public static bool IsPlainLocal(string? path, bool allowDriveRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000) return false;
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        if (path.Any(char.IsControl) || path.Contains('/') || path.IndexOf(':', 2) >= 0) return false;
        if (path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.IndexOfAny(Wildcards) >= 0) return false;
        if (path.Length == 3) return allowDriveRoot;
        foreach (var p in path[3..].TrimEnd('\\').Split('\\'))
        {
            if (p.Length == 0 || p is "." or "..") return false;
            if (p.EndsWith('.') || p.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>"C", "C:", "C:\" → 'C'; anything else → null.</summary>
    public static char? DriveLetter(string? drive)
    {
        var d = drive?.Trim() ?? "";
        if (d.EndsWith('\\')) d = d[..^1];
        if (d.EndsWith(':')) d = d[..^1];
        return d.Length == 1 && char.IsAsciiLetter(d[0]) ? char.ToUpperInvariant(d[0]) : null;
    }

    /// <summary>True if no existing part of the path, from its drive down, is a junction or symbolic link.</summary>
    public static bool HasNoLinks(string path)
    {
        var cur = Path.GetPathRoot(path)!;
        foreach (var part in path[cur.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            try
            {
                if ((File.GetAttributes(@"\\?\" + cur) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception) { return false; }
        }
        return true;
    }
}
