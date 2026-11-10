using PcTidy.Features.Tweaks.Reading;

namespace PcTidy.Features.Tweaks.Planning;

/// <summary>
/// Builds the PlanItems both scanners emit. Registry keys go through the <see cref="RegistryRedirect"/>, so the
/// plan writes exactly where the scan read (the test area in tests and the sandbox, the real key on the PC).
/// Every item is a system change (restore point first) and reversible: the platform handlers record the exact
/// prior state (value existed? type, data; old start type) before changing anything.
/// </summary>
public static class TweakPlanItems
{
    public const string TweaksFeature = "tweaks";
    public const string FinderFeature = "tweaks.badtweaks";

    /// <summary>HKLM and policy keys need admin (users can't write HKCU\Software\Policies).</summary>
    public static bool NeedsAdmin(RegistryHive hive, string keyPath) => hive == RegistryHive.LocalMachine || RegistryNames.IsPolicyKey(keyPath);

    public static PlanItem RegistrySet(string feature, RegistryRedirect redirect, RegistryHive hive, string keyPath, string valueName,
        RegistryKind kind, string data, string description)
    {
        var (h, k) = redirect.Map(hive, keyPath);
        return new PlanItem
        {
            Kind = ActionKind.RegistrySetValue, FeatureId = feature, Description = description,
            Registry = new RegistryValueSpec { Hive = h, KeyPath = k, ValueName = valueName, Kind = kind, Data = data },
            NeedsAdmin = NeedsAdmin(hive, keyPath), IsSystemChange = true, Reversible = true,
        };
    }

    public static PlanItem RegistryRemove(string feature, RegistryRedirect redirect, RegistryHive hive, string keyPath, string valueName,
        RegistryKind kind, string description)
    {
        var (h, k) = redirect.Map(hive, keyPath);
        return new PlanItem
        {
            Kind = ActionKind.RegistryDeleteValue, FeatureId = feature, Description = description,
            Registry = new RegistryValueSpec { Hive = h, KeyPath = k, ValueName = valueName, Kind = kind },
            NeedsAdmin = NeedsAdmin(hive, keyPath), IsSystemChange = true, Reversible = true,
        };
    }

    public static PlanItem ServiceStart(string feature, string serviceName, ServiceStart start, string description) => new()
    {
        Kind = ActionKind.ServiceSetStartType, FeatureId = feature, Description = description,
        ServiceName = serviceName, ServiceStartType = start, NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    /// <summary>Store-app removal: undone by reinstalling from the Store, so it's marked not reversible.</summary>
    public static PlanItem RemoveApp(string feature, string packageFullName, string description) => new()
    {
        Kind = ActionKind.AppxRemoveForUser, FeatureId = feature, Description = description,
        Args = new Dictionary<string, string> { ["packageFullName"] = packageFullName },
        IsSystemChange = true, Reversible = false,
    };

    public static PlanItem RemoveFeature(string feature, string featureName, string description) => new()
    {
        Kind = ActionKind.OptionalFeatureRemove, FeatureId = feature, Description = description,
        Args = new Dictionary<string, string> { ["name"] = featureName, ["kind"] = "feature" },
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    /// <summary>
    /// <see cref="ToolIds.BcdDeleteValue"/> with Args["name"]. Reversible when the handler can record the old element value;
    /// for x2apicpolicy and uselegacyapicmode it can't (<see cref="BadTweakCatalog.NotUndoableBootValues"/>), so those are
    /// marked permanent and the confirm screen says so before anything changes.
    /// </summary>
    public static PlanItem DeleteBootValue(string feature, string name, string description) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = feature, Description = description, ToolId = ToolIds.BcdDeleteValue,
        Args = new Dictionary<string, string> { ["name"] = name },
        NeedsAdmin = true, IsSystemChange = true, Reversible = BadTweakCatalog.IsBootRemovalUndoable(name),
    };

    public static PlanItem Tool(string feature, string toolId, string description) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = feature, Description = description, ToolId = toolId,
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    public static PlanItem EnableTask(string feature, string taskPath, string description) => new()
    {
        Kind = ActionKind.ScheduledTaskSetEnabled, FeatureId = feature, Description = description,
        TaskPath = taskPath, Enable = true, NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    /// <summary>HostsCommentOut: Args["lines"] = the exact lines (\n separated); SourcePath = the hosts file read.</summary>
    public static PlanItem CommentOutHosts(string feature, string hostsPath, IReadOnlyList<string> lines, string description) => new()
    {
        Kind = ActionKind.HostsCommentOut, FeatureId = feature, Description = description, SourcePath = hostsPath,
        Args = new Dictionary<string, string> { ["lines"] = string.Join("\n", lines) },
        NeedsAdmin = true, IsSystemChange = true, Reversible = true,
    };

    /// <summary>
    /// PowerPlanSet with Args["schemeGuid"] (the same step Speed offers). The handler records the plan that was active and
    /// undo switches back to it. No admin needed: it's the per-user active plan.
    /// </summary>
    public static PlanItem PowerPlan(string feature, Guid scheme, string description) => new()
    {
        Kind = ActionKind.PowerPlanSet, FeatureId = feature, Description = description,
        Args = new Dictionary<string, string> { ["schemeGuid"] = scheme.ToString("D") },
        Reversible = true,
    };

    public static PlanItem Quarantine(string feature, string path, long size, DateTime lastWriteUtc, string description) => new()
    {
        Kind = ActionKind.QuarantinePath, FeatureId = feature, Description = description, SourcePath = path,
        ExpectedSize = size, ExpectedLastWriteUtc = lastWriteUtc, Reversible = true,
    };
}
