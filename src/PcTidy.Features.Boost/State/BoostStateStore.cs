using System.Text.Json;

namespace PcTidy.Features.Boost.State;

/// <summary>
/// The ONLY code in Game boost that writes to disk: &lt;DataRoot&gt;\boost\state.json (PC Tidy's own data).
/// Durable atomic save (temp file flushed to disk, then replaced, keeping a .bak). Reads never throw: a damaged
/// file falls back to the backup, then to "no boost active" with <see cref="LastProblem"/> set.
/// </summary>
public sealed class BoostStateStore
{
    public const string FolderName = BoostSettings.FeatureKey;

    private readonly object _gate = new();

    public BoostStateStore(string dataRoot)
    {
        Folder = Path.Combine(dataRoot, FolderName);
        FilePath = Path.Combine(Folder, "state.json");
    }

    public string Folder { get; }
    public string FilePath { get; }
    /// <summary>Why the last load or save didn't work (shown on the page), or null.</summary>
    public string? LastProblem { get; private set; }

    public BoostState Load()
    {
        lock (_gate)
        {
            string? problem = null;
            foreach (var candidate in new[] { FilePath, FilePath + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var state = JsonSerializer.Deserialize<BoostState>(File.ReadAllText(candidate), AppSettings.Json);
                    if (state is null) continue;
                    state.ClosedApps ??= new();
                    state.ReopenOffers ??= new();
                    state.ClosedApps.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.ExePath));
                    state.ReopenOffers.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.ExePath));
                    LastProblem = problem;
                    return state;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    problem ??= $"Game boost's saved state couldn't be read ({ex.Message}).";
                }
            }
            LastProblem = problem;
            return new BoostState();
        }
    }

    /// <summary>Saves the state. Returns false (and sets <see cref="LastProblem"/>) instead of throwing.</summary>
    public bool Save(BoostState state)
    {
        lock (_gate)
        {
            try
            {
                if (IsLink(Folder)) throw new IOException("the boost data folder is a link, so PC Tidy won't write through it");
                Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
                var tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) // boundary-ok: own data in DataRoot
                using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    w.Write(JsonSerializer.Serialize(state, AppSettings.Json));
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true); // boundary-ok: own data in DataRoot
                else File.Move(tmp, FilePath); // boundary-ok: own data in DataRoot
                LastProblem = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastProblem = $"Game boost couldn't save its state ({ex.Message}).";
                return false;
            }
        }
    }

    private static bool IsLink(string folder)
    {
        try { return Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
