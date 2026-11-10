using System.Text.Json;
using System.Text.Json.Serialization;
using PcTidy.Core.Model;

namespace PcTidy.Core;

/// <summary>User settings, saved as JSON in the data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Default data folder: %LOCALAPPDATA%\PC Tidy (works on every PC; changeable in Settings).</summary>
    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PC Tidy");

    /// <summary>
    /// A tiny pointer file next to the default location remembers a user-chosen data folder
    /// (so settings can live on another drive). Read with <see cref="ResolveDataRoot"/>.
    /// </summary>
    public static string PointerFile => Path.Combine(DefaultDataRoot, "datafolder.txt");

    public static string ResolveDataRoot()
    {
        try
        {
            if (File.Exists(PointerFile))
            {
                var p = File.ReadAllText(PointerFile).Trim();
                if (p.Length > 0 && Path.IsPathFullyQualified(p)) return p;
            }
        }
        catch { /* fall back to default */ }
        return DefaultDataRoot;
    }

    public string DataRoot { get; set; } = DefaultDataRoot;
    public int QuarantineDays { get; set; } = 7;
    /// <summary>Ask before purging old quarantine (true) or purge automatically (false).</summary>
    public bool AskBeforePurge { get; set; } = true;
    public ScanDepth DefaultDepth { get; set; } = ScanDepth.Quick;
    /// <summary>Extra folders the user wants protected (never touched).</summary>
    public List<string> ExtraProtectedFolders { get; set; } = new();
    /// <summary>Cloud/AskFirst folders the user explicitly allowed (e.g. a OneDrive subfolder for organizing).</summary>
    public List<string> AllowedAskFirstFolders { get; set; } = new();
    /// <summary>Folders excluded from scans.</summary>
    public List<string> ScanExclusions { get; set; } = new();
    /// <summary>Allow the elevated MFT fast scan.</summary>
    public bool AllowAdminFastScan { get; set; } = false;
    public string Theme { get; set; } = "System"; // System | Dark | Light
    public bool OnboardingDone { get; set; } = false;
    public bool SuggestSimulationOnFirstUse { get; set; } = true;
    public bool LearningEnabled { get; set; } = true;
    public bool ExpertMode { get; set; } = false;
    public bool TrayEnabled { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string? ObsidianExportFolder { get; set; }
    /// <summary>Per-feature settings blobs (feature id → JSON).</summary>
    public Dictionary<string, JsonElement> Features { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SettingsPath(string dataRoot) => Path.Combine(dataRoot, "settings.json");

    /// <summary>
    /// Loads settings. If settings.json is unreadable, tries settings.json.bak; if both fail, returns defaults with
    /// <see cref="LoadProblem"/> set (the UI must tell the user; Save refuses until acknowledged). Never crashes.
    /// </summary>
    public static AppSettings Load(string dataRoot)
    {
        var p = SettingsPath(dataRoot);
        if (!File.Exists(p) && !File.Exists(p + ".bak")) return new AppSettings { DataRoot = dataRoot };
        string? problem = null;
        foreach (var candidate in new[] { p, p + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), Json);
                if (s is null) { problem ??= $"{Path.GetFileName(candidate)} is empty."; continue; }
                s.DataRoot = string.IsNullOrWhiteSpace(s.DataRoot) ? dataRoot : s.DataRoot;
                if (problem is not null) s.LoadProblem = problem + " PC Tidy used the backup copy instead.";
                return s;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                problem ??= $"{Path.GetFileName(candidate)} couldn't be read ({ex.Message}).";
            }
        }
        return new AppSettings { DataRoot = dataRoot, LoadProblem = (problem ?? "The settings file couldn't be read.") + " Default settings are in use until you confirm." };
    }

    /// <summary>Atomic save (write temp, then replace).</summary>
    /// <summary>
    /// Durable atomic save: write temp + flush to disk, then replace (keeping settings.json.bak).
    /// Refuses to overwrite a settings file that failed to load (see <see cref="LoadProblem"/>), so a corrupt
    /// file is never silently replaced by defaults that drop the user's protected folders.
    /// </summary>
    public void Save()
    {
        if (LoadProblem is not null && !LoadProblemAcknowledged)
            throw new InvalidOperationException("Settings weren't saved: the settings file couldn't be read. " + LoadProblem);
        Directory.CreateDirectory(DataRoot);
        var p = SettingsPath(DataRoot);
        var tmp = p + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(JsonSerializer.Serialize(this, Json));
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(p)) File.Replace(tmp, p, p + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, p);
    }

    /// <summary>Set when settings.json existed but couldn't be read (the backup or defaults were used instead).</summary>
    [JsonIgnore] public string? LoadProblem { get; private set; }
    /// <summary>The UI sets this after telling the user; then Save may write a fresh file.</summary>
    [JsonIgnore] public bool LoadProblemAcknowledged { get; set; }

    public T? GetFeature<T>(string featureId) where T : class
        => Features.TryGetValue(featureId, out var e) ? e.Deserialize<T>(Json) : null;

    public void SetFeature<T>(string featureId, T value)
        => Features[featureId] = JsonSerializer.SerializeToElement(value, Json);
}

/// <summary>Standard sub-folders of the data folder.</summary>
public static class DataPaths
{
    public static string Runs(string dataRoot) => Path.Combine(dataRoot, "runs");
    public static string Quarantine(string dataRoot) => Path.Combine(dataRoot, "Quarantine");
    public static string Reports(string dataRoot) => Path.Combine(dataRoot, "reports");
    public static string Index(string dataRoot) => Path.Combine(dataRoot, "index");
    public static string Logs(string dataRoot) => Path.Combine(dataRoot, "logs");
    public static string Backups(string dataRoot) => Path.Combine(dataRoot, "backups");
    public static string LearnedDb(string dataRoot) => Path.Combine(dataRoot, "learning", "learned.db");
}
