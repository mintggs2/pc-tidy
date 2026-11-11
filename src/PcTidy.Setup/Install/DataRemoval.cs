using System.Text.Json;

namespace PcTidy.Setup.Install;

/// <summary>
/// The marker PC Tidy writes into a data folder it made somewhere other than the default %LOCALAPPDATA%\PC Tidy
/// (same format as PcTidy.App's <c>DataFolderMarker</c>): <c>pctidy-data.json</c> = <c>{"format":"pctidy-data-folder","id":"…"}</c>,
/// and the same id in <c>%LOCALAPPDATA%\PC Tidy\datafolder.id</c>. Only a folder whose marker id matches counts as
/// PC Tidy's, so a folder the user picked that already held their own files never does (the app makes a
/// "PC Tidy Data" folder inside it instead).
/// </summary>
public static class DataFolderMarker
{
    public const string FileName = "pctidy-data.json";
    public const string IdFileName = "datafolder.id";
    public const string Format = "pctidy-data-folder";

    /// <summary>The id in a folder's marker, or null when there is no valid marker.</summary>
    public static string? ReadId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var f) || f.GetString() != Format) return null;
            return root.TryGetProperty("id", out var id) && id.GetString() is { Length: >= 16 and <= 64 } s ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The id recorded next to the default data folder for the moved one, or null.</summary>
    public static string? RecordedId(string defaultDataRoot)
    {
        try
        {
            var path = Path.Combine(defaultDataRoot, IdFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 256) return null;
            var id = File.ReadAllText(path).Trim();
            return id.Length is >= 16 and <= 64 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>What "delete my data too" would delete, what it leaves alone, and why.</summary>
public sealed record DataRemovalPlan(IReadOnlyList<string> Delete, IReadOnlyList<string> Keep, IReadOnlyList<string> Refused)
{
    /// <summary>The folders that are emptied of PC Tidy's own things (each is removed itself only once empty).</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Delete.Count == 0;

    /// <summary>The confirmation text: every folder and file that will go, then what stays.</summary>
    public string Describe(int max = 14)
    {
        var lines = new List<string>();
        if (Delete.Count == 0) lines.Add("Nothing of PC Tidy's own was found to delete.");
        else
        {
            lines.Add("These are deleted for good (they don't go to the Recycle Bin):");
            lines.AddRange(Delete.Take(max).Select(d => "•  " + d));
            if (Delete.Count > max) lines.Add($"   …and {Delete.Count - max} more of PC Tidy's own items");
        }
        if (Keep.Count > 0)
        {
            lines.Add("");
            lines.Add("These stay, because PC Tidy didn't make them:");
            lines.AddRange(Keep.Take(6).Select(k => "•  " + k));
            if (Keep.Count > 6) lines.Add($"   …and {Keep.Count - 6} more");
        }
        foreach (var r in Refused) { lines.Add(""); lines.Add(r); }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Plans "delete my data too" (UNI-1). PC Tidy's data folder can be any folder the user picked, so the uninstaller
/// never deletes a folder tree wholesale: only a folder that is PC Tidy's (the default %LOCALAPPDATA%\PC Tidy, or one
/// with PC Tidy's marker and matching id), and inside it only the entries PC Tidy itself makes (<see cref="OwnNames"/>).
/// Anything else stays, and so does the folder then. Never a drive root or one of the user's own folders.
/// </summary>
public static class DataRemovalPlanner
{
    /// <summary>Everything PC Tidy creates directly in its data folder (settings, history, quarantine, each section's own data).</summary>
    public static readonly IReadOnlySet<string> OwnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // files
        "settings.json", "settings.json.bak", "settings.json.tmp", "datafolder.txt", DataFolderMarker.IdFileName, DataFolderMarker.FileName, "learned.db",
        // shared folders (Core DataPaths and the shell)
        "runs", "Quarantine", "reports", "index", "logs", "backups", "learning", "Rescue", "updates", "exports", "staging", "temp",
        // stopped scans' results (file paths from scans; 3.1.1)
        "scan-checkpoints",
        // each section's own data (<DataRoot>\<feature id>)
        "aichats", "apps", "assistant", "boost", "cleanup", "desktoptidy", "developer", "drivemove", "drivers", "duplicates", "engine",
        "games", "insights", "learned", "measure", "organize", "pccare", "photos", "privacy", "schedule", "search", "services", "shell",
        "spacemap", "speed", "tweaks",
    };

    public static DataRemovalPlan Plan(InstallEnvironment env)
    {
        var delete = new List<string>();
        var keep = new List<string>();
        var refused = new List<string>();
        var roots = new List<string>();
        var candidates = new List<string> { env.DataRoot };
        if (!FolderSafety.Same(env.DefaultDataRoot, env.DataRoot)) candidates.Add(env.DefaultDataRoot);

        foreach (var root in candidates)
        {
            if (!Directory.Exists(root)) continue;
            if (WhyNotOurs(root, env) is { } why) { refused.Add(why); continue; }
            roots.Add(root);
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(root).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused.Add($"{root} couldn't be read, so nothing in it was deleted."); continue; }
            foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                (OwnNames.Contains(e.Name) ? delete : keep).Add(e.FullName);
        }
        return new DataRemovalPlan(delete, keep, refused) { Roots = roots };
    }

    /// <summary>Null when <paramref name="root"/> is PC Tidy's data folder, else the plain-English reason it stays untouched.</summary>
    private static string? WhyNotOurs(string root, InstallEnvironment env)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return $"{root} is a link to another folder, so nothing in it was deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{root} couldn't be read, so nothing in it was deleted."; }
        if (!FolderSafety.IsCleanableFolder(root)) return $"{root} is a drive or one of your own folders, so nothing in it was deleted.";
        if (FolderSafety.Same(root, env.DefaultDataRoot)) return null;
        var marker = DataFolderMarker.ReadId(root);
        var recorded = DataFolderMarker.RecordedId(env.DefaultDataRoot);
        if (marker is not null && recorded is not null && string.Equals(marker, recorded, StringComparison.Ordinal)) return null;
        return $"{root} doesn't look like PC Tidy's data folder (no PC Tidy marker), so it was left alone. Delete PC Tidy's folders in it yourself if you want (runs, Quarantine, logs…).";
    }
}
