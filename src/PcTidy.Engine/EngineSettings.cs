using PcTidy.Core;

namespace PcTidy.Engine;

/// <summary>Where quarantined files go when they live on a different drive than the data folder.</summary>
public enum PerVolumeQuarantine
{
    /// <summary>Always use the data folder (cross-drive = copy, verify, remove).</summary>
    Off,
    /// <summary>Default: a hidden "PC Tidy Quarantine" folder on each non-system fixed drive (instant rename).</summary>
    NonSystemDrives,
    /// <summary>Also on the system drive (only used if its root is writable, which it usually isn't).</summary>
    AllFixedDrives,
}

/// <summary>Engine settings, stored in <see cref="AppSettings.Features"/> under <see cref="FeatureId"/> ("engine").</summary>
public sealed class EngineSettings
{
    public const string FeatureId = "engine";
    /// <summary>Put <c>Args["capOverride"] = "true"</c> on any plan item to let a plan exceed the delete caps.</summary>
    public const string CapOverrideArg = "capOverride";

    public PerVolumeQuarantine PerVolumeQuarantine { get; set; } = PerVolumeQuarantine.NonSystemDrives;

    /// <summary>Delete cap: a plan with more remove/move items than this is refused (unless overridden).</summary>
    public int MaxRemoveItemsPerRun { get; set; } = 50_000;

    /// <summary>Delete cap: a plan removing/moving more bytes than this is refused (unless overridden). Default 200 GB.</summary>
    public long MaxRemoveBytesPerRun { get; set; } = 200L * 1024 * 1024 * 1024;

    public static EngineSettings From(AppSettings settings)
    {
        try { return settings.GetFeature<EngineSettings>(FeatureId) ?? new EngineSettings(); }
        catch (System.Text.Json.JsonException) { return new EngineSettings(); }
    }

    public void SaveTo(AppSettings settings) => settings.SetFeature(FeatureId, this);
}
