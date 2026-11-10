namespace PcTidy.Setup.Install;

/// <summary>Checks that keep Setup from installing into, or deleting, a folder that isn't PC Tidy's.</summary>
public static class FolderSafety
{
    /// <summary>Folders that must never be deleted as "PC Tidy's data" (Windows, Program Files, shared data).</summary>
    private static IEnumerable<string> Protected()
    {
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonApplicationData,
                 })
        {
            var p = Environment.GetFolderPath(f);
            if (p.Length > 0) yield return p;
        }
    }

    /// <summary>Folders that may contain PC Tidy's data folder, but must never BE it.</summary>
    private static IEnumerable<string> NotItself()
    {
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu,
                 })
        {
            var p = Environment.GetFolderPath(f);
            if (p.Length > 0) yield return p;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>
    /// Null if PC Tidy may be installed in <paramref name="dir"/>, else a friendly reason. Only one folder qualifies:
    /// "PC Tidy" directly inside <paramref name="programFilesRoot"/> (%ProgramFiles%), because only there can no
    /// account without admin rights change the program that later runs with admin rights.
    /// </summary>
    public static string? CheckInstallDir(string? dir, string programFilesRoot, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))
            return "PC Tidy installs in Program Files.";
        string full;
        try { full = Normalize(dir); }
        catch { return "That folder name isn't valid."; }
        var expected = Path.Combine(Normalize(programFilesRoot), Product.Name);
        if (!Same(full, expected)) return $"PC Tidy installs in {expected}, so the parts that run with admin rights can't be changed by other programs.";
        if (IsSameOrUnder(full, dataRoot) || IsSameOrUnder(dataRoot, full)) return "That's PC Tidy's data folder (history and quarantine). Move the data folder in Settings first.";
        return null;
    }

    /// <summary>True if the folder exists and has anything in it that isn't an earlier PC Tidy install.</summary>
    public static bool HasForeignContent(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, Product.ManifestFile)) || File.Exists(Path.Combine(dir, Product.AppExe))) return false;
            return Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return true; }
    }

    /// <summary>
    /// True for a folder "delete my data" may take PC Tidy's own things out of: an existing, local folder that isn't a
    /// drive root, Windows or Program Files (or inside them), or one of the user's own folders (or above them). Whether it
    /// is PC Tidy's at all is decided by <see cref="DataRemovalPlanner"/> (marker), and even then only PC Tidy's own
    /// entries in it are deleted.
    /// </summary>
    public static bool IsCleanableFolder(string dir)
    {
        try
        {
            if (!Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(dir)) return false;
            var full = Normalize(dir);
            if (Path.GetPathRoot(full) is { } root && Same(full, root)) return false;
            if (Protected().Any(p => IsSameOrUnder(p, full) || IsSameOrUnder(full, p))) return false;
            if (NotItself().Any(p => Same(full, p) || IsSameOrUnder(p, full))) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>True if any folder between <paramref name="folder"/> (exclusive) and <paramref name="path"/> is a junction or link.</summary>
    public static bool GoesThroughLink(string path, string folder)
    {
        try
        {
            var stop = Normalize(folder);
            for (var dir = Path.GetDirectoryName(Normalize(path)); dir is not null && IsSameOrUnder(dir, stop) && !Same(dir, stop); dir = Path.GetDirectoryName(dir))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameOrUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
