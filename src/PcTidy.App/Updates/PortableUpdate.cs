using System.IO.Compression;
using PcTidy.App.Shell;

namespace PcTidy.App.Updates;

/// <summary>
/// Updating a portable copy: the checked zip is unpacked into a NEW folder (next to the current one, for example
/// "…\PC Tidy 3.1.2", or else &lt;DataRoot&gt;\updates\PC Tidy 3.1.2). Nothing of the running copy is replaced and
/// nothing from the zip is ever started: the user starts PC Tidy from the new folder. Settings, history and quarantine
/// live in the data folder (%LOCALAPPDATA%\PC Tidy by default), so they carry over.
/// Every entry must stay inside the new folder (zip-slip), names must be plain Windows names, and the entry count and
/// unpacked size are capped (<see cref="MaxEntries"/>, <see cref="MaxUnpackedBytes"/>).
/// </summary>
public static class PortableUpdate
{
    public const int MaxEntries = 5_000;
    public const long MaxUnpackedBytes = 1536L << 20;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string FolderName(Version v) => $"PC Tidy {ReleaseVersion.Show(v)}";

    /// <summary>
    /// Creates the folder the new version goes into and returns it: next to <paramref name="appFolder"/> when that
    /// name is free and the folder can be made there, else inside <paramref name="updatesFolder"/> ("PC Tidy 3.1.2",
    /// "PC Tidy 3.1.2 (2)", …). An existing folder is never reused.
    /// </summary>
    public static string CreateTarget(string appFolder, string updatesFolder, Version v)
    {
        var name = FolderName(v);
        var parent = Path.GetDirectoryName(Path.GetFullPath(appFolder).TrimEnd('\\'));
        if (parent is not null && !InstallFolder.IsProtected(parent) && TryCreateNew(Path.Combine(parent, name)) is { } beside) return beside;
        for (int n = 1; n <= 20; n++)
            if (TryCreateNew(Path.Combine(updatesFolder, n == 1 ? name : $"{name} ({n})")) is { } inUpdates) return inUpdates;
        throw new InvalidOperationException("PC Tidy couldn't make a new folder for the update, so nothing was unpacked.");
    }

    private static string? TryCreateNew(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full) || File.Exists(full)) return null;
            Directory.CreateDirectory(full); // boundary-ok: a new, empty folder for the update
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// The path inside <paramref name="root"/> for a zip entry name, or null when the name could leave the folder or
    /// isn't a plain Windows name (absolute, a drive, "..", ":" streams, device names, trailing dots or spaces).
    /// </summary>
    public static string? SafeDestination(string root, string relative)
    {
        var rel = relative.Replace('\\', '/');
        if (rel.Length == 0 || rel.StartsWith('/') || rel.Contains(':') || rel.Any(char.IsControl)) return null;
        var parts = rel.TrimEnd('/').Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (Reserved.Contains(p.Split('.')[0])) return null;
        }
        var baseDir = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var dest = Path.GetFullPath(Path.Combine(baseDir, string.Join('\\', parts)));
        return dest.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && dest.Length > baseDir.Length ? dest : null;
    }

    /// <summary>
    /// Unpacks <paramref name="zipStream"/> into the empty folder <paramref name="target"/>. One folder that holds
    /// everything (the release zip has "PC Tidy &lt;version&gt;\…") is left out, so the files land directly in
    /// <paramref name="target"/>. Every entry is checked BEFORE anything is written; a bad one stops it all.
    /// Throws <see cref="InvalidDataException"/> with a plain reason. Returns the number of files written.
    /// </summary>
    /// <param name="maxBytes">Tests: a smaller size cap (real: <see cref="MaxUnpackedBytes"/>).</param>
    public static int Unpack(Stream zipStream, string target, long maxBytes = MaxUnpackedBytes, int maxEntries = MaxEntries)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries;
        if (entries.Count == 0) throw new InvalidDataException("The zip is empty.");
        if (entries.Count > maxEntries) throw new InvalidDataException("The zip has far too many files to be PC Tidy.");
        long declared = 0;
        foreach (var e in entries)
        {
            declared += Math.Max(0, e.Length);
            if (declared > maxBytes) throw new InvalidDataException("The zip would unpack to far more than PC Tidy's size.");
        }

        var names = entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        var top = SharedTopFolder(names);
        var plan = new List<(ZipArchiveEntry Entry, string Dest, bool IsFolder)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var rel = top is null ? names[i] : names[i][(top.Length + 1)..];
            if (rel.Length == 0) continue; // the shared top folder itself
            var dest = SafeDestination(target, rel) ?? throw new InvalidDataException($"The zip has a file that would land outside the new folder (\"{Short(names[i])}\"), so nothing was unpacked.");
            plan.Add((entries[i], dest, rel.EndsWith('/')));
        }
        if (plan.Select(p => p.Dest).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count)
            throw new InvalidDataException("The zip has the same file twice, so nothing was unpacked.");

        long written = 0;
        int files = 0;
        var buf = new byte[81920];
        foreach (var (entry, dest, isFolder) in plan)
        {
            if (isFolder) { Directory.CreateDirectory(dest); continue; } // boundary-ok: inside the new update folder
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); // boundary-ok: inside the new update folder
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None); // boundary-ok: inside the new update folder
            long thisFile = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                thisFile += n;
                written += n;
                // Never trust the declared sizes alone: count what really comes out.
                if (thisFile > entry.Length || written > maxBytes) throw new InvalidDataException("The zip unpacks to more than it says, so PC Tidy stopped.");
                dst.Write(buf, 0, n);
            }
            files++;
        }
        return files;
    }

    /// <summary>The one top folder every entry is inside, or null.</summary>
    private static string? SharedTopFolder(IReadOnlyList<string> names)
    {
        string? top = null;
        foreach (var n in names)
        {
            int slash = n.IndexOf('/');
            if (slash <= 0) return null;
            var first = n[..slash];
            if (top is null) top = first;
            else if (!string.Equals(top, first, StringComparison.Ordinal)) return null;
        }
        return top;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>
    /// Creates the new folder and unpacks into it; on any problem the half-made new folder (only that one, which PC
    /// Tidy just created) is removed again and the plain reason is thrown as <see cref="InvalidOperationException"/>.
    /// </summary>
    public static string UnpackIntoNewFolder(Stream zipStream, string appFolder, string updatesFolder, Version v)
    {
        var target = CreateTarget(appFolder, updatesFolder, v);
        try
        {
            Unpack(zipStream, target);
            return target;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any problem (a bad entry, an unsupported compression method: NotSupportedException, a full disk, a name
            // Windows refuses: ArgumentException or PathTooLongException…): the half-made folder goes (review L6).
            try { Directory.Delete(target, recursive: true); } // boundary-ok: only the new folder created above
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException((ex is InvalidDataException ? ex.Message : ex is NotSupportedException ? "The zip uses a kind of packing PC Tidy can't unpack, so nothing was unpacked." : "The zip couldn't be unpacked: " + ex.Message) + " Your current PC Tidy wasn't changed.", ex);
        }
    }
}
