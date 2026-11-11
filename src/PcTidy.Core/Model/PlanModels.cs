using System.Text.Json.Serialization;

namespace PcTidy.Core.Model;

/// <summary>Registry value description used by registry plan items.</summary>
public sealed record RegistryValueSpec
{
    public required RegistryHive Hive { get; init; }
    /// <summary>Key path without hive, e.g. "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced".</summary>
    public required string KeyPath { get; init; }
    /// <summary>Value name; "" = default value. Null for key-level operations.</summary>
    public string? ValueName { get; init; }
    public RegistryKind Kind { get; init; } = RegistryKind.DWord;
    /// <summary>Data as invariant string: DWord/QWord decimal or 0x hex; Binary = hex bytes; MultiString = \n separated.</summary>
    public string? Data { get; init; }
    /// <summary>true = 64-bit view (default), false = 32-bit (WOW6432Node) view.</summary>
    public bool View64 { get; init; } = true;
}

/// <summary>Exact prior state of a registry value, captured before a change (for undo).</summary>
public sealed record RegistryPriorState
{
    public bool KeyExisted { get; init; }
    public bool ValueExisted { get; init; }
    public RegistryKind? Kind { get; init; }
    public string? Data { get; init; }
    /// <summary>Path to a .reg export (key-level operations).</summary>
    public string? RegExportPath { get; init; }
}

/// <summary>
/// One change PC Tidy may make. Features build these; the engine executes them.
/// Every item is re-checked by the SafetyGuard right before it runs.
/// </summary>
public sealed record PlanItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required ActionKind Kind { get; init; }
    /// <summary>Feature that created it (e.g. "cleanup", "organize").</summary>
    public required string FeatureId { get; init; }
    /// <summary>Plain-English description shown in confirm and history ("Move 'cat.jpg' to Downloads\Pictures").</summary>
    public required string Description { get; init; }

    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>Size seen at scan time (bytes, logical). Used for totals and re-verification.</summary>
    public long ExpectedSize { get; init; }
    /// <summary>Last-write time seen at scan time. If the file changed since, the step is skipped.</summary>
    public DateTime? ExpectedLastWriteUtc { get; init; }

    /// <summary>
    /// Id of the cleanup target / carve-out that allows touching a path inside a protected area
    /// (e.g. "cleanup.windows-temp"). Only the SafetyGuard's carve-out table can honour it.
    /// </summary>
    public string? CarveOutId { get; init; }

    public bool NeedsAdmin { get; init; }
    /// <summary>System-level change: a restore point is made first.</summary>
    public bool IsSystemChange { get; init; }
    /// <summary>False = cannot be undone (shown in red; never ticked by default).</summary>
    public bool Reversible { get; init; } = true;

    /// <summary>Only for in-folder moves/renames: what to do if the destination exists.</summary>
    public NameClashPolicy ClashPolicy { get; init; } = NameClashPolicy.AutoNumber;

    public RegistryValueSpec? Registry { get; init; }
    public string? ServiceName { get; init; }
    public ServiceStart? ServiceStartType { get; init; }
    public string? TaskPath { get; init; }
    /// <summary>Enable/disable flag for toggles (tasks, startup items, shell verbs).</summary>
    public bool? Enable { get; init; }
    /// <summary>For RunSystemTool: one of <see cref="ToolIds"/>.</summary>
    public string? ToolId { get; init; }
    public Dictionary<string, string>? Args { get; init; }

    /// <summary>Apps that must be closed first (process names without .exe). The engine checks; it never closes them.</summary>
    public IReadOnlyList<string>? RequiresClosedApps { get; init; }

    /// <summary>
    /// Ids of earlier items in the same plan that must have finished successfully first (e.g. "back up drivers" before
    /// "remove driver"; "redirect Downloads" before "quarantine the old copy"). If one was skipped or failed, this item
    /// is skipped too. They must come earlier in the plan; undo ignores them.
    /// </summary>
    public IReadOnlyList<string>? DependsOn { get; init; }
}

/// <summary>Whitelisted system tools the elevated helper may run. Nothing else can be run.</summary>
public static class ToolIds
{
    public const string DeliveryOptimizationClear = "do.clear";             // Delete-DeliveryOptimizationCache -Force
    public const string DismAnalyzeComponentStore = "dism.analyze";         // read-only
    public const string DismStartComponentCleanup = "dism.startcomponentcleanup"; // never /ResetBase
    public const string CleanmgrHandler = "cleanmgr.handler";              // Args["handler"] = VolumeCaches key name (whitelisted list)
    public const string HibernateType = "powercfg.hibernate";              // Args["type"] = full|reduced|off|on
    public const string SfcScan = "sfc.scannow";
    public const string DismRestoreHealth = "dism.restorehealth";
    public const string FlushDns = "net.flushdns";
    public const string WinsockReset = "net.winsockreset";
    public const string DefenderQuickScan = "defender.quickscan";
    public const string WingetUpgrade = "winget.upgrade";                  // Args["id"]
    public const string PnpExportDrivers = "pnputil.export";               // Args["folder"]
    public const string PnpDeleteDriver = "pnputil.delete";                // Args["inf"] (never /force)
    public const string PnpAddDrivers = "pnputil.add";                     // Args["folder"]
    public const string PnpRemoveDevice = "pnputil.removedevice";          // Args["instanceId"]
    public const string WindowsUpdateInstallDrivers = "wua.installdrivers";// Args["updateIds"]
    public const string ShadowStorageResize = "vssadmin.resize";           // Args["percent"] (>=5)
    public const string BcdDeleteValue = "bcdedit.deletevalue";            // Args["name"] - only removals of known-bad values
    public const string NetshAutotuningNormal = "netsh.autotuning.normal";
    public const string WslShutdown = "wsl.shutdown";
    public const string WslSetSparse = "wsl.setsparse";                    // Args["distro"]
    public const string DockerPrune = "docker.prune";                      // Args["what"] = images|builder|containers
    public const string DevCacheClean = "dev.cacheclean";                  // Args["tool"] = npm|pnpm|yarn|pip|nuget-http|gradle
    public const string OneDriveShutdown = "onedrive.shutdown";
    public const string OneDriveStart = "onedrive.start";
    public const string DriverRollback = "driver.rollback";                // Args["instanceId"]
    public const string RestorePointCreate = "restorepoint.create";        // Args["description"] (admin; verified by listing)
    public const string CipherWipeFreeSpace = "cipher.wipefreespace";      // Args["drive"] e.g. "D:\" (HDD only; permanent; slow)
    public const string CleanmgrStorageSense = "storagesense.open";        // opens Settings > Storage (harmless)
    public const string CompactLzx = "compact.lzx";                        // Args["folder"]: NTFS LZX compression (undo: compact /u)

    // ---- v3.1 (fix-it recipes, runtimes) ----
    public const string ServiceRestart = "service.restart";                // Args["name"]: stop + start one allow-listed service (start type unchanged)
    public const string WindowsUpdateReset = "wu.resetcomponents";         // Microsoft's documented reset: stop services, rename SoftwareDistribution/catroot2 to *.old-<stamp>, start
    public const string MsiReregister = "msi.reregister";                  // msiexec /unregister + /regserver (Windows Installer errors 2502/2503 and friends)
    public const string WingetInstall = "winget.install";                  // Args["id"]: only allow-listed Microsoft runtimes (VC++, DirectX, .NET Desktop)
}

/// <summary>A frozen list of changes the user confirmed (or wants simulated).</summary>
public sealed record ActionPlan
{
    public string RunId { get; init; } = NewRunId();
    /// <summary>Set when this plan is one part of an "Optimize everything" run.</summary>
    public string? ParentRunId { get; init; }
    public required string FeatureId { get; init; }
    public required string Title { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public required IReadOnlyList<PlanItem> Items { get; init; }

    [JsonIgnore] public bool RequiresRestorePoint => Items.Any(i => i.IsSystemChange);
    [JsonIgnore] public bool RequiresAdmin => Items.Any(i => i.NeedsAdmin);
    [JsonIgnore] public long TotalBytes => Items.Sum(i => i.ExpectedSize);

    public static string NewRunId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
}
