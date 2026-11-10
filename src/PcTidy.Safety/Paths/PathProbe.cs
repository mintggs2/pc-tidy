using System.Runtime.InteropServices;
using PcTidy.Safety.Native;

namespace PcTidy.Safety.Paths;

/// <summary>What the file system says about one path, read without opening it (so OneDrive files are never downloaded).</summary>
public readonly record struct PathFacts(bool Exists, FileAttributes Attributes, uint ReparseTag)
{
    public static readonly PathFacts Missing = new(false, 0, 0);

    /// <summary>
    /// Tag used when a reparse point's real tag couldn't be read (its folder can't be listed). It counts as a link:
    /// when in doubt, PC Tidy doesn't go through it.
    /// </summary>
    public const uint UnknownReparseTag = 0xFFFFFFFF;

    public bool IsReparsePoint => Exists && (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Junctions, symbolic links, mount points and other "this is really somewhere else" links. Cloud placeholders
    /// (OneDrive), dedup and compressed system files are reparse points too, but they are not redirections.
    /// </summary>
    public bool IsLink => IsReparsePoint && (ReparseTag & NativeMethods.ReparseTagNameSurrogateBit) != 0;

    public bool IsSystem => Exists && (Attributes & FileAttributes.System) != 0;

    public bool IsDirectory => Exists && (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Result of walking a path from its drive root, one segment at a time.
/// </summary>
/// <param name="Path">Canonical path: every existing segment spelled as the file system stores it (8.3 short names expanded),
/// the rest kept as given.</param>
/// <param name="Self">Facts about the path itself (Missing when it, or a folder above it, doesn't exist).</param>
/// <param name="LinkAt">First junction/symlink on the way (the path itself included), or null. The walk stops there.</param>
/// <param name="Unreadable">A folder on the way couldn't be listed, so links below it couldn't be ruled out.</param>
public readonly record struct WalkResult(string Path, PathFacts Self, string? LinkAt, bool Unreadable);

/// <summary>
/// Read-only attribute probes. Never follows links, never opens file contents. Every probe uses the literal
/// <c>\\?\</c> form, exactly like the engine, so "lnk." is looked up as "lnk." and never as "lnk".
/// </summary>
public static class PathProbe
{
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorDirectory = 267, ErrorAccessDenied = 5;

    /// <summary>
    /// Walks <paramref name="canonical"/> (from <see cref="SafePath.Parse"/>) top-down with one cheap lookup per segment:
    /// expands short names, spots links on the way, and reads the item's own attributes, all in a single pass.
    /// </summary>
    public static WalkResult Walk(string canonical)
    {
        var root = canonical[..3];
        if (canonical.Length <= 3) return new(root, InspectRoot(root), null, false);
        var segments = canonical[3..].Split('\\');
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var probe = SafePath.Join(current, segments[i]);
            var (facts, longName, error) = ProbeOne(probe);
            if (!facts.Exists)
            {
                var rest = string.Join('\\', segments, i, segments.Length - i);
                var unreadable = !IsNotFound(error);
                return new(SafePath.Join(current, rest), PathFacts.Missing, null, unreadable);
            }
            current = SafePath.Join(current, longName ?? segments[i]);
            if (facts.IsLink)
            {
                // Nothing below a link is looked at: it would be somewhere else entirely.
                var rest = i + 1 < segments.Length ? "\\" + string.Join('\\', segments, i + 1, segments.Length - i - 1) : "";
                return new(current + rest, i == segments.Length - 1 ? facts : PathFacts.Missing, current, false);
            }
            if (i == segments.Length - 1) return new(current, facts, null, false);
        }
        return new(current, PathFacts.Missing, null, false); // unreachable: the loop always returns
    }

    /// <summary>True for the "it isn't there" errors (as opposed to "couldn't look": access denied, bad name, not ready).</summary>
    internal static bool IsNotFound(int error) => error is ErrorFileNotFound or ErrorPathNotFound or ErrorDirectory;

    /// <summary>Attributes of one already-canonical path (no walk above it).</summary>
    public static PathFacts Inspect(string normalized)
        => normalized.Length <= 3 ? InspectRoot(normalized) : ProbeOne(normalized).Facts;

    /// <summary>
    /// One item: its facts and, when it was looked up by listing, its name as stored (long form).
    /// Fast path: a name lookup (GetFileAttributesEx), which needs no right to list the folder. A full listing
    /// (FindFirstFile) is only needed for a reparse point (to tell a link from a OneDrive placeholder by its tag)
    /// and for a name that may be an 8.3 short name ("PROGRA~1", which must be expanded before rules compare it).
    /// Generated short names always contain "~"; custom ones without it can only be set with admin rights.
    /// </summary>
    /// <returns>Error: 0, a "not found" error (the item is missing), or why it couldn't be checked.</returns>
    internal static (PathFacts Facts, string? LongName, int Error) ProbeOne(string normalized)
    {
        var literal = SafePath.Extended(normalized);
        if (!NativeMethods.GetFileAttributesEx(literal, NativeMethods.GetFileExInfoStandard, out var info))
            return (PathFacts.Missing, null, Marshal.GetLastWin32Error());
        var attrs = (FileAttributes)info.dwFileAttributes;
        var isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
        var maybeShortName = SafePath.Leaf(normalized).Contains('~');
        if (!isReparse && !maybeShortName) return (new PathFacts(true, attrs, 0), null, 0);

        var h = NativeMethods.FindFirstFileEx(literal, NativeMethods.FindExInfoBasic, out var data,
            NativeMethods.FindExSearchNameMatch, IntPtr.Zero, 0);
        if (h == NativeMethods.InvalidHandle)
        {
            // The folder can't be listed (e.g. Windows\Temp for a standard user): an unknown tag counts as a link,
            // and a possible short name stays "couldn't check".
            var error = Marshal.GetLastWin32Error();
            if (maybeShortName) return (PathFacts.Missing, null, IsNotFound(error) ? ErrorAccessDenied : error);
            return (new PathFacts(true, attrs, PathFacts.UnknownReparseTag), null, 0);
        }
        NativeMethods.FindClose(h);
        attrs = (FileAttributes)data.dwFileAttributes;
        var tag = (attrs & FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0u;
        return (new PathFacts(true, attrs, tag), string.IsNullOrEmpty(data.cFileName) ? null : data.cFileName, 0);
    }

    /// <summary>FindFirstFile can't describe a drive root; roots are never links.</summary>
    private static PathFacts InspectRoot(string root)
    {
        try { return Directory.Exists(root) ? new PathFacts(true, File.GetAttributes(root), 0) : PathFacts.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PathFacts.Missing; }
    }
}
