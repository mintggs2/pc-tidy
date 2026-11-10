using PcTidy.Core;
using PcTidy.Features.Search.Structure;

namespace PcTidy.Features.Search;

/// <summary>A folder in PC Tidy's own Favorites list.</summary>
public sealed record FavoriteFolder(string Path, string Label);

/// <summary>
/// What the Search section remembers, stored with <c>AppSettings.SetFeature("search", …)</c>; the shell saves
/// when the user leaves the page. The index itself is NOT here (it lives in &lt;DataRoot&gt;\search\).
/// </summary>
public sealed class SearchSettings
{
    public List<FavoriteFolder> Favorites { get; set; } = new();

    /// <summary>Folders PC Tidy's own index covers. Empty = the defaults (your profile + other drives' own folders).</summary>
    public List<string> IndexRoots { get; set; } = new();

    /// <summary>Use Everything when it's installed and running (it's faster and always current).</summary>
    public bool UseEverything { get; set; } = true;

    /// <summary>Include AppData (app caches and settings) in the own index. Off: it's mostly noise.</summary>
    public bool IndexAppData { get; set; }

    /// <summary>Minutes between full background re-scans (change notifications are only hints).</summary>
    public int RescanMinutes { get; set; } = 30;

    public StructurePreset Preset { get; set; } = StructurePreset.Simple;

    public static SearchSettings Load(AppSettings settings)
    {
        try { return settings.GetFeature<SearchSettings>(SearchIds.FeatureId) ?? new SearchSettings(); }
        catch (System.Text.Json.JsonException) { return new SearchSettings(); }
    }

    public void Store(AppSettings settings) => settings.SetFeature(SearchIds.FeatureId, this);
}
