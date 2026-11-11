namespace PcTidy.App.Updates;

/// <summary>
/// The only code that writes update downloads: &lt;DataRoot&gt;\updates\&lt;exact release file name&gt; (PC Tidy's own
/// data). Downloads land in a ".partial" file and only get their final name once their checksum matched. A name is
/// used only after <see cref="UpdateAssets.IsSafeFileName"/> accepted it and the resulting path is checked to stay
/// directly inside the updates folder: a server-provided name never becomes a path by itself.
/// </summary>
public sealed class UpdateStore
{
    public UpdateStore(string dataRoot) => Folder = Path.GetFullPath(Path.Combine(dataRoot, "updates")).TrimEnd('\\');

    public string Folder { get; }

    public string FinalPath(string fileName)
    {
        if (!UpdateAssets.IsSafeFileName(fileName)) throw new InvalidOperationException("That update file name isn't safe to use, so PC Tidy stopped.");
        var path = Path.GetFullPath(Path.Combine(Folder, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That update file name leads outside PC Tidy's updates folder, so PC Tidy stopped.");
        return path;
    }

    public string PartialPath(string fileName) => FinalPath(fileName) + ".partial";

    public Stream CreatePartial(string fileName)
    {
        var partial = PartialPath(fileName);
        Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
        return new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None); // boundary-ok: own data in DataRoot
    }

    /// <summary>Gives a checked download its final name; returns the final path.</summary>
    public string Complete(string fileName)
    {
        var final = FinalPath(fileName);
        File.Move(PartialPath(fileName), final, overwrite: true); // boundary-ok: own data in DataRoot
        return final;
    }

    private static readonly System.Text.RegularExpressions.Regex OwnDownload = new(
        @"^PC-Tidy-(?:Setup-(?<v>\d{1,5}\.\d{1,5}\.\d{1,5})\.exe|(?<v>\d{1,5}\.\d{1,5}\.\d{1,5})-portable\.zip)(?<partial>\.partial)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.ExplicitCapture);

    /// <summary>How old a ".partial" download of a newer version must be before it counts as left over.</summary>
    public static readonly TimeSpan PartialMaxAge = TimeSpan.FromHours(6);

    /// <summary>
    /// Deletes PC Tidy's own downloads that are no longer needed (3.1.1 review L9): installers and portable zips for
    /// <paramref name="running"/> or older (this newer PC Tidy started, so they did their job or were never used),
    /// their ".partial" leftovers, and ".partial" downloads of newer versions older than <see cref="PartialMaxAge"/>.
    /// Only files directly in the updates folder whose names are exactly PC Tidy's release names; folders (an unpacked
    /// portable copy, which may be what is running) and anything else are never touched. Returns how many went.
    /// </summary>
    public int RemoveOld(Version running, DateTime nowUtc)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(Folder) || (File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) return 0;
            foreach (var file in Directory.EnumerateFiles(Folder).ToList())
            {
                var m = OwnDownload.Match(Path.GetFileName(file));
                if (!m.Success || !Version.TryParse(m.Groups["v"].Value, out var v)) continue;
                bool partial = m.Groups["partial"].Success;
                bool old = v <= running;
                bool stale = partial && nowUtc - File.GetLastWriteTimeUtc(file) > PartialMaxAge;
                if (!old && !stale) continue;
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    File.Delete(file); // boundary-ok: own data in DataRoot (PC Tidy's own old downloads)
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use or locked: next start */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    public void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } // boundary-ok: own data in DataRoot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
