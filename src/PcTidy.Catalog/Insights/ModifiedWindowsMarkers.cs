namespace PcTidy.Catalog.Insights;

/// <summary>Where a modified-Windows sign is looked for (all read-only).</summary>
public enum MarkerKind
{
    /// <summary>Text in HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation (Manufacturer, Model, SupportURL…).</summary>
    OemText,
    /// <summary>Text in RegisteredOwner / RegisteredOrganization / ProductName (HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion).</summary>
    RegisteredText,
    /// <summary>A folder under %WINDIR%.</summary>
    WindowsFolder,
    /// <summary>A folder under Program Files.</summary>
    ProgramFilesFolder,
}

/// <summary>One sign that a playbook or a custom image was used. Every marker is a heuristic (Feature census C4: no tool documents them).</summary>
public sealed record ModifiedWindowsMarker(string Build, MarkerKind Kind, string Value);

/// <summary>
/// Read-only "modified Windows" signs (Plan v3 census addition; Feature census C4 rows Atlas / ReviOS / AME / Tiny11).
/// The research has no verified marker list, so the card always says the result is a best guess and never acts on it.
/// Two families: names left behind by the tools, and core Windows services that a stock install always has.
/// </summary>
public static class ModifiedWindowsMarkers
{
    public const string AtlasOs = "AtlasOS";
    public const string ReviOs = "ReviOS";
    public const string Ame = "AME Wizard playbook";
    public const string Tiny11 = "Tiny11";
    public const string Custom = "a customised Windows image";

    public static readonly IReadOnlyList<ModifiedWindowsMarker> Names = new ModifiedWindowsMarker[]
    {
        new(AtlasOs, MarkerKind.OemText, "Atlas"),
        new(AtlasOs, MarkerKind.OemText, "AtlasOS"),
        new(AtlasOs, MarkerKind.RegisteredText, "AtlasOS"),
        new(AtlasOs, MarkerKind.WindowsFolder, "AtlasModules"),
        new(AtlasOs, MarkerKind.WindowsFolder, "AtlasDesktop"),
        new(ReviOs, MarkerKind.OemText, "ReviOS"),
        new(ReviOs, MarkerKind.RegisteredText, "ReviOS"),
        new(ReviOs, MarkerKind.ProgramFilesFolder, "Revision Tool"),
        new(Ame, MarkerKind.OemText, "Ameliorated"),
        new(Tiny11, MarkerKind.OemText, "tiny11"),
        new(Tiny11, MarkerKind.RegisteredText, "tiny11"),
        new(Custom, MarkerKind.OemText, "Ghost Spectre"),
        new(Custom, MarkerKind.RegisteredText, "Ghost Spectre"),
    };

    /// <summary>
    /// Services every stock Windows 10/11 install has (their registry keys exist even when the service is set to Disabled).
    /// A missing key means the component was removed from the image, which Tiny11 Core and some playbooks do.
    /// </summary>
    public static readonly IReadOnlyList<(string Service, string What)> CoreServices = new[]
    {
        ("WinDefend", "Microsoft Defender Antivirus"),
        ("wuauserv", "Windows Update"),
        ("UsoSvc", "Update Orchestrator"),
        ("WaaSMedicSvc", "Windows Update Medic"),
        ("BITS", "Background Intelligent Transfer"),
        ("TrustedInstaller", "Windows Modules Installer"),
        ("mpssvc", "Windows Defender Firewall"),
        ("wscsvc", "Security Center"),
    };
}
