namespace PcTidy.Catalog.PcCare;

public enum OptionalPartKind { Feature, Capability }

/// <summary>
/// An optional Windows part that many home PCs don't use. <paramref name="Name"/> is the exact Windows name
/// (Win32_OptionalFeature.Name, or a capability name like "App.StepsRecorder~~~~0.0.1.0").
/// <paramref name="Marker"/> (capabilities only) is a file, relative to the Windows or Program Files folder, whose
/// presence shows the capability is installed ("{windir}\System32\psr.exe").
/// </summary>
public sealed record OptionalPart(
    string Name, OptionalPartKind Kind, string Title, string Why, string ReAdd, string? Marker = null, bool SecurityBenefit = false, string? Source = null);

/// <summary>
/// The optional features and capabilities PC Tidy offers to remove: situational, never ticked, each with how to add it back.
/// Anything not listed here is never offered (Defender, .NET, WSL, Hyper-V and other developer features stay put).
/// </summary>
public static class OptionalFeatureCatalog
{
    private const string FeaturesReAdd = "To add it back: Settings > System > Optional features > More Windows features, tick it, restart.";
    private const string CapabilityReAdd = "To add it back: Settings > System > Optional features > View features, search for it.";

    public static readonly IReadOnlyList<OptionalPart> All = new OptionalPart[]
    {
        new("SMB1Protocol", OptionalPartKind.Feature, "SMB 1.0 file sharing",
            "A very old file-sharing protocol used by the WannaCry worm. Only very old network drives still need it.", FeaturesReAdd, SecurityBenefit: true, Source: PcCareSources.Smb1),
        new("MicrosoftWindowsPowerShellV2Root", OptionalPartKind.Feature, "Windows PowerShell 2.0",
            "An old PowerShell version Microsoft is retiring; attackers use it to skip modern logging. PowerShell 5.1 stays.", FeaturesReAdd, SecurityBenefit: true, Source: PcCareSources.PowerShell2),
        new("WorkFolders-Client", OptionalPartKind.Feature, "Work Folders",
            "Syncs files with a company Work Folders server. Home PCs almost never use it.", FeaturesReAdd),
        new("Printing-XPSServices-Features", OptionalPartKind.Feature, "Microsoft XPS Document Writer",
            "A virtual printer that makes .xps files. 'Print to PDF' stays.", FeaturesReAdd),
        new("FaxServicesClientPackage", OptionalPartKind.Feature, "Windows Fax and Scan",
            "Sends faxes through a fax modem.", FeaturesReAdd),
        new("TelnetClient", OptionalPartKind.Feature, "Telnet client", "An old, unencrypted remote-terminal tool.", FeaturesReAdd),
        new("TFTP", OptionalPartKind.Feature, "TFTP client", "An old, unencrypted file-transfer tool.", FeaturesReAdd),
        new("SimpleTCP", OptionalPartKind.Feature, "Simple TCP/IP services", "Old test services (echo, quote of the day).", FeaturesReAdd),
        new("Printing-Foundation-LPDPrintService", OptionalPartKind.Feature, "LPD print service", "Lets old Unix computers print through this PC.", FeaturesReAdd),
        new("Printing-Foundation-LPRPortMonitor", OptionalPartKind.Feature, "LPR port monitor", "Prints to old Unix print servers.", FeaturesReAdd),
        new("Internet-Explorer-Optional-amd64", OptionalPartKind.Feature, "Internet Explorer 11",
            "Microsoft no longer supports it. Edge has an IE mode for old sites.", FeaturesReAdd),
        new("WindowsMediaPlayer", OptionalPartKind.Feature, "Windows Media Player Legacy",
            "The old media player. The new Media Player app stays. A few older apps use it to play sound.", FeaturesReAdd),

        new("App.StepsRecorder~~~~0.0.1.0", OptionalPartKind.Capability, "Steps Recorder",
            "Records the steps you take for support. Microsoft is retiring it.", CapabilityReAdd, Marker: @"{windir}\System32\psr.exe", Source: PcCareSources.FeaturesOnDemand),
        new("MathRecognizer~~~~0.0.1.0", OptionalPartKind.Capability, "Math Input Panel",
            "Turns handwritten maths into text.", CapabilityReAdd, Marker: @"{programfiles}\Common Files\Microsoft Shared\Ink\mip.exe", Source: PcCareSources.FeaturesOnDemand),
        new("Microsoft.Windows.WordPad~~~~0.0.1.0", OptionalPartKind.Capability, "WordPad",
            "The old word processor Microsoft is retiring.", CapabilityReAdd, Marker: @"{programfiles}\Windows NT\Accessories\wordpad.exe", Source: PcCareSources.FeaturesOnDemand),
        new("Microsoft.Windows.PowerShell.ISE~~~~0.0.1.0", OptionalPartKind.Capability, "PowerShell ISE",
            "The old PowerShell script editor. PowerShell itself stays.", CapabilityReAdd, Marker: @"{windir}\System32\WindowsPowerShell\v1.0\powershell_ise.exe", Source: PcCareSources.FeaturesOnDemand),
    };

    public static OptionalPart? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
