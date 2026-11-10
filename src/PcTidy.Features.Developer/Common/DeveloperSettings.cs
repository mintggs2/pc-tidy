using PcTidy.Core;

namespace PcTidy.Features.Developer.Common;

/// <summary>
/// What the Developer section remembers, stored with <c>AppSettings.SetFeature("developer", …)</c>; the shell saves.
/// </summary>
public sealed class DeveloperSettings
{
    public const string FeatureKey = "developer";

    /// <summary>Folders to look for projects in. Empty = your user folder plus the other fixed drives.</summary>
    public List<string> Roots { get; set; } = new();
    /// <summary>A project counts as idle (its build folders are offered) after this many days without activity.</summary>
    public int IdleDays { get; set; } = 90;
    /// <summary>Projects untouched this many months are suggested for archiving.</summary>
    public int ArchiveMonths { get; set; } = 12;
    /// <summary>Where archive zips go. Null = <see cref="DevContext.DefaultArchiveFolder"/>.</summary>
    public string? ArchiveFolder { get; set; }

    public static DeveloperSettings From(AppSettings settings)
    {
        try { return settings.GetFeature<DeveloperSettings>(FeatureKey) ?? new DeveloperSettings(); }
        catch (System.Text.Json.JsonException) { return new DeveloperSettings(); }
    }

    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}
