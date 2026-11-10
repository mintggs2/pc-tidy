namespace PcTidy.Features.Services.Sources;

/// <summary>
/// Finds the file that really is a service: the DLL for services hosted in svchost, the program in ImagePath for
/// the rest, the .sys file for drivers. Reads Windows' own path forms (quotes, unquoted Program Files paths,
/// <c>%SystemRoot%</c>, <c>\SystemRoot\</c>, <c>\??\</c>, <c>System32\drivers\x.sys</c>). Environment variables are
/// expanded from <see cref="IKnownLocations"/> first, so the sandbox resolves into its own folders, never the real PC's.
/// </summary>
public static class ServiceBinary
{
    private static readonly string[] Extensions = { ".exe", ".sys", ".dll", ".com" };

    /// <param name="realPc">True to fall back to this process's environment for variables the locations don't know.</param>
    public static string? Of(ServiceRecord r, IKnownLocations locations, bool realPc)
    {
        var program = ProgramOf(r.ImagePath, locations, realPc);
        if (!string.IsNullOrWhiteSpace(r.ServiceDll) && IsServiceHost(program))
            return Normalize(r.ServiceDll!, locations, realPc);
        if (program is null && r.IsDriver)
            return Path.Combine(locations.WindowsDir, "System32", "drivers", r.Name + ".sys");
        return program;
    }

    /// <summary>True for svchost.exe (a shared host: its own publisher says nothing about the service).</summary>
    public static bool IsServiceHost(string? program)
        => program is not null && Path.GetFileName(program).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The program part of a service command line, as a full path; null for an empty command.</summary>
    public static string? ProgramOf(string? imagePath, IKnownLocations locations, bool realPc)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = Expand(imagePath.Trim(), locations, realPc);
        string exe;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            exe = close < 0 ? text[1..] : text[1..close];
        }
        else exe = text[..EndOfUnquotedProgram(text)];
        exe = exe.Trim();
        return exe.Length == 0 ? null : Normalize(exe, locations, realPc);
    }

    /// <summary>Just after the first known extension followed by a space or the end, else the first space.</summary>
    private static int EndOfUnquotedProgram(string text)
    {
        var best = -1;
        foreach (var ext in Extensions)
        {
            var from = 0;
            while (from < text.Length)
            {
                var at = text.IndexOf(ext, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var end = at + ext.Length;
                if (end == text.Length || text[end] is ' ' or '\t')
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

    public static string Normalize(string path, IKnownLocations locations, bool realPc)
    {
        var p = Expand(path.Trim().Trim('"'), locations, realPc);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(locations.WindowsDir, p[@"\SystemRoot\".Length..]);
        if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(locations.WindowsDir, p[@"SystemRoot\".Length..]);
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(locations.WindowsDir, p);
        // A bare file name is found in System32 (where Windows looks first for services).
        if (p.Length > 0 && !p.Contains('\\') && !p.Contains('/')) return Path.Combine(locations.WindowsDir, "System32", p);
        return p;
    }

    private static string Expand(string s, IKnownLocations l, bool realPc)
    {
        if (!s.Contains('%')) return s;
        var map = new (string Var, string Value)[]
        {
            ("%SystemRoot%", l.WindowsDir), ("%windir%", l.WindowsDir), ("%SystemDrive%", l.SystemDrive.TrimEnd('\\')),
            ("%ProgramFiles(x86)%", l.ProgramFilesX86), ("%ProgramW6432%", l.ProgramFiles), ("%ProgramFiles%", l.ProgramFiles),
            ("%CommonProgramFiles%", Path.Combine(l.ProgramFiles, "Common Files")), ("%ProgramData%", l.ProgramData),
            ("%ALLUSERSPROFILE%", l.ProgramData), ("%LOCALAPPDATA%", l.LocalAppData), ("%APPDATA%", l.RoamingAppData),
            ("%USERPROFILE%", l.UserProfile), ("%TEMP%", l.UserTemp), ("%TMP%", l.UserTemp),
        };
        foreach (var (v, value) in map) s = s.Replace(v, value, StringComparison.OrdinalIgnoreCase);
        return realPc && s.Contains('%') ? Environment.ExpandEnvironmentVariables(s) : s;
    }
}
