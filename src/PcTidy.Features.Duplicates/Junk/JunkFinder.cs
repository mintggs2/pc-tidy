using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Junk;

/// <summary>
/// Empty folders, zero-byte files and broken shortcuts, from the walk's inventory. Conservative on purpose:
/// anything Windows or an app might rely on (desktop.ini-only folders, marker files like .gitkeep or .nomedia,
/// lock files, installer-managed or network shortcuts, things changed in the last day) is left out.
/// </summary>
public sealed class JunkFinder
{
    /// <summary>Things changed this recently may be in use right now ("New folder" you're about to fill).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    /// <summary>Empty files that mean something to an app or tool just by existing.</summary>
    private static readonly HashSet<string> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitkeep", ".keep", ".gitignore", ".gitattributes", ".gitmodules", ".npmignore", ".dockerignore", ".nomedia",
        ".noindex", ".nosync", ".metadata_never_index", ".placeholder", ".empty", ".exists", ".stignore", ".stfolder",
        ".dropbox", "__init__.py", "py.typed", "CACHEDIR.TAG", "desktop.ini", "Thumbs.db", ".hidden", ".lock", "lock",
        "LOCK", ".DS_Store", ".localized", "NUL", ".editorconfig", ".prettierrc", ".eslintignore", "requirements.txt",
        ".env", "yarn.lock", "Gemfile.lock",
    };

    private static readonly HashSet<string> MarkerExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".lock", ".lck", ".pid", ".sock", ".flag", ".marker", ".keep", ".ready", ".done", ".tag", ".stamp" };

    private readonly DateTime _cutoffUtc;

    public JunkFinder(DateTime nowUtc) => _cutoffUtc = nowUtc - MinimumAge;

    public long NetworkShortcuts;
    public long UncheckableShortcuts;

    // ------------------------------------------------------------------ empty folders

    /// <summary>
    /// Folders with nothing in them at any depth. Only the outermost one is listed (its empty subfolders go with it).
    /// Each candidate is listed again right now, so a folder the walk couldn't read never looks "empty".
    /// </summary>
    public IEnumerable<JunkItem> EmptyFolders(RootInventory inv, CancellationToken ct)
    {
        var dirs = inv.Dirs;
        var empty = new bool[dirs.Count];
        var nestedCount = new int[dirs.Count];
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var d = dirs[i];
            bool isEmpty = d.AllFiles == 0 && !d.HasOpaqueContent && d.Children.All(c => empty[c]);
            empty[i] = isEmpty;
            if (isEmpty) nestedCount[i] = d.Children.Sum(c => 1 + nestedCount[c]);
        }
        // Windows marks special folders with System or ReadOnly; hidden empty folders often belong to an app.
        bool Listable(DirRec d) => (d.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Hidden)) == 0
                                   && Newest(d.WriteUtc, d.CreationUtc) <= _cutoffUtc;
        var listable = new bool[dirs.Count];
        for (int i = 1; i < dirs.Count; i++) listable[i] = empty[i] && Listable(dirs[i]);
        for (int i = 1; i < dirs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = dirs[i];
            // Only the outermost listable one: its empty subfolders go with it.
            if (!listable[i] || listable[d.Parent]) continue;
            if (!StillEmpty(d.Path, depth: 0)) continue;
            var nested = nestedCount[i];
            yield return new JunkItem
            {
                Kind = JunkKind.EmptyFolder, Path = d.Path, LastWriteUtc = d.WriteUtc, NestedEmptyFolders = nested,
                Reason = nested == 0
                    ? "An empty folder. Nothing is inside it."
                    : $"An empty folder. The {nested} folder{(nested == 1 ? "" : "s")} inside {(nested == 1 ? "is" : "are")} empty too.",
            };
        }
    }

    private static DateTime Newest(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Fresh read-only listing: no files, no links, only empty subfolders.</summary>
    private static bool StillEmpty(string dir, int depth)
    {
        if (depth > 64) return false;
        try
        {
            foreach (var entry in new DirectoryInfo(Win32Path.Raw(dir)).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                if (entry is not DirectoryInfo sub || (sub.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (!StillEmpty(System.IO.Path.Combine(dir, sub.Name), depth + 1)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ zero-byte files

    public IEnumerable<JunkItem> ZeroByteFiles(RootInventory inv)
    {
        foreach (var f in inv.Files)
        {
            if (f.Size != 0 || f.IsSystem || f.IsHidden) continue;
            if (IsMarker(f.Name)) continue;
            if (Newest(f.WriteUtc, f.CreationUtc) > _cutoffUtc) continue;
            yield return new JunkItem
            {
                Kind = JunkKind.ZeroByteFile, Path = f.Path, LastWriteUtc = f.WriteUtc,
                Reason = "This file is empty (0 bytes). There's nothing in it to lose.",
            };
        }
    }

    public static bool IsMarker(string name)
        => MarkerNames.Contains(name) || MarkerExtensions.Contains(System.IO.Path.GetExtension(name))
           || name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".~lock.", StringComparison.Ordinal);

    // ------------------------------------------------------------------ broken shortcuts

    public IEnumerable<JunkItem> BrokenShortcuts(RootInventory inv, CancellationToken ct)
    {
        foreach (var f in inv.Files)
        {
            if (!f.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.Size == 0 || f.IsSystem) continue;
            ct.ThrowIfCancellationRequested();
            var target = ShortcutReader.Read(f.Path);
            switch (Check(target))
            {
                case TargetState.Missing:
                    yield return new JunkItem
                    {
                        Kind = JunkKind.BrokenShortcut, Path = f.Path, LastWriteUtc = f.WriteUtc, Size = f.Size, Target = target.Path,
                        Reason = $"Points to {target.Path}, which isn't there any more. Opening it only shows an error.",
                    };
                    break;
                case TargetState.Network: NetworkShortcuts++; break;
                case TargetState.Unknown: UncheckableShortcuts++; break;
            }
        }
    }

    internal enum TargetState { Exists, Missing, Network, Unknown }

    /// <summary>
    /// Broken only when PC Tidy is sure: every path the shortcut records is missing, on a fixed drive that's there, and
    /// the main path's spelling is certain (not code-page text with letters that may have been decoded differently).
    /// </summary>
    internal static TargetState Check(ShortcutTarget target)
    {
        if (target.Kind == ShortcutTargetKind.Network) return TargetState.Network;
        if (target.Kind != ShortcutTargetKind.Local || target.Path is null) return TargetState.Unknown;
        var state = CheckPath(target.Path);
        if (state != TargetState.Missing) return state;
        foreach (var other in target.OtherPaths)
            if (CheckPath(other) == TargetState.Exists) return TargetState.Exists;
        return target.MaybeMisread ? TargetState.Unknown : TargetState.Missing;
    }

    private static TargetState CheckPath(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return TargetState.Unknown;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network) return TargetState.Network;
            // A USB stick or disc that isn't plugged in: the shortcut may work again later.
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return TargetState.Unknown;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return TargetState.Unknown;
        }
        var raw = Win32Path.Raw(path);
        try
        {
            if (File.Exists(raw) || Directory.Exists(raw)) return TargetState.Exists;
            // The parent can't be read (permissions): we can't be sure it's gone.
            var parent = System.IO.Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(Win32Path.Raw(parent)))
            {
                try { _ = Directory.EnumerateFileSystemEntries(Win32Path.Raw(parent)).Take(1).Count(); }
                catch (UnauthorizedAccessException) { return TargetState.Unknown; }
            }
            return TargetState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return TargetState.Unknown;
        }
    }
}
