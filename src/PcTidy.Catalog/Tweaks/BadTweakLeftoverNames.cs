using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>An installed program the finder reports (matched against the Uninstall list's DisplayName, case-insensitively).</summary>
/// <param name="Name">Text the DisplayName must contain (a product name, never a version).</param>
/// <param name="Kind">What it is, so the card says the right thing.</param>
public sealed record ReportedProgram(string Name, ReportedProgramKind Kind);

public enum ReportedProgramKind
{
    /// <summary>A "PC optimizer" suite: registry cleaner, startup and service tweaks, "turbo" modes in one.</summary>
    OptimizerSuite,
    /// <summary>A registry cleaner (on its own, or the registry part of a cleaner).</summary>
    RegistryCleaner,
}

/// <summary>
/// Small, hand-written name lists the bad-tweak finder reads (v3.1, "FPS boost tweaks" §4 and the comparison page §8.3).
/// Everything here is REPORT-ONLY: the finder shows names and folders, never file contents, and never removes or runs
/// anything on this list. Product names are facts; the descriptions are PC Tidy's own words.
/// </summary>
public static partial class BadTweakLeftoverNames
{
    /// <summary>
    /// Kernel drivers that give any program raw access to memory and hardware ports, left behind by tuning and monitoring
    /// tools (Community tweaks §9.6). File name → the tool that installs it. PC care's <c>VulnerableDriverCatalog</c> lists
    /// some of the same files for their CVEs (WinRing0); the finder says so instead of listing them twice.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HardwareAccessDrivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RwDrv.sys"] = "RWEverything, used by USB \"IMOD\" latency tweaks",
        ["WinRing0x64.sys"] = "older fan-control, RGB and monitoring apps (WinRing0)",
        ["WinRing0.sys"] = "older fan-control, RGB and monitoring apps (WinRing0)",
        // verify: PawnIO's installer registers the service "PawnIO" with this file name.
        ["PawnIO.sys"] = "PawnIO, a newer hardware-access driver used by some fan-control and monitoring apps",
        ["inpoutx64.sys"] = "InpOut32/64, an old port-access driver some tuning and hardware tools use",
    };

    /// <summary>
    /// Helper drivers of AMI's in-Windows BIOS tools (flash and setup-variable editors). Loaded as a service while the
    /// tool runs and often left registered. verify: amifldrv64.sys / amifldrv32.sys are the names AMI's AFU and SCE tools use.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BiosToolDrivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["amifldrv64.sys"] = "AMI's BIOS tools (SCEWIN, AFUWIN)",
        ["amifldrv32.sys"] = "AMI's BIOS tools (SCEWIN, AFUWIN)",
    };

    /// <summary>
    /// File names of in-Windows BIOS tools: AMI's setup-variable editor (SCEWIN / AMISCE, which "BIOS tweak" guides use to
    /// export and re-import every BIOS setting) and AMI's flasher. The inference "SCEWIN" comes from the research and is
    /// marked unconfirmed there.
    /// </summary>
    public static bool IsBiosToolProgram(string fileName) => BiosToolProgram().IsMatch(fileName);

    /// <summary>Settings exports those tools write ("SCEWIN_64.exe /o /s nvram.txt"). Only reported next to a BIOS tool.</summary>
    public static readonly IReadOnlySet<string> BiosExportFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nvram.txt", "BIOSSettings.txt" };

    /// <summary>
    /// "PC optimizer" suites and registry cleaners (Uninstall list DisplayName contains the name). Kept small on purpose:
    /// well-known products only. Report-only: the card suggests uninstalling in PC Tidy's Apps section.
    /// </summary>
    public static readonly IReadOnlyList<ReportedProgram> Programs = new ReportedProgram[]
    {
        new("Advanced SystemCare", ReportedProgramKind.OptimizerSuite),
        new("Wise Care 365", ReportedProgramKind.OptimizerSuite),
        new("Glary Utilities", ReportedProgramKind.OptimizerSuite),
        new("Auslogics BoostSpeed", ReportedProgramKind.OptimizerSuite),
        new("AVG TuneUp", ReportedProgramKind.OptimizerSuite),
        new("Avast Cleanup", ReportedProgramKind.OptimizerSuite),
        new("Norton Utilities", ReportedProgramKind.OptimizerSuite),
        new("System Mechanic", ReportedProgramKind.OptimizerSuite),
        new("Ashampoo WinOptimizer", ReportedProgramKind.OptimizerSuite),
        new("Advanced System Optimizer", ReportedProgramKind.OptimizerSuite),
        new("Restoro", ReportedProgramKind.OptimizerSuite),
        new("Fortect", ReportedProgramKind.OptimizerSuite),
        new("Reimage Repair", ReportedProgramKind.OptimizerSuite),
        new("Outbyte PC Repair", ReportedProgramKind.OptimizerSuite),
        new("Wise Registry Cleaner", ReportedProgramKind.RegistryCleaner),
        new("Auslogics Registry Cleaner", ReportedProgramKind.RegistryCleaner),
        new("Eusing Free Registry Cleaner", ReportedProgramKind.RegistryCleaner),
        new("jv16 PowerTools", ReportedProgramKind.RegistryCleaner),
        new("RegClean Pro", ReportedProgramKind.RegistryCleaner),
        new("Glarysoft Registry Repair", ReportedProgramKind.RegistryCleaner),
        new("CCleaner", ReportedProgramKind.RegistryCleaner),
    };

    /// <summary>The listed program a DisplayName names, or null. Suites win over registry cleaners when both match.</summary>
    public static ReportedProgram? MatchProgram(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        return Programs.Where(p => displayName.Contains(p.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Kind).FirstOrDefault();
    }

    /// <summary>Folders of the Windows drive's root the BIOS-tool search never walks (Windows, apps, other users).</summary>
    public static readonly IReadOnlySet<string> SkippedRootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "$Recycle.Bin", "System Volume Information",
        "Recovery", "PerfLogs", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "OneDriveTemp", "Documents and Settings",
    };

    [GeneratedRegex(@"^(SCEWIN|AMISCE|AFUWIN)[A-Za-z0-9_\-]{0,20}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BiosToolProgram();
}
