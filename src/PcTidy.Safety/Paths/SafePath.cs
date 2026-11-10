using PcTidy.Safety.Native;

namespace PcTidy.Safety.Paths;

/// <summary>
/// Turns a spelling of a local path into the ONE path the engine will act on, so a crafted path can't slip past
/// a rule. The engine always works through the literal <c>\\?\</c> form (PcTidy.Engine LongPath.Io), so nothing
/// here ever trims or guesses: a name that Windows could read two ways is refused instead.
/// <list type="bullet">
/// <item><c>\\?\C:\…</c> is literal: no "." / ".." segments, no "/", no empty segments.</item>
/// <item><c>C:\…</c> and <c>\\.\C:\…</c> go through Windows' normal path rules: "/" and "." / ".." are resolved
/// exactly like GetFullPathName does. Those rules also drop trailing dots and spaces, so such names are refused there.</item>
/// <item>Any ":" after the drive (alternate data streams like <c>file:hidden</c>) is refused.</item>
/// <item>A segment ending in "." or " ", or a reserved device name (CON, NUL, COM1…), is refused, except as the
/// LAST segment of a path that needs no normalising, and only when the caller asks for it (rename and quarantine
/// sources, so odd names can be fixed or cleaned).</item>
/// <item><c>\??\</c>, <c>\\?\GLOBALROOT</c>, network and other device paths are refused. Volume-GUID paths map to their drive letter;
/// SUBST drives map to the folder they stand for; 8.3 short names expand while the path is walked (see <see cref="PathProbe.Walk"/>).</item>
/// </list>
/// </summary>
public static class SafePath
{
    /// <summary>Result of parsing a path: either a canonical full path or a plain-English problem.</summary>
    /// <param name="OddLeaf">The last segment ends in a dot/space or is a reserved device name (only when allowed).</param>
    public readonly record struct Result(string? Path, string? Problem, bool OddLeaf = false)
    {
        public bool Ok => Path is not null;
    }

    public const string NotAFullPath = "PC Tidy couldn't work out exactly where this is, so it won't touch it.";
    public const string NetworkPath = "This is on a network location. PC Tidy only changes files on this PC's own drives.";
    public const string DevicePath = "This path points at a device, not a normal folder, so PC Tidy won't touch it.";
    public const string StreamPath = "This points at a hidden stream inside a file. PC Tidy only touches whole files and folders.";
    public const string OddSegment = "A folder on this path has a name ending in a dot or space, or a reserved name like CON. Windows reads such names two ways, so PC Tidy won't go through it.";
    public const string OddLeafNotAllowed = "This name ends in a dot or space, or is a reserved name like CON. PC Tidy can only rename it or move it to quarantine.";

    /// <summary>Longest path Windows accepts in the \\?\ form.</summary>
    private const int MaxPath = 32_000;

    /// <summary>Characters no real name can hold (control characters, wildcards, quotes, pipes). ':' is handled separately.</summary>
    private static readonly char[] InvalidNameChars = System.IO.Path.GetInvalidFileNameChars().Where(c => c is not '\\' and not ':').ToArray();

    /// <summary>
    /// Canonical full path (drive letter upper-case, no trailing separator, long names expanded where the path exists),
    /// or a problem. Odd last segments are refused; use <see cref="Parse"/> with <c>allowOddLeaf</c> for rename/quarantine sources.
    /// </summary>
    public static Result Normalize(string? raw)
    {
        var parsed = Parse(raw, allowOddLeaf: false);
        return parsed.Ok ? parsed with { Path = PathProbe.Walk(parsed.Path!).Path } : parsed;
    }

    /// <summary>Normalised path, or null when the path can't be understood (callers treat null as "block").</summary>
    public static string? TryNormalize(string? raw) => Normalize(raw).Path;

    /// <summary>
    /// Lexical part of normalising: prefixes, separators, "." / "..", streams, odd names, volume GUIDs and SUBST drives.
    /// No file-system walk (short names are expanded later by <see cref="PathProbe.Walk"/>).
    /// </summary>
    public static Result Parse(string? raw, bool allowOddLeaf)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxPath || raw.IndexOf('\0') >= 0) return new(null, NotAFullPath);
        var s = raw;
        bool literal = false, win32Rules = true;

        if (StartsWith(s, @"\\?\UNC\") || StartsWith(s, @"\\.\UNC\")) return new(null, NetworkPath);
        if (StartsWith(s, @"\??\")) return new(null, NotAFullPath); // NT spelling: the engine doesn't take it, so neither do we
        if (StartsWith(s, @"\\?\")) { s = s[4..]; literal = true; win32Rules = false; }
        // Device form (\\.\): refused outright. Windows lets "..'" climb above the drive there
        // (\\.\C:\..\HarddiskVolume3\x is really C:\x), so no lexical reading of it can be trusted.
        else if (StartsWith(s, @"\\.\")) return new(null, DevicePath);

        if (StartsWith(s, "Volume{"))
        {
            var mapped = MapVolumeGuidPath(s);
            if (mapped is null) return new(null, DevicePath);
            s = mapped;
        }

        if (literal && s.Contains('/')) return new(null, NotAFullPath);
        var hadSlash = s.Contains('/');
        if (hadSlash) s = s.Replace('/', '\\');
        if (StartsWith(s, @"\\")) return new(null, NetworkPath);
        if (s.Length < 3 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || s[2] != '\\') return new(null, literal ? DevicePath : NotAFullPath);

        var rest = s.AsSpan(3);
        if (rest.IndexOf(':') >= 0) return new(null, StreamPath);
        if (rest.IndexOfAny(InvalidNameChars) >= 0) return new(null, NotAFullPath);

        // Split, then resolve "." / ".." / empty segments the way Windows does for non-literal paths.
        var parts = s[3..].Split('\\');
        var trailingSeparators = 0;
        for (var i = parts.Length - 1; i >= 0 && parts[i].Length == 0; i--) trailingSeparators++;
        var segments = new List<string>(parts.Length);
        var needsWin32Normalising = hadSlash || trailingSeparators > 1;
        for (var i = 0; i < parts.Length - trailingSeparators; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part is "." or "..")
            {
                if (literal) return new(null, NotAFullPath); // \\?\ takes these literally: no real name looks like that
                needsWin32Normalising = true;
                if (part == ".." && segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(part);
        }

        var oddLeaf = false;
        for (var i = 0; i < segments.Count; i++)
        {
            if (!IsOddName(segments[i])) continue;
            if (i < segments.Count - 1) return new(null, OddSegment);
            // Last segment: only literal-safe spellings (the engine uses them as-is) and only when the caller allows it.
            if (needsWin32Normalising || (win32Rules && !IsPlainForm(raw))) return new(null, OddSegment);
            if (!allowOddLeaf) return new(null, OddLeafNotAllowed);
            oddLeaf = true;
        }

        var root = char.ToUpperInvariant(s[0]) + @":\";
        var path = root + string.Join('\\', segments);
        path = ResolveSubstDrive(path);
        if (path is null) return new(null, NotAFullPath);
        return new(path, null, oddLeaf);
    }

    /// <summary>
    /// True for a name Windows' normal path rules would change or treat as a device: ends in "." or " ",
    /// or is CON, PRN, AUX, NUL, COM0-9, LPT0-9 (also with ¹²³ and with an extension, like "nul.txt"), CONIN$, CONOUT$.
    /// </summary>
    public static bool IsOddName(string name)
    {
        if (name.Length == 0) return false;
        if (name[^1] is '.' or ' ') return true;
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (stem.Length == 3)
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³';
        return stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "C:\a\b" that the engine passes straight to Windows as \\?\C:\a\b (mirrors LongPath.Io: no "/", no "\.\",
    /// no "\..\", no doubled separators, fully qualified, no prefix).
    /// </summary>
    private static bool IsPlainForm(string raw)
        => raw.Length >= 3 && char.IsAsciiLetter(raw[0]) && raw[1] == ':' && raw[2] == '\\'
           && !raw.Contains('/') && !raw.Contains(@"\.\", StringComparison.Ordinal) && !raw.Contains(@"\..\", StringComparison.Ordinal)
           && !raw.EndsWith(@"\..", StringComparison.Ordinal) && !raw.EndsWith(@"\.", StringComparison.Ordinal)
           && raw.IndexOf(@"\\", 1, StringComparison.Ordinal) < 0;

    /// <summary>True if <paramref name="path"/> equals or is inside <paramref name="folder"/>. Both must already be normalised.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return IsStrictlyUnder(path, folder);
    }

    /// <summary>True if <paramref name="path"/> is inside (not equal to) <paramref name="folder"/>. Both normalised.</summary>
    public static bool IsStrictlyUnder(string path, string folder)
    {
        if (path.Length <= folder.Length) return false;
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        return folder.EndsWith('\\') || path[folder.Length] == '\\';
    }

    public static bool SameAs(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parent folder of a normalised path, or null for a drive root.</summary>
    public static string? Parent(string normalized)
    {
        if (normalized.Length <= 3) return null;
        var i = normalized.LastIndexOf('\\');
        if (i < 0) return null;
        return i == 2 ? normalized[..3] : normalized[..i];
    }

    /// <summary>Last segment of a normalised path ("" for a drive root).</summary>
    public static string Leaf(string normalized)
        => normalized.Length <= 3 ? "" : normalized[(normalized.LastIndexOf('\\') + 1)..];

    /// <summary>The path and all its ancestors, from the drive root down to the path itself.</summary>
    public static IEnumerable<string> SelfAndAncestorsTopDown(string normalized)
    {
        var chain = new List<string>();
        for (var p = normalized; p is not null; p = Parent(p)) chain.Add(p);
        chain.Reverse();
        return chain;
    }

    /// <summary>"C:\a" + "b" → "C:\a\b"; "C:\" + "b" → "C:\b".</summary>
    public static string Join(string folder, string name) => folder.EndsWith('\\') ? folder + name : folder + "\\" + name;

    /// <summary>The \\?\ form the engine uses for <paramref name="normalized"/>: probes use it so names are read literally.</summary>
    public static string Extended(string normalized) => @"\\?\" + normalized;

    private static bool StartsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ SUBST drives

    private static readonly TimeSpan SubstLifetime = TimeSpan.FromSeconds(10);
    private sealed record SubstEntry(long CheckedTicks, string? Target);
    private static readonly SubstEntry?[] SubstCache = new SubstEntry?[26];

    /// <summary>A SUBST drive (e.g. S: → C:\Windows) is rewritten to the real folder it stands for (chains followed, at most 4 deep).</summary>
    private static string? ResolveSubstDrive(string path)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            var target = SubstTarget(path[0]);
            if (target is null) return path;
            path = path.Length > 3 ? Join(target, path[3..]) : target;
        }
        return null; // a SUBST loop: refuse
    }

    private static string? SubstTarget(char letter)
    {
        var slot = char.ToUpperInvariant(letter) - 'A';
        if (slot is < 0 or >= 26) return null;
        var now = Environment.TickCount64;
        var cached = Volatile.Read(ref SubstCache[slot]);
        if (cached is not null && now - cached.CheckedTicks < SubstLifetime.TotalMilliseconds) return cached.Target;
        var target = QuerySubst((char)('A' + slot));
        Volatile.Write(ref SubstCache[slot], new SubstEntry(now, target));
        return target;
    }

    private static string? QuerySubst(char letter)
    {
        var buf = new char[1024];
        var n = NativeMethods.QueryDosDevice(letter + ":", buf, (uint)buf.Length);
        if (n == 0) return null;
        var first = new string(buf, 0, (int)n).Split('\0')[0];
        // Subst targets look like "\??\C:\folder"; real volumes look like "\Device\HarddiskVolume3".
        if (!first.StartsWith(@"\??\", StringComparison.Ordinal) || first.Length < 7) return null;
        var target = first[4..].TrimEnd('\\');
        if (target.Length < 2 || !char.IsAsciiLetter(target[0]) || target[1] != ':') return null;
        return target.Length == 2 ? target + "\\" : char.ToUpperInvariant(target[0]) + target[1..];
    }

    // ------------------------------------------------------------------ volume GUID paths

    /// <summary>"Volume{guid}\rest" → "X:\rest" using the volume's drive-letter mount, or null if it has none.</summary>
    private static string? MapVolumeGuidPath(string s)
    {
        var close = s.IndexOf('}');
        if (close < 0) return null;
        var volume = @"\\?\" + s[..(close + 1)] + "\\";
        var rest = s.Length > close + 2 ? s[(close + 2)..] : "";
        if (s.Length > close + 1 && s[close + 1] != '\\') return null;
        var buf = new char[1024];
        if (!NativeMethods.GetVolumePathNamesForVolumeName(volume, buf, (uint)buf.Length, out var len) || len == 0) return null;
        var mount = new string(buf, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(m => m.Length == 3 && m[1] == ':');
        return mount is null ? null : mount + rest;
    }
}
