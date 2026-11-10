namespace PcTidy.Features.Insights;

/// <summary>Ids of the Insights section and its cards (stable: used by tests, history and the page).</summary>
public static class InsightsIds
{
    public const string FeatureId = "insights";

    public const string ScriptScan = "insights.script-scan";
    public const string ModifiedWindows = "insights.modified-windows";
    public const string Reliability = "insights.reliability";
    public const string MemoryTest = "insights.memory-test";
    public const string Backup = "insights.backup";
    public const string Hosts = "insights.hosts";
    public const string DevDrive = "insights.dev-drive";
    public const string GpuReinstall = "insights.gpu-reinstall";
    public const string OneDriveFreeUp = "insights.onedrive-free-up";
    public const string CompressFolders = "insights.compress-folders";
    public const string RefreshRate = "insights.refresh-rate";
    public const string Ram = "insights.ram";
    public const string GamesOnHdd = "insights.games-on-hdd";
    public const string ResizableBar = "insights.resizable-bar";
    public const string Timer = "insights.timer";
    public const string Vanguard = "insights.vanguard";
    public const string Runtimes = "insights.runtimes";
    public const string BitLocker = "insights.bitlocker";
    public const string SmartAppControl = "insights.smart-app-control";

    // Sections that own fixes the cards point to.
    public const string TweaksModule = "tweaks";
    public const string DriversModule = "drivers";
    public const string DriveMoveModule = "drivemove";
    public const string DeveloperModule = "developer";
}
