namespace PcTidy.Features.Search;

/// <summary>Names shared by the Search section (feature id = settings key = own-data folder name).</summary>
public static class SearchIds
{
    public const string FeatureId = "search";
    public const string Title = "Search";

    /// <summary>PC Tidy's own search data: <c>&lt;DataRoot&gt;\search\</c> (index snapshot only, never user files).</summary>
    public static string DataFolder(string dataRoot) => Path.Combine(dataRoot, FeatureId);
}
