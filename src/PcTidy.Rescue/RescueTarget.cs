using PcTidy.Core;

namespace PcTidy.Rescue;

/// <summary>Which PC Tidy data folder Rescue works on, and whether it's a practice (sandbox) folder.</summary>
public sealed record RescueTarget(string DataRoot, string? SandboxRoot, string HowFound)
{
    public bool IsSandbox => SandboxRoot is not null;

    public static RescueTarget ForDataFolder(string folder, string howFound = "chosen by you")
        => new(Path.GetFullPath(folder), null, howFound);

    public static RescueTarget ForSandbox(string root)
    {
        var full = Path.GetFullPath(root);
        return new(new SandboxKnownLocations(full).DataRoot, full, "practice folder");
    }

    /// <summary>
    /// Command line first; then the folder PC Tidy itself uses (%LOCALAPPDATA%\PC Tidy or the one chosen in Settings);
    /// then, if Rescue was started from a copy inside a data folder (&lt;data&gt;\Rescue\), that data folder.
    /// </summary>
    public static RescueTarget Resolve(RescueOptions options, string? exeDirectory = null)
    {
        if (options.SandboxRoot is { } sandbox) return ForSandbox(sandbox);
        if (options.DataFolder is { } data) return ForDataFolder(data, "from the command line");

        var standard = AppSettings.ResolveDataRoot();
        if (LooksLikeDataFolder(standard)) return new(standard, null, "PC Tidy's data folder");

        exeDirectory ??= AppContext.BaseDirectory;
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(exeDirectory))?.FullName;
        if (parent is not null && LooksLikeDataFolder(parent)) return new(parent, null, "the folder Rescue was started from");

        return new(standard, null, "PC Tidy's data folder");
    }

    /// <summary>True if the folder holds a run journal or a quarantine (what Rescue needs).</summary>
    public static bool LooksLikeDataFolder(string folder)
    {
        try
        {
            return Directory.Exists(DataPaths.Runs(folder)) || Directory.Exists(DataPaths.Quarantine(folder));
        }
        catch { return false; }
    }
}
