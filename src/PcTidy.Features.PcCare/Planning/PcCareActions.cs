using PcTidy.Core.Model;

namespace PcTidy.Features.PcCare.Planning;

/// <summary>
/// Every PlanItem PC care can emit. The page only DESCRIBES changes; PcTidy.Platform's handlers carry them out after
/// the standard confirm. Windows' own tools can't be undone from History, so their items say Reversible = false
/// (they show in the confirm's "can't be undone" box and are never ticked for you).
/// </summary>
public static class PcCareActions
{
    public const string FeatureId = "pccare";

    private static Dictionary<string, string> A(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static PlanItem Tool(string toolId, string description, bool admin, bool systemChange, Dictionary<string, string>? args = null) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = FeatureId, ToolId = toolId, Description = description,
        NeedsAdmin = admin, IsSystemChange = systemChange, Reversible = false, Args = args,
    };

    // ---- app updates

    /// <summary>winget upgrade --id &lt;id&gt; (exact). Agreements are only accepted through <see cref="WithAgreementsAccepted"/>.</summary>
    public static PlanItem WingetUpgrade(string id, string name, string toVersion)
        => Tool(ToolIds.WingetUpgrade, $"Update {name} to {toVersion}", admin: false, systemChange: false, A(("id", id)));

    /// <summary>Copies of winget items that accept the source and package agreements (only after the user ticked the box).</summary>
    public static IReadOnlyList<PlanItem> WithAgreementsAccepted(IEnumerable<PlanItem> items)
        => items.Select(i => i.ToolId == ToolIds.WingetUpgrade
            ? i with { Args = new Dictionary<string, string>(i.Args ?? new()) { ["acceptAgreements"] = "true" } }
            : i).ToList();

    // ---- Windows repair

    public static PlanItem DismRestoreHealth()
        => Tool(ToolIds.DismRestoreHealth, "Repair the Windows component store with DISM (RestoreHealth)", admin: true, systemChange: true);

    public static PlanItem SfcScan()
        => Tool(ToolIds.SfcScan, "Check and repair Windows system files (sfc /scannow)", admin: true, systemChange: true);

    // ---- network

    public static PlanItem FlushDns() => Tool(ToolIds.FlushDns, "Clear the DNS cache", admin: false, systemChange: false);

    public static PlanItem WinsockReset()
        => Tool(ToolIds.WinsockReset, "Reset Winsock and TCP/IP to Windows' defaults", admin: true, systemChange: true, A(("needsRestart", "true")));

    // ---- security

    public static PlanItem DefenderQuickScan() => Tool(ToolIds.DefenderQuickScan, "Run a Microsoft Defender quick scan", admin: false, systemChange: false);

    // ---- restore points

    public static PlanItem CreateRestorePoint(string description)
        => Tool(ToolIds.RestorePointCreate, $"Make a restore point (\"{description}\")", admin: true, systemChange: false, A(("description", description)));

    public static PlanItem DeleteRestorePoint(uint sequenceNumber, string description) => new()
    {
        Kind = ActionKind.RestorePointDelete, FeatureId = FeatureId, Description = description,
        Args = A(("sequenceNumber", sequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        NeedsAdmin = true, Reversible = false,
    };

    /// <summary>Restore point space as a % of the system drive (never below 5%). Shrinking can delete the oldest points.</summary>
    public static PlanItem ResizeRestoreSpace(int percent, string drive)
    {
        if (percent < 5) throw new ArgumentOutOfRangeException(nameof(percent), "Restore point space never goes below 5%.");
        return Tool(ToolIds.ShadowStorageResize, $"Set restore point space on {drive.TrimEnd('\\')} to {percent}%", admin: true, systemChange: false,
            A(("percent", percent.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("drive", drive)));
    }

    // ---- Store

    public static PlanItem StoreReset() => new()
    {
        Kind = ActionKind.StoreRepair, FeatureId = FeatureId, Description = "Reset the Microsoft Store cache (wsreset)",
        Args = A(("wsreset", "true")), Reversible = false,
    };

    public static PlanItem StoreReRegister(string packageFamilyName, string name) => new()
    {
        Kind = ActionKind.StoreRepair, FeatureId = FeatureId, Description = $"Re-register {name} for your account",
        Args = A(("packageFamilyName", packageFamilyName)), Reversible = false,
    };

    // ---- leftovers

    public static PlanItem DeleteWifi(string profileName, Guid interfaceGuid) => new()
    {
        Kind = ActionKind.WlanProfileDelete, FeatureId = FeatureId, Description = $"Forget the Wi-Fi network \"{profileName}\" (a backup is kept)",
        Args = A(("profileName", profileName), ("interfaceGuid", interfaceGuid.ToString("D"))),
        NeedsAdmin = true, Reversible = true,
    };

    public static PlanItem DeleteUserProfile(string sid, string folder) => new()
    {
        Kind = ActionKind.UserProfileDelete, FeatureId = FeatureId, Description = $"Remove the leftover profile {folder}",
        Args = A(("sid", sid)), NeedsAdmin = true, IsSystemChange = true, Reversible = false,
    };

    /// <summary>Features can be turned back on from History; capabilities need a fresh download, so they're not reversible.</summary>
    public static PlanItem RemoveOptional(string name, bool isCapability, string title) => new()
    {
        Kind = ActionKind.OptionalFeatureRemove, FeatureId = FeatureId, Description = $"Remove {title}",
        Args = A(("name", name), ("kind", isCapability ? "capability" : "feature")),
        NeedsAdmin = true, IsSystemChange = true, Reversible = !isCapability,
    };

    // ---- shredder

    public static PlanItem Shred(string path, long size, DateTime lastWriteUtc) => new()
    {
        Kind = ActionKind.ShredFile, FeatureId = FeatureId, Description = $"Shred {Path.GetFileName(path)} (overwrite, then delete)",
        SourcePath = path, ExpectedSize = size, ExpectedLastWriteUtc = lastWriteUtc, Reversible = false,
    };

    /// <summary>Windows' own cipher /w on a hard drive's free space. Slow, permanent, HDD only.</summary>
    public static PlanItem WipeFreeSpace(string drive)
        => Tool(ToolIds.CipherWipeFreeSpace, $"Overwrite the free space on {drive.TrimEnd('\\')}", admin: false, systemChange: false, A(("drive", drive)));

    // ---- reports and links

    public static PlanItem WriteReport(string path, string content) => new()
    {
        Kind = ActionKind.WriteTextFile, FeatureId = FeatureId, Description = $"Save the health report as {Path.GetFileName(path)}",
        DestinationPath = path, Args = A(("content", content)), ExpectedSize = System.Text.Encoding.UTF8.GetByteCount(content),
    };

    public static PlanItem Open(string target, string description) => new()
    {
        Kind = ActionKind.OpenExternal, FeatureId = FeatureId, Description = description, Args = A(("target", target)), Reversible = true,
    };

    public static bool IsOpen(PlanItem i) => i.Kind == ActionKind.OpenExternal;
}

/// <summary>Settings pages and help pages PC care links to (only ms-settings: and https: targets, which OpenExternal allows).</summary>
public static class PcCareLinks
{
    public const string WindowsSecurity = "ms-settings:windowsdefender";
    public const string Apps = "ms-settings:appsfeatures";
    public const string OptionalFeatures = "ms-settings:optionalfeatures";
    public const string Languages = "ms-settings:regionlanguage";
    public const string Troubleshoot = "ms-settings:troubleshoot";
    public const string WindowsUpdate = "ms-settings:windowsupdate";
    public const string Backup = "ms-settings:backup";
    public const string Network = "ms-settings:network-status";
    public const string StoreTerms = "https://aka.ms/microsoft-store-terms-of-transaction";

    /// <summary>The browser's own "reset settings" help page (browser-internal pages can't be opened from outside the browser).</summary>
    public static string ResetHelp(string browser) => browser switch
    {
        "Edge" => "https://support.microsoft.com/microsoft-edge",
        "Brave" => "https://support.brave.com/",
        _ => "https://support.google.com/chrome/answer/3296214",
    };

    public static string ExtensionsHelp(string browser) => browser switch
    {
        "Edge" => "https://support.microsoft.com/microsoft-edge",
        "Brave" => "https://support.brave.com/",
        _ => "https://support.google.com/chrome_webstore/answer/2664769",
    };

    /// <summary>What to type in the browser's address bar ("chrome://extensions").</summary>
    public static string InternalPage(string browser, string page) => browser switch
    {
        "Edge" => "edge://" + page,
        "Brave" => "brave://" + page,
        _ => "chrome://" + page,
    };
}
