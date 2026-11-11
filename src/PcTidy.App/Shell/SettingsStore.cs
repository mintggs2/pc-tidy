using System.IO;
using PcTidy.Core;

namespace PcTidy.App.Shell;

/// <summary>Settings persistence. This and screenshot PNGs are the only file writes the shell makes itself.</summary>
public interface ISettingsStore
{
    /// <summary>Saves settings.json in the current data folder.</summary>
    void Save(AppSettings settings);

    /// <summary>
    /// Points PC Tidy at another data folder: writes <see cref="AppSettings.PointerFile"/> (next to the default
    /// location) and saves the settings into the new folder. Takes effect at the next start. A picked folder that
    /// already holds other things gets a "PC Tidy Data" folder inside it instead (see <see cref="DataFolderChoice"/>);
    /// <see cref="AppSettings.DataRoot"/> says which folder was used.
    /// </summary>
    void SetDataFolder(AppSettings settings, string newRoot);
}

public sealed class FileSettingsStore : ISettingsStore
{
    public void Save(AppSettings settings) => settings.Save();

    public void SetDataFolder(AppSettings settings, string newRoot)
    {
        var picked = PathUtil.Normalize(newRoot);
        if (InstallFolder.IsProtected(picked))
            throw new InvalidOperationException("That folder is where Windows keeps installed programs, PC Tidy included, so PC Tidy doesn't keep its data there. Pick a folder of your own, for example in Documents or on another drive.");
        var defaultRoot = AppSettings.DefaultDataRoot;
        var full = DataFolderChoice.Resolve(picked, defaultRoot);
        Directory.CreateDirectory(defaultRoot);
        DataFolderChoice.Claim(full, defaultRoot);
        bool isDefault = DataFolderChoice.Same(full, defaultRoot);
        // An empty pointer means "use the default folder" (ResolveDataRoot ignores it).
        File.WriteAllText(AppSettings.PointerFile, isDefault ? "" : full);
        settings.DataRoot = full;
        settings.Save();
    }
}

/// <summary>
/// Which folder becomes PC Tidy's data folder, and the marker that makes it recognisably PC Tidy's (UNI-1). The
/// uninstaller's "delete my data too" only touches a folder with this marker (whose id matches the one recorded next
/// to the default folder), and inside it only PC Tidy's own folders and files. So the data folder must never be a
/// folder that already holds the user's things: such a pick gets a dedicated "PC Tidy Data" folder inside it.
/// Format (shared with PcTidy.Setup's DataFolderMarker): <c>pctidy-data.json</c> =
/// <c>{"format":"pctidy-data-folder","id":"…","createdUtc":"…"}</c> and <c>&lt;default&gt;\datafolder.id</c> = the id.
/// </summary>
public static class DataFolderChoice
{
    public const string MarkerFile = "pctidy-data.json";
    public const string IdFile = "datafolder.id";
    public const string Format = "pctidy-data-folder";
    public const string SubfolderName = "PC Tidy Data";

    /// <summary>
    /// The folder to use for a pick: the default folder, a folder that is already PC Tidy's (has the marker), a new
    /// or empty folder, or else "PC Tidy Data" (or "PC Tidy Data (2)", …) inside the picked folder.
    /// </summary>
    public static string Resolve(string picked, string defaultRoot)
    {
        var full = PathUtil.Normalize(picked);
        if (Same(full, defaultRoot) || IsUsable(full)) return full;
        for (int n = 1; n < 100; n++)
        {
            var candidate = Path.Combine(full, n == 1 ? SubfolderName : $"{SubfolderName} ({n})");
            if (IsUsable(candidate)) return candidate;
        }
        throw new InvalidOperationException("PC Tidy couldn't make a folder of its own in that folder. Pick another one.");
    }

    /// <summary>Creates the folder (if needed) and, unless it is the default one, writes the marker and records its id.</summary>
    public static void Claim(string folder, string defaultRoot)
    {
        Directory.CreateDirectory(folder);
        if (Same(folder, defaultRoot)) return;
        var id = ReadMarkerId(folder);
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            var json = System.Text.Json.JsonSerializer.Serialize(new { format = Format, id, createdUtc = DateTime.UtcNow });
            File.WriteAllText(Path.Combine(folder, MarkerFile), json);
        }
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(Path.Combine(defaultRoot, IdFile), id);
    }

    /// <summary>A folder that doesn't exist yet, is empty, or already carries PC Tidy's marker.</summary>
    public static bool IsUsable(string folder)
    {
        try
        {
            if (File.Exists(folder)) return false;
            if (!Directory.Exists(folder)) return true;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return false;
            return ReadMarkerId(folder) is not null || !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string? ReadMarkerId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, MarkerFile);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var f) || f.GetString() != Format) return null;
            return root.TryGetProperty("id", out var id) && id.GetString() is { Length: >= 16 and <= 64 } s ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public static bool Same(string a, string b) => string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// PC Tidy installs per machine (%ProgramFiles%\PC Tidy). That folder, and Program Files itself, are read-only for the
/// user: PC Tidy never writes there at run time (data, updates and the spare Rescue copy live in the data folder).
/// </summary>
public static class InstallFolder
{
    /// <summary>The folder PcTidy.exe runs from.</summary>
    public static string Current => PathUtil.Normalize(AppContext.BaseDirectory);

    /// <summary>True for the install folder, Program Files (64- and 32-bit) and anything inside them.</summary>
    public static bool IsProtected(string path) => IsProtected(path, Current,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    /// <summary>Testable form: <paramref name="roots"/> are the protected folders (empty ones are ignored).</summary>
    public static bool IsProtected(string path, params string[] roots)
    {
        var full = PathUtil.Normalize(path);
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Any(r => PathUtil.IsUnder(full, PathUtil.Normalize(r)));
    }
}

/// <summary>Demo / screenshot mode: keeps changes in memory only.</summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    public void Save(AppSettings settings) { }
    public void SetDataFolder(AppSettings settings, string newRoot) => settings.DataRoot = newRoot;
}

/// <summary>Shell-only preferences kept in <see cref="AppSettings.Features"/>["shell"] (no Core change needed).</summary>
public sealed record ShellPrefs
{
    public const string Key = "shell";
    /// <summary>Home shows the health score with its reasons (owner asked for it; users can hide it).</summary>
    public bool ShowHealthScore { get; init; } = true;
    /// <summary>Home shows the live "Now" card (CPU, GPU and RAM load, drive space). Off by default (layout spec §4.3).</summary>
    public bool ShowNowCard { get; init; }
    /// <summary>Quarantine size cap in GB that PC Tidy suggests purging down to (asks first). 0 = no cap.</summary>
    public int QuarantineCapGb { get; init; } = 20;

    // ---- right-click menu (the state on THIS PC: set only after the menu entries were really added/removed) ----
    /// <summary>"What is this?" is in Explorer's right-click menu.</summary>
    public bool WhatIsMenu { get; init; }
    /// <summary>"Tidy this folder" is in Explorer's right-click menu (folders and folder background).</summary>
    public bool TidyFolderMenu { get; init; }

    // ---- tray alerts and notifications ----
    public bool QuietHoursOn { get; init; } = true;
    /// <summary>Local time "HH:mm".</summary>
    public string QuietFrom { get; init; } = "22:00";
    public string QuietTo { get; init; } = "08:00";
    /// <summary>Tray: "a drive is under 10 % free", at most once a day.</summary>
    public bool LowDiskAlerts { get; init; } = true;
    /// <summary>A notification after the scheduled scan when it found enough (threshold in Schedule &amp; reports).</summary>
    public bool ScanReportAlerts { get; init; } = true;

    // ---- updates ----
    /// <summary>The release feed (an https URL to a small JSON manifest) the user chose. Empty = updates off.</summary>
    public string? UpdateFeedUrl { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Notifications.QuietHoursWindow Quiet => Notifications.QuietHoursWindow.Parse(QuietHoursOn, QuietFrom, QuietTo);

    public bool AlertEnabled(Notifications.AlertKind kind) => kind == Notifications.AlertKind.LowDisk ? LowDiskAlerts : ScanReportAlerts;

    public static ShellPrefs Get(AppSettings s) => s.GetFeature<ShellPrefs>(Key) ?? new ShellPrefs();
    public static void Set(AppSettings s, ShellPrefs p) => s.SetFeature(Key, p);

    /// <summary>
    /// <see cref="AppSettings.LoadProblem"/> without the parser's technical detail in brackets
    /// ("settings.json couldn't be read. Default settings are in use until you confirm.").
    /// </summary>
    public static string FriendlyLoadProblem(string problem)
        => System.Text.RegularExpressions.Regex.Replace(problem, @"\s*\([^)]*\)", "").Trim();
}

/// <summary>
/// The shell's remembered screen state (not preferences): whether the rail's "More" is open, when History was last
/// opened (for its rail counter), the Now card's folded state and chart, and the colour tags on restore points.
/// Kept in <see cref="AppSettings.Features"/>["shellstate"] and left out of settings exports (it describes this PC).
/// </summary>
public sealed record ShellState
{
    public const string Key = "shellstate";

    /// <summary>The rail's "More" group is open.</summary>
    public bool RailMoreOpen { get; init; }
    /// <summary>When History was last opened; null = never (then nothing counts as unseen).</summary>
    public DateTime? HistoryViewedUtc { get; init; }
    /// <summary>The Now card is folded to its header.</summary>
    public bool NowCollapsed { get; init; }
    /// <summary>The Now card's 60-second line: "cpu" or "gpu".</summary>
    public string NowChart { get; init; } = "cpu";
    /// <summary>Colour tags on restore points (key = <see cref="Pages.RestorePointsModel.TagKey"/>, value = tag name).</summary>
    public Dictionary<string, string> RestorePointTags { get; init; } = new();

    public static ShellState Get(AppSettings s)
    {
        try { return s.GetFeature<ShellState>(Key) ?? new ShellState(); }
        catch (System.Text.Json.JsonException) { return new ShellState(); }
    }

    public static void Set(AppSettings s, ShellState state) => s.SetFeature(Key, state);

    public static void Update(AppSettings s, Func<ShellState, ShellState> change) => Set(s, change(Get(s)));
}
