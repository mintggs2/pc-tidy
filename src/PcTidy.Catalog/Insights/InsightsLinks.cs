namespace PcTidy.Catalog.Insights;

/// <summary>
/// Every page the Insights section can open (official sources only, https or a Windows Settings/Security URI).
/// Opening one is an <c>OpenExternal</c> plan item: harmless, and it never runs anything on the PC.
/// </summary>
public static class InsightsLinks
{
    // ---- GPU drivers (Feature census C5: only the vendor sites and Wagnardsoft are official) ----
    public const string Ddu = "https://www.wagnardsoft.com/";
    public const string NvidiaDrivers = "https://www.nvidia.com/en-us/drivers/";
    public const string AmdDrivers = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDrivers = "https://www.intel.com/content/www/us/en/support/detect.html";

    // ---- Microsoft Learn ----
    public const string DevDriveDocs = "https://learn.microsoft.com/en-us/windows/dev-drive/";
    public const string CompactDocs = "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact";
    public const string VisualCppRedist = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
    public const string DotNetDownloads = "https://dotnet.microsoft.com/download/dotnet";

    // ---- Microsoft support and downloads ----
    /// <summary>Microsoft's own short link to the recovery keys saved to a Microsoft account (from "Find your BitLocker recovery key").</summary>
    public const string RecoveryKeys = "https://aka.ms/myrecoverykey";
    public const string DirectXRuntime = "https://www.microsoft.com/download/details.aspx?id=35";

    // ---- Windows Settings / Windows Security pages ----
    public const string SettingsAdvancedDisplay = "ms-settings:display-advanced";
    public const string SettingsDisksAndVolumes = "ms-settings:disksandvolumes";
    public const string SettingsBackup = "ms-settings:backup";
    public const string SettingsDeviceEncryption = "ms-settings:deviceencryption";
    public const string SettingsWindowsSecurity = "ms-settings:windowsdefender";
    public const string SecurityCoreIsolation = "windowsdefender://coreisolation";
}
