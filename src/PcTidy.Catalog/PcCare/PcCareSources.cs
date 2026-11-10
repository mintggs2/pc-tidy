namespace PcTidy.Catalog.PcCare;

/// <summary>Where the PC care lists come from (shown in the vault page and kept next to the data on purpose).</summary>
public static class PcCareSources
{
    public const string MsVulnerableDriverBlocklist = "https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules";
    public const string LolDrivers = "https://www.loldrivers.io/";
    public const string MsPua = "https://learn.microsoft.com/defender-endpoint/detect-block-potentially-unwanted-apps-microsoft-defender-antivirus";
    public const string MsPuaCriteria = "https://learn.microsoft.com/microsoft-365/security/intelligence/criteria";
    public const string MalwarebytesPup = "https://www.malwarebytes.com/blog/detections";
    public const string ChromePermissions = "https://developer.chrome.com/docs/extensions/reference/permissions-list";
    public const string ChromeExtensionPolicy = "https://chromeenterprise.google/policies/#ExtensionInstallForcelist";
    public const string BugCheckReference = "https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-code-reference2";
    public const string FeaturesOnDemand = "https://learn.microsoft.com/windows-hardware/manufacture/desktop/features-on-demand-non-language-fod";
    public const string Smb1 = "https://learn.microsoft.com/windows-server/storage/file-server/troubleshoot/detect-enable-and-disable-smbv1-v2-v3";
    public const string PowerShell2 = "https://learn.microsoft.com/powershell/scripting/windows-powershell/install/windows-powershell-system-requirements";
    public const string CipherWipe = "https://learn.microsoft.com/windows-server/administration/windows-commands/cipher";
}
