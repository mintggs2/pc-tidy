namespace PcTidy.App.Shell;

/// <summary>
/// PC Tidy Rescue: the small separate tool that can undo recent runs even if the main app won't start.
/// The shell finds it (next to PcTidy.exe, else the spare copy in the data folder) and keeps that spare up to date.
/// Starting it goes through <see cref="FlowHost.OpenRescueTool"/>, the shell's single process-start place.
/// </summary>
public static class RescueTool
{
    public const string ExeName = "PC Tidy Rescue.exe";

    /// <summary>The copy shipped next to PcTidy.exe.</summary>
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, ExeName);

    /// <summary>The spare kept in the data folder (portable users keep it even if the app folder is gone).</summary>
    public static string Spare(string dataRoot) => Path.Combine(dataRoot, "Rescue", ExeName);

    /// <summary>The Rescue exe to start, or null when neither copy exists.</summary>
    public static string? Find(string dataRoot)
    {
        foreach (var p in new[] { Bundled, Spare(dataRoot) })
        {
            try { if (File.Exists(p)) return p; }
            catch (Exception) { /* unreadable location: try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Real starts only: when &lt;DataRoot&gt;\Rescue\ exists and the bundled Rescue is newer than the spare (or the
    /// spare is missing), copy it there. App-owned housekeeping inside PC Tidy's own data folder; never throws.
    /// </summary>
    public static void RefreshSpare(string dataRoot)
    {
        try
        {
            var folder = Path.GetDirectoryName(Spare(dataRoot))!;
            if (!Directory.Exists(folder) || !File.Exists(Bundled)) return;
            var spare = Spare(dataRoot);
            if (File.Exists(spare) && File.GetLastWriteTimeUtc(spare) >= File.GetLastWriteTimeUtc(Bundled)) return;
            // Copy to a temp name first so a half-written spare never replaces a working one.
            var tmp = spare + ".new";
            File.Copy(Bundled, tmp, overwrite: true);
            File.Move(tmp, spare, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashHandler.Write(ex, "Updating the spare PC Tidy Rescue", terminating: false);
        }
    }
}
