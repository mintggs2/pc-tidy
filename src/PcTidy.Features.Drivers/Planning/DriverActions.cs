using PcTidy.Core.Model;

namespace PcTidy.Features.Drivers.Planning;

/// <summary>
/// The only PlanItems the Drivers section ever builds. Every driver change is a whitelisted Windows tool run by
/// PcTidy.Platform in the admin helper; no item ever carries /force, /uninstall or a bulk device filter.
/// </summary>
public static class DriverActions
{
    public const string FeatureId = "drivers";

    /// <summary>`pnputil /export-driver * &lt;folder&gt;`: a copy of every third-party driver. Changes nothing on the system.</summary>
    public static PlanItem ExportAll(string folder) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.PnpExportDrivers,
        Args = new Dictionary<string, string> { ["folder"] = folder },
        Description = $"Back up all third-party drivers to {folder}",
        DestinationPath = folder,
        NeedsAdmin = true,
        IsSystemChange = false,
        // The backup itself changes nothing on the system, so there is nothing to undo (the copy is kept).
        Reversible = true,
    };

    /// <summary>`pnputil /delete-driver oemNN.inf` (never /force, never /uninstall).</summary>
    public static PlanItem DeletePackage(string publishedName, string what, long sizeBytes) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.PnpDeleteDriver,
        Args = new Dictionary<string, string> { ["inf"] = publishedName },
        Description = $"Remove the old driver package {publishedName} ({what}) from the driver store",
        ExpectedSize = sizeBytes,
        NeedsAdmin = true,
        IsSystemChange = true,
        Reversible = false,
    };

    /// <summary>`pnputil /remove-device &lt;instance id&gt;` for exactly one device (no /subtree, /deviceid, /class or /force).</summary>
    public static PlanItem RemoveDevice(string instanceId, string name) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.PnpRemoveDevice,
        Args = new Dictionary<string, string> { ["instanceId"] = instanceId },
        Description = $"Remove the old device entry \"{name}\" (its saved settings are lost)",
        NeedsAdmin = true,
        IsSystemChange = true,
        Reversible = false,
    };

    /// <summary>Windows' documented DiRollbackDriver for one device (only offered when the previous package is still in the store).</summary>
    public static PlanItem Rollback(string instanceId, string name, string previousVersion) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.DriverRollback,
        Args = new Dictionary<string, string> { ["instanceId"] = instanceId },
        Description = $"Roll back \"{name}\" to its previous driver ({previousVersion})",
        NeedsAdmin = true,
        IsSystemChange = true,
        Reversible = false,
    };

    /// <summary>Install driver updates offered by Windows Update. Args updateIds = comma-separated WUA update ids.</summary>
    public static PlanItem InstallWindowsUpdates(IReadOnlyList<string> updateIds, string description) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.WindowsUpdateInstallDrivers,
        Args = new Dictionary<string, string> { ["updateIds"] = string.Join(',', updateIds) },
        Description = description,
        NeedsAdmin = true,
        IsSystemChange = true,
        Reversible = false,
    };

    /// <summary>`pnputil /add-driver &lt;folder&gt;\*.inf /subdirs /install`: put drivers back from a PC Tidy backup.</summary>
    public static PlanItem RestoreFromBackup(string folder, string when) => new()
    {
        Kind = ActionKind.RunSystemTool,
        FeatureId = FeatureId,
        ToolId = ToolIds.PnpAddDrivers,
        Args = new Dictionary<string, string> { ["folder"] = folder },
        Description = $"Put back the drivers from the backup of {when}",
        SourcePath = folder,
        NeedsAdmin = true,
        IsSystemChange = true,
        Reversible = false,
    };

    /// <summary>Opens an official page (https) or a Settings page. Harmless; nothing to undo.</summary>
    public static PlanItem Open(string target, string description) => new()
    {
        Kind = ActionKind.OpenExternal,
        FeatureId = FeatureId,
        Args = new Dictionary<string, string> { ["target"] = target },
        Description = description,
    };

    /// <summary>
    /// A row's steps: the backup, then the change, which only runs if the backup finished (<see cref="PlanItem.DependsOn"/>).
    /// <see cref="DriverPlanBuilder"/> merges the backups of several rows into one and re-points the changes at it.
    /// </summary>
    public static PlanItem[] AfterBackup(string exportFolder, PlanItem change)
    {
        var backup = ExportAll(exportFolder);
        return new[] { backup, NeedsBackup(change, backup) };
    }

    /// <summary>The change, made to need exactly <paramref name="backup"/> (any older backup dependency is replaced).</summary>
    public static PlanItem NeedsBackup(PlanItem change, PlanItem backup) => change with { DependsOn = new[] { backup.Id } };

    /// <summary>Tools that change drivers or devices (anything but the backup itself and opening pages).</summary>
    public static bool IsDriverChange(PlanItem item)
        => item.Kind == ActionKind.RunSystemTool && item.ToolId is ToolIds.PnpDeleteDriver or ToolIds.DriverRollback
            or ToolIds.WindowsUpdateInstallDrivers or ToolIds.PnpAddDrivers;

    public static bool IsExport(PlanItem item) => item.Kind == ActionKind.RunSystemTool && item.ToolId == ToolIds.PnpExportDrivers;

    public static bool IsWindowsUpdateInstall(PlanItem item) => item.Kind == ActionKind.RunSystemTool && item.ToolId == ToolIds.WindowsUpdateInstallDrivers;

    public static bool IsDeviceRemoval(PlanItem item) => item.Kind == ActionKind.RunSystemTool && item.ToolId == ToolIds.PnpRemoveDevice;
}
