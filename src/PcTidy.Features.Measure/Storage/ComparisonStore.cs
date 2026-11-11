using System.Text;
using System.Text.Json;
using PcTidy.Core;

namespace PcTidy.Features.Measure.Storage;

/// <summary>
/// The ONLY code in Measure that writes to disk: one &lt;folder&gt;\&lt;id&gt;.json per saved comparison, in PC Tidy's own
/// data folder (&lt;DataRoot&gt;\measure by default). Atomic save (temp file flushed, then moved into place). Reads never throw:
/// a damaged file is skipped and counted in <see cref="LastProblem"/>.
/// </summary>
public sealed class ComparisonStore
{
    public const string FolderName = "measure";
    /// <summary>Most comparisons listed (newest first).</summary>
    public const int MaxListed = 500;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();

    /// <param name="folder">Folder that holds the JSON files (created on first save).</param>
    public ComparisonStore(string folder) => Folder = folder;

    public static ComparisonStore ForDataRoot(string dataRoot) => new(Path.Combine(dataRoot, FolderName));

    public string Folder { get; }
    public string? LastProblem { get; private set; }

    public string PathFor(string id) => Path.Combine(Folder, SafeId(id) + ".json");

    public bool Save(SavedComparison comparison)
    {
        lock (_gate)
        {
            try
            {
                comparison.Id = SafeId(comparison.Id);
                if (comparison.CreatedUtc == default) comparison.CreatedUtc = DateTime.UtcNow;
                if (IsLink(Folder)) throw new IOException("the Measure data folder is a link, so PC Tidy won't write through it");
                Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
                var target = PathFor(comparison.Id);
                var tmp = target + ".tmp";
                // Serialised first: a number JSON can't hold (infinite FPS from a damaged file) fails here, before any file is written.
                var json = JsonSerializer.Serialize(comparison, AppSettings.Json);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) // boundary-ok: own data in DataRoot
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, target, overwrite: true); // boundary-ok: own data in DataRoot
                LastProblem = null;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            {
                LastProblem = "The comparison couldn't be saved: some of its numbers can't be stored (a damaged recording?). Remove that run and save again.";
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastProblem = $"The comparison couldn't be saved ({ex.Message}).";
                return false;
            }
        }
    }

    public SavedComparison? Load(string id)
    {
        lock (_gate) return Read(PathFor(id));
    }

    /// <summary>Saved comparisons, newest first. Optionally only those linked to one History run.</summary>
    public IReadOnlyList<SavedComparison> List(string? linkedRunId = null)
    {
        lock (_gate)
        {
            LastProblem = null;
            if (!Directory.Exists(Folder)) return Array.Empty<SavedComparison>();
            var result = new List<SavedComparison>();
            int bad = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                {
                    if (Read(file) is { } c)
                    {
                        if (linkedRunId is null || string.Equals(c.LinkedRunId, linkedRunId, StringComparison.OrdinalIgnoreCase)) result.Add(c);
                    }
                    else bad++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastProblem = $"Saved comparisons couldn't be listed ({ex.Message}).";
            }
            if (bad > 0) LastProblem = $"{bad} saved comparison file(s) couldn't be read and were skipped.";
            return result.OrderByDescending(c => c.CreatedUtc).Take(MaxListed).ToList();
        }
    }

    private static SavedComparison? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            var c = JsonSerializer.Deserialize<SavedComparison>(File.ReadAllText(file), AppSettings.Json);
            if (c is null) return null;
            c.Before ??= new();
            c.After ??= new();
            c.BeforeAgain ??= new();
            c.Conditions ??= Conditions.NewList();
            return c;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Ids are file names: letters, digits and dashes only.</summary>
    internal static string SafeId(string? id)
    {
        var clean = new string((id ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(64).ToArray());
        return clean.Length > 0 ? clean : Guid.NewGuid().ToString("N");
    }

    private static bool IsLink(string folder)
    {
        try { return Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
