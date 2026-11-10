namespace PcTidy.Engine.IO;

/// <summary>
/// Converts user paths to Win32 extended-length form (<c>\\?\D:\...</c>) for every file operation.
/// Why: paths over 260 characters, and names with trailing dots/spaces or reserved names (CON, NUL...),
/// are silently mangled or refused by the normal Win32 path parser. The extended form skips that parser.
/// Every file operation and probe (stat, hash, in-use, enumeration) goes through <see cref="Io"/>.
/// User-facing text (journal, manifests, messages) always uses <see cref="Display"/>.
/// </summary>
internal static class LongPath
{
    private const string Prefix = @"\\?\";

    /// <summary>
    /// The literal extended form of a plain drive-letter path (<c>D:\...</c>, or that same path already in
    /// <c>\\?\D:\...</c> form). Everything else is refused, never rewritten:
    /// <list type="bullet">
    /// <item>paths that would need normalising (relative, "..", ".", "/", doubled separators, a stream ':'), because
    /// the engine must act on exactly the path the SafetyGuard judged ("a." and "a" are different names here);</item>
    /// <item>device and object-manager forms (<c>\\.\</c>, <c>\\?\GLOBALROOT</c>, <c>\\?\Volume{…}</c>, <c>\??\</c>), whose
    /// "..", volume and device names resolve somewhere other than the text says (<c>\\.\C:\..\HarddiskVolume3\x</c> is a
    /// file on another drive);</item>
    /// <item>UNC paths (<c>\\server\share</c>, <c>\\?\UNC\…</c>): PC Tidy only changes files on this PC's own drives.</item>
    /// </list>
    /// </summary>
    public static string Io(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty path.", nameof(path));
        var plain = path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
        if (!IsPlainDrivePath(plain)) throw new ArgumentException("PC Tidy only works with plain full paths on this PC's drives.", nameof(path));
        if (plain.Length > 3) plain = plain.TrimEnd('\\');
        return Prefix + plain;
    }

    public static string Display(string path)
        => path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;

    /// <summary>Case-insensitive comparison of two paths in either form.</summary>
    public static bool Same(string a, string b)
        => string.Equals(Display(Io(a)), Display(Io(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> equals or is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Display(Io(path));
        var f = Display(Io(folder)).TrimEnd('\\');
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"X:\" followed by nothing that needs normalising. Device, object-manager and UNC forms never match.</summary>
    private static bool IsPlainDrivePath(string p)
        => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\' && !NeedsNormalising(p);

    private static bool NeedsNormalising(string p)
        => p.Contains('/') || p.IndexOf(':', 2) >= 0 || p.Contains(@"\.\") || p.Contains(@"\..\") || p.EndsWith(@"\..") || p.EndsWith(@"\.")
           || p.IndexOf(@"\\", 1, StringComparison.Ordinal) >= 0;
}
