using System.Text;

namespace PcTidy.Features.Organize;

/// <summary>Windows file-name rules: illegal characters, reserved device names, the classic 260-character limit.</summary>
public static class WindowsNames
{
    /// <summary>Explorer and many older apps still break on paths longer than this.</summary>
    public const int ClassicMaxPath = 260;

    private static readonly HashSet<string> Reserved = BuildReserved();

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        foreach (var prefix in new[] { "COM", "LPT" })
        {
            for (int i = 1; i <= 9; i++) set.Add(prefix + i);
            // Windows also reserves the superscript digits ¹ ² ³.
            set.Add(prefix + "¹"); set.Add(prefix + "²"); set.Add(prefix + "³");
        }
        return set;
    }

    public static bool IsIllegalChar(char c) => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    /// <summary>"CON", "con.txt", "Nul .tar.gz": Windows treats the part before the first dot as a device name.</summary>
    public static bool IsReserved(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = (dot >= 0 ? fileName[..dot] : fileName).TrimEnd(' ');
        return Reserved.Contains(stem);
    }

    public static string RemoveIllegal(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (!IsIllegalChar(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Makes one folder-name segment safe (illegal chars dropped, no trailing dots/spaces, no device names).</summary>
    public static string CleanFolderSegment(string segment)
    {
        if (segment is "." or "..") return segment;
        var s = RemoveIllegal(segment).Trim().TrimEnd('.', ' ');
        if (s.Length > 0 && IsReserved(s)) s += "_";
        return s;
    }

    /// <summary>Why <paramref name="fileName"/> can't be used, or null when it's fine.</summary>
    public static string? Problem(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "The new name is empty.";
        if (fileName.Any(IsIllegalChar)) return "The new name has characters Windows doesn't allow (< > : \" / \\ | ? *).";
        if (fileName.EndsWith('.') || fileName.EndsWith(' ')) return "Windows doesn't allow names ending in a dot or space.";
        if (IsReserved(fileName)) return $"Windows reserves the name \"{fileName}\".";
        return null;
    }
}
