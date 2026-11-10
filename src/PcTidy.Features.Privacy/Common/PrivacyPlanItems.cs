using System.Globalization;

namespace PcTidy.Features.Privacy.Common;

/// <summary>
/// The only place this project builds PlanItems. Each one is a description; the engine and platform handlers do the work
/// (quarantine, registry with exact prior state, cookie database edit with backup, Store app removal).
/// </summary>
public static class PrivacyPlanItems
{
    /// <summary>Moves one file into quarantine (restorable for the quarantine period).</summary>
    public static PlanItem Quarantine(string featureId, FileEntry file, string description) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = featureId, Description = description,
        SourcePath = file.FullPath, ExpectedSize = file.Size, ExpectedLastWriteUtc = file.LastWriteUtc, Reversible = true,
    };

    /// <summary>
    /// An HKCU DWORD switch. <paramref name="physicalKey"/> is what <see cref="UserRegistry.Map"/> returned, so the plan
    /// writes where the scan read. Not a system change: it's one of your own Settings switches with an exact undo.
    /// </summary>
    public static PlanItem SetUserDword(string featureId, string physicalKey, string valueName, uint data, string description) => new()
    {
        Kind = ActionKind.RegistrySetValue, FeatureId = featureId, Description = description,
        Registry = new RegistryValueSpec
        {
            Hive = RegistryHive.CurrentUser, KeyPath = physicalKey, ValueName = valueName, Kind = RegistryKind.DWord,
            Data = data.ToString(CultureInfo.InvariantCulture),
        },
        Reversible = true,
    };

    /// <summary>
    /// Deletes a browser profile's cookies except the kept sites (engine handler: edits a verified copy, keeps the old
    /// database in quarantine, undo puts it back). The browser must be closed.
    /// </summary>
    public static PlanItem CookiesExcept(string featureId, string browser, string profileDir, IEnumerable<string> keepHosts, string processName, string description) => new()
    {
        Kind = ActionKind.CookiesDeleteExcept, FeatureId = featureId, Description = description,
        Args = new Dictionary<string, string>
        {
            ["browser"] = browser, ["profileDir"] = profileDir, ["keepHosts"] = string.Join("\n", keepHosts),
        },
        RequiresClosedApps = new[] { processName },
        Reversible = true,
    };

    /// <summary>Store app removal for this account. It comes back from the Microsoft Store, so it's marked not reversible.</summary>
    public static PlanItem RemoveStoreApp(string featureId, string packageFullName, string displayName, string description) => new()
    {
        Kind = ActionKind.AppxRemoveForUser, FeatureId = featureId, Description = description,
        Args = new Dictionary<string, string> { ["packageFullName"] = packageFullName, ["displayName"] = displayName },
        IsSystemChange = true, Reversible = false,
    };

    /// <summary>Turns a Windows optional feature off (admin, restore point first; undo turns it back on).</summary>
    public static PlanItem RemoveOptionalFeature(string featureId, string featureName, string description) => new()
    {
        Kind = ActionKind.OptionalFeatureRemove, FeatureId = featureId, Description = description,
        Args = new Dictionary<string, string> { ["name"] = featureName, ["kind"] = "feature" },
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    /// <summary>Opens a Settings page (harmless, nothing to undo). Used through <see cref="LinkOpener"/>.</summary>
    public static PlanItem OpenExternal(string featureId, string target, string description) => new()
    {
        Kind = ActionKind.OpenExternal, FeatureId = featureId, Description = description,
        Args = new Dictionary<string, string> { ["target"] = target }, Reversible = true,
    };
}
