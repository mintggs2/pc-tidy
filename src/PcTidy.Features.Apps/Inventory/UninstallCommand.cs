using System.Text.RegularExpressions;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>Reads facts out of an uninstall command line without running it.</summary>
public static partial class UninstallCommand
{
    [GeneratedRegex(@"msiexec(\.exe)?""?\s+.*?/[IXix]\s*(?<code>\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommand();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidInBraces();

    [GeneratedRegex(@"steam://uninstall/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamUninstall();

    /// <summary>"{GUID}" when the command is "MsiExec.exe /X{GUID}" or "/I{GUID}"; null otherwise.</summary>
    public static string? MsiProductCode(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var m = MsiCommand().Match(command);
        return m.Success ? m.Groups["code"].Value.ToUpperInvariant() : null;
    }

    public static bool IsGuidKeyName(string name) => GuidInBraces().IsMatch(name);

    /// <summary>Steam app id when the command is a "steam://uninstall/&lt;id&gt;" link (Steam's own ARP entries).</summary>
    public static string? SteamAppId(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var m = SteamUninstall().Match(command);
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>
    /// The program an uninstall command starts: the quoted path, or everything up to ".exe" for unquoted paths with
    /// spaces ("C:\Program Files\App\uninst.exe /S"). Null for commands that aren't a local program (msiexec, rundll32, URLs).
    /// </summary>
    public static string? ProgramPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        string path;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end <= 1) return null;
            path = c[1..end];
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
        }
        try { path = Environment.ExpandEnvironmentVariables(path); } catch (ArgumentException) { return null; }
        if (!Path.IsPathFullyQualified(path)) return null;
        var file = Path.GetFileName(path);
        // Windows' own launchers always exist; they say nothing about whether the app's files are still there.
        if (file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || file.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return path;
    }

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe" (DisplayIcon values carry an icon index).</summary>
    public static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma].Trim().Trim('"');
        try { s = Environment.ExpandEnvironmentVariables(s); } catch (ArgumentException) { return null; }
        return Path.IsPathFullyQualified(s) ? s : null;
    }
}
