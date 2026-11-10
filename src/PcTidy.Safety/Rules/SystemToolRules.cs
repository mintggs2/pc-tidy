using System.Reflection;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Safety.Rules;

/// <summary>
/// Which whitelisted Windows tools may run and with which arguments. Every id in <see cref="ToolIds"/> is known;
/// anything else is refused. Arguments are checked against tight patterns so a plan can't smuggle in
/// "/ResetBase", "/force" or an arbitrary command.
/// </summary>
public static partial class SystemToolRules
{
    /// <summary>All ids declared in <see cref="ToolIds"/> (read by reflection, so new ids are picked up automatically).</summary>
    public static readonly IReadOnlySet<string> KnownIds = typeof(ToolIds)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tools whose effect can't be undone; their plan items must say Reversible = false.</summary>
    public static readonly IReadOnlySet<string> Permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ToolIds.DeliveryOptimizationClear, ToolIds.DismStartComponentCleanup, ToolIds.CleanmgrHandler,
        ToolIds.DockerPrune, ToolIds.DevCacheClean, ToolIds.PnpDeleteDriver, ToolIds.CipherWipeFreeSpace, ToolIds.RestorePointCreate,
        // Shrinking restore point space makes Windows delete snapshots (possibly the newest): can't be undone.
        ToolIds.ShadowStorageResize,
    };

    /// <summary>Disk Cleanup handlers PC Tidy may run. Never "Downloads Folder", "Device Driver Packages" or the Recycle Bin.</summary>
    public static readonly IReadOnlySet<string> CleanmgrHandlers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbnail Cache", "Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files", "Update Cleanup",
        "Delivery Optimization Files", "Setup Log Files", "Windows ESD installation files", "D3D Shader Cache",
        "Windows Error Reporting Files", "System error memory dump files", "System error minidump files", "Old ChkDsk Files",
        "Temporary Files", "Downloaded Program Files", "Temporary Internet Files",
    };

    /// <summary>
    /// Boot settings the bad-tweak finder may REMOVE (deleting the value restores Windows' default): timer tweaks,
    /// security/anti-cheat blockers left by "optimizers", and CPU/memory limits. Nothing can be added or set.
    /// </summary>
    private static readonly HashSet<string> BcdRemovableValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "useplatformclock", "useplatformtick", "disabledynamictick", "tscsyncpolicy",
        "testsigning", "nointegritychecks", "debug", "bootdebug",
        "x2apicpolicy", "uselegacyapicmode", "increaseuserva", "quietboot", "numproc", "onecpu", "removememory",
    };

    private static readonly Dictionary<string, string[]> Choices = new(StringComparer.OrdinalIgnoreCase)
    {
        [ToolIds.HibernateType] = new[] { "full", "reduced", "off", "on" },
        [ToolIds.DockerPrune] = new[] { "images", "builder", "containers" },
        [ToolIds.DevCacheClean] = new[] { "npm", "pnpm", "yarn", "pip", "nuget-http", "gradle" },
    };

    private static readonly Dictionary<string, string> ChoiceArg = new(StringComparer.OrdinalIgnoreCase)
    {
        [ToolIds.HibernateType] = "type", [ToolIds.DockerPrune] = "what", [ToolIds.DevCacheClean] = "tool",
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$")] private static partial Regex PackageIdPattern();
    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase)] private static partial Regex OemInfPattern();
    [GeneratedRegex(@"^[A-Za-z0-9\\&_.\-{}#]{1,400}$")] private static partial Regex InstanceIdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")] private static partial Regex DistroPattern();
    [GeneratedRegex(@"^[A-Za-z0-9\-,]{1,4000}$")] private static partial Regex UpdateIdsPattern();

    /// <summary>Folder argument a tool writes to (checked like any destination) or reads from (must be a full path).</summary>
    public static (string ArgName, bool Writes)? FolderArgument(string toolId) => toolId.ToLowerInvariant() switch
    {
        ToolIds.PnpExportDrivers => ("folder", true),
        ToolIds.PnpAddDrivers => ("folder", false),
        // compact /c /exe:lzx rewrites every file in the folder: checked like a destination (protected, cloud, games...).
        ToolIds.CompactLzx => ("folder", true),
        _ => null,
    };

    /// <summary>Null when the tool and its arguments are allowed; otherwise a plain-English reason.</summary>
    public static string? Validate(PlanItem item)
    {
        var id = item.ToolId;
        if (string.IsNullOrWhiteSpace(id) || !KnownIds.Contains(id)) return Reasons.UnknownTool;
        var args = item.Args ?? new Dictionary<string, string>();
        if (args.Values.Any(v => v is null || v.Contains("resetbase", StringComparison.OrdinalIgnoreCase) || v.Contains("/force", StringComparison.OrdinalIgnoreCase)))
            return Reasons.ToolArgument("a forbidden switch");
        if (Permanent.Contains(id) && item.Reversible) return Reasons.MustBeMarkedPermanent;

        string? Arg(string name) => args.TryGetValue(name, out var v) ? v?.Trim() : null;

        if (ChoiceArg.TryGetValue(id, out var argName))
            return Choices[id].Contains(Arg(argName) ?? "", StringComparer.OrdinalIgnoreCase) ? null : Reasons.ToolArgument($"{argName} not allowed");

        return id.ToLowerInvariant() switch
        {
            ToolIds.CleanmgrHandler => CleanmgrHandlers.Contains(Arg("handler") ?? "") ? null : Reasons.ToolArgument("that Disk Cleanup handler"),
            ToolIds.WingetUpgrade => PackageIdPattern().IsMatch(Arg("id") ?? "") ? null : Reasons.ToolArgument("package id"),
            ToolIds.PnpDeleteDriver => OemInfPattern().IsMatch(Arg("inf") ?? "") ? null : Reasons.ToolArgument("only oemNN.inf driver packages"),
            ToolIds.PnpRemoveDevice or ToolIds.DriverRollback => InstanceIdPattern().IsMatch(Arg("instanceId") ?? "") ? null : Reasons.ToolArgument("device id"),
            ToolIds.WindowsUpdateInstallDrivers => UpdateIdsPattern().IsMatch(Arg("updateIds") ?? "") ? null : Reasons.ToolArgument("update ids"),
            ToolIds.ShadowStorageResize => int.TryParse(Arg("percent"), out var pct) && pct is >= 5 and <= 100 ? null : Reasons.ToolArgument("restore point space below 5%"),
            ToolIds.BcdDeleteValue => BcdRemovableValues.Contains(Arg("name") ?? "") ? null : Reasons.ToolArgument("that boot setting"),
            ToolIds.WslSetSparse => DistroPattern().IsMatch(Arg("distro") ?? "") ? null : Reasons.ToolArgument("distro name"),
            ToolIds.PnpExportDrivers or ToolIds.PnpAddDrivers or ToolIds.CompactLzx => string.IsNullOrWhiteSpace(Arg("folder")) ? Reasons.ToolArgument("missing folder") : null,
            _ => null,
        };
    }
}
