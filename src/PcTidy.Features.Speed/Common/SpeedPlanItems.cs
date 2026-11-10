using System.Globalization;

namespace PcTidy.Features.Speed.Common;

/// <summary>
/// Builds every PlanItem the Speed section can emit, with the Args documented in Core
/// (and the extra optional keys listed on the vault page). Nothing here changes anything.
/// </summary>
public static class SpeedPlanItems
{
    public const string FeatureId = "speed";

    /// <summary>Task-Manager-style on/off via StartupApproved. Args: location = Run | Run32 | StartupFolder, name, hive = HKCU | HKLM.</summary>
    public static PlanItem StartupToggle(string displayName, string location, string name, RegistryHive hive, bool enable) => new()
    {
        Kind = ActionKind.StartupItemSetEnabled,
        FeatureId = FeatureId,
        Description = enable ? $"Let {displayName} start with Windows again" : $"Stop {displayName} starting with Windows (it stays installed)",
        Enable = enable,
        NeedsAdmin = hive == RegistryHive.LocalMachine,
        Args = new Dictionary<string, string>
        {
            ["location"] = location,
            ["name"] = name,
            ["hive"] = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
        },
    };

    /// <summary>Turns a scheduled task off (never deletes it). Changing tasks needs the admin helper.</summary>
    public static PlanItem TaskToggle(string displayName, string taskPath, bool enable) => new()
    {
        Kind = ActionKind.ScheduledTaskSetEnabled,
        FeatureId = FeatureId,
        Description = enable ? $"Turn the task '{displayName}' back on" : $"Turn off the task '{displayName}' (it isn't deleted)",
        TaskPath = taskPath,
        Enable = enable,
        NeedsAdmin = true,
        IsSystemChange = true,
    };

    /// <summary>Sets a service to Manual: Windows can still start it when something needs it. Never Disabled.</summary>
    public static PlanItem ServiceToManual(string displayName, string serviceName) => new()
    {
        Kind = ActionKind.ServiceSetStartType,
        FeatureId = FeatureId,
        Description = $"Set the '{displayName}' service to start only when needed (Manual)",
        ServiceName = serviceName,
        ServiceStartType = ServiceStart.Manual,
        NeedsAdmin = true,
        IsSystemChange = true,
    };

    /// <summary>Politely asks an app to close (like clicking its X). Args: pid, processName, optional startedUtc (guards against a reused PID).</summary>
    public static PlanItem CloseApp(string displayName, int pid, string processName, DateTime? startedUtc)
    {
        var args = new Dictionary<string, string>
        {
            ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
            ["processName"] = processName,
        };
        if (startedUtc is { } s) args["startedUtc"] = s.ToString("O", CultureInfo.InvariantCulture);
        return new PlanItem
        {
            Kind = ActionKind.CloseApp,
            FeatureId = FeatureId,
            Description = $"Ask {displayName} to close (it may ask you to save first)",
            Reversible = false,
            Args = args,
        };
    }

    /// <summary>Switches the active power plan. No admin needed.</summary>
    public static PlanItem PowerPlan(string planName, Guid scheme) => new()
    {
        Kind = ActionKind.PowerPlanSet,
        FeatureId = FeatureId,
        Description = $"Switch the power plan to {planName}",
        Args = new Dictionary<string, string> { ["schemeGuid"] = scheme.ToString("D") },
    };

    /// <summary>One registry value from the Speed catalog (<see cref="SpeedRegistryAllowances"/>).</summary>
    public static PlanItem Registry(string description, RegistryHive hive, string key, string value, string data) => new()
    {
        Kind = ActionKind.RegistrySetValue,
        FeatureId = FeatureId,
        Description = description,
        NeedsAdmin = hive == RegistryHive.LocalMachine,
        IsSystemChange = hive == RegistryHive.LocalMachine,
        Registry = new RegistryValueSpec { Hive = hive, KeyPath = key, ValueName = value, Kind = RegistryKind.DWord, Data = data },
    };

    /// <summary>
    /// Optimize one volume with Windows' own optimizer. Args: drive (letter), expect = retrim | defrag (what the scan
    /// saw; the handler decides by media type and must refuse to defrag an SSD). Not reversible, harmless, admin.
    /// </summary>
    public static PlanItem OptimizeVolume(char letter, bool isSsd) => new()
    {
        Kind = ActionKind.VolumeOptimize,
        FeatureId = FeatureId,
        Description = isSsd ? $"Tell the SSD {letter}: which space is free (ReTrim)" : $"Defragment the hard drive {letter}:",
        Reversible = false,
        NeedsAdmin = true,
        Args = new Dictionary<string, string>
        {
            ["drive"] = letter.ToString(),
            ["expect"] = isSsd ? "retrim" : "defrag",
        },
    };

    /// <summary>Hides (or shows again) a shell extension through the Blocked list. Args: clsid, hive, name.</summary>
    public static PlanItem ShellExtension(string displayName, Guid clsid, bool enable) => new()
    {
        Kind = ActionKind.ShellExtensionSetEnabled,
        FeatureId = FeatureId,
        Description = enable ? $"Show '{displayName}' in the right-click menu again" : $"Hide '{displayName}' from the right-click menu (not deleted)",
        Enable = enable,
        Args = new Dictionary<string, string>
        {
            ["clsid"] = clsid.ToString("B").ToUpperInvariant(),
            ["hive"] = "HKCU",
            ["name"] = displayName,
        },
    };

    /// <summary>Hides (or shows again) a static menu entry with LegacyDisable on its verb key. Args: verbKey (no hive prefix), hive, name.</summary>
    public static PlanItem ShellVerb(string displayName, RegistryHive hive, string verbKey, bool enable) => new()
    {
        Kind = ActionKind.ShellExtensionSetEnabled,
        FeatureId = FeatureId,
        Description = enable ? $"Show '{displayName}' in the right-click menu again" : $"Hide '{displayName}' from the right-click menu (not deleted)",
        Enable = enable,
        NeedsAdmin = hive == RegistryHive.LocalMachine,
        Args = new Dictionary<string, string>
        {
            ["verbKey"] = verbKey,
            ["hive"] = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
            ["name"] = displayName,
        },
    };
}
