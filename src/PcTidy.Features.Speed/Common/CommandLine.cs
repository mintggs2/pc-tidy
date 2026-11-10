namespace PcTidy.Features.Speed.Common;

/// <summary>A startup command split into the program and its arguments, plus the file that really runs.</summary>
/// <param name="Executable">The program as written (expanded), or null for an empty command.</param>
/// <param name="Arguments">Everything after the program.</param>
/// <param name="TargetFile">
/// The file worth checking for publisher and signature: the program itself, or the DLL a <c>rundll32</c> line loads,
/// or the script a script host runs.
/// </param>
public sealed record ParsedCommand(string? Executable, string Arguments, string? TargetFile)
{
    public static readonly ParsedCommand Empty = new(null, "", null);
}

/// <summary>
/// Parses Run-key and service command lines the way Windows reads them: quoted paths, unquoted paths with spaces
/// (Program Files), bare program names, environment variables, <c>\SystemRoot\</c> and <c>\??\</c> prefixes, and
/// host programs (rundll32, script hosts). Never runs anything and never parses localized text.
/// </summary>
public static class CommandLine
{
    private static readonly string[] ProgramExtensions = { ".exe", ".com", ".bat", ".cmd", ".lnk", ".vbs", ".js", ".ps1", ".scr", ".pif" };
    private static readonly string[] ScriptHosts = { "wscript.exe", "cscript.exe", "mshta.exe" };

    /// <param name="raw">The command as stored.</param>
    /// <param name="windowsDir">Windows folder, for bare names (<c>ctfmon.exe</c>) and <c>\SystemRoot\</c> paths.</param>
    /// <param name="searchPath">Extra folders Windows searches for bare names (the PATH), after System32 and the Windows folder.</param>
    public static ParsedCommand Parse(string? raw, string windowsDir, IReadOnlyList<string>? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ParsedCommand.Empty;
        var text = Expand(raw.Trim());

        string exe, args;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            exe = close < 0 ? text[1..] : text[1..close];
            args = close < 0 ? "" : text[(close + 1)..].Trim();
        }
        else
        {
            var cut = EndOfUnquotedProgram(text);
            exe = text[..cut].Trim();
            args = text[cut..].Trim();
        }

        exe = NormalizePath(exe, windowsDir, searchPath);
        return new ParsedCommand(exe, args, TargetOf(exe, args, windowsDir, searchPath));
    }

    /// <summary>
    /// End of an unquoted program path: just after the first program extension that is followed by a space or the
    /// end (so <c>C:\Program Files\App\app.exe -min</c> keeps its spaces), else the first space.
    /// </summary>
    private static int EndOfUnquotedProgram(string text)
    {
        var best = -1;
        foreach (var ext in ProgramExtensions)
        {
            var from = 0;
            while (from < text.Length)
            {
                var at = text.IndexOf(ext, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var end = at + ext.Length;
                if (end == text.Length || text[end] is ' ' or '\t' or ',')
                {
                    if (best < 0 || end < best) best = end;
                    break;
                }
                from = end;
            }
        }
        if (best > 0) return best;
        var space = text.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? text.Length : space;
    }

    /// <summary>Expands %VARS%, turns <c>\SystemRoot\</c>, <c>\??\</c> and bare/relative names into full paths.</summary>
    public static string NormalizePath(string path, string windowsDir, IReadOnlyList<string>? searchPath = null)
    {
        var p = Expand(path.Trim().Trim('"'));
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windowsDir, p[@"\SystemRoot\".Length..]);
        else if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windowsDir, p[@"SystemRoot\".Length..]);
        else if (p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(windowsDir, p);
        else if (p.Length > 0 && !p.Contains('\\') && !p.Contains('/'))
        {
            // A bare program name is found the way Windows finds it: System32, the Windows folder, then the PATH.
            var name = Path.HasExtension(p) ? p : p + ".exe";
            var system32 = Path.Combine(windowsDir, "System32", name);
            var candidates = new[] { system32, Path.Combine(windowsDir, name) }
                .Concat((searchPath ?? Array.Empty<string>()).Where(d => d.Length > 0 && d.IndexOfAny(Path.GetInvalidPathChars()) < 0).Select(d => Path.Combine(d, name)));
            p = candidates.FirstOrDefault(SafeExists) ?? system32;
        }
        return p;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static string? TargetOf(string exe, string args, string windowsDir, IReadOnlyList<string>? searchPath)
    {
        var name = Path.GetFileName(exe);
        if (name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            // rundll32 "C:\path\x.dll",Entry args  |  rundll32 C:\path\x.dll,Entry
            var first = FirstArgument(args, stopAtComma: true);
            return string.IsNullOrEmpty(first) ? exe : NormalizePath(first, windowsDir, searchPath);
        }
        if (ScriptHosts.Any(h => name.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            var script = FirstArgument(args, stopAtComma: false, skipSwitches: true);
            return string.IsNullOrEmpty(script) ? exe : NormalizePath(script, windowsDir, searchPath);
        }
        return exe;
    }

    private static string FirstArgument(string args, bool stopAtComma, bool skipSwitches = false)
    {
        var rest = args.Trim();
        while (rest.Length > 0)
        {
            string token;
            if (rest.StartsWith('"'))
            {
                var close = rest.IndexOf('"', 1);
                token = close < 0 ? rest[1..] : rest[1..close];
                rest = close < 0 ? "" : rest[(close + 1)..].Trim();
            }
            else
            {
                var stops = stopAtComma ? new[] { ' ', '\t', ',' } : new[] { ' ', '\t' };
                var end = rest.IndexOfAny(stops);
                token = end < 0 ? rest : rest[..end];
                rest = end < 0 ? "" : rest[(end + 1)..].Trim();
            }
            if (skipSwitches && (token.StartsWith('/') || token.StartsWith("//", StringComparison.Ordinal))) continue;
            return token.Trim();
        }
        return "";
    }

    private static string Expand(string s) => s.Contains('%') ? Environment.ExpandEnvironmentVariables(s) : s;
}
