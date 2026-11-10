namespace PcTidy.Features.Duplicates;

/// <summary>
/// What the Duplicates section remembers, stored with <c>AppSettings.SetFeature("duplicates", …)</c>.
/// The shell saves settings (feature code never writes files).
/// </summary>
public sealed class DuplicatesSettings
{
    public const string FeatureKey = DuplicatesModule.FeatureId;

    /// <summary>Folders whose copies are never removed (dupeGuru-style reference folders). Scanned, never touched.</summary>
    public List<string> ReferenceFolders { get; set; } = new();
    /// <summary>Last folders used in "Compare to a master folder".</summary>
    public string? LastMasterFolder { get; set; }
    public string? LastOtherFolder { get; set; }

    public static DuplicatesSettings From(AppSettings settings)
    {
        try { return settings.GetFeature<DuplicatesSettings>(FeatureKey) ?? new DuplicatesSettings(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return new DuplicatesSettings();
        }
    }

    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}
