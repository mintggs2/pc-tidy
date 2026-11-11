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
        // v3.1: nothing to put back (a service restart, re-registering Windows Installer), not undone on purpose (the
        // Windows Update reset keeps the old folders, but putting them back would bring the problem back), or an install
        // PC Tidy doesn't remove (a Microsoft runtime other apps may then rely on).
        ToolIds.ServiceRestart, ToolIds.MsiReregister, ToolIds.WindowsUpdateReset, ToolIds.WingetInstall,
    };

    /// <summary>service.restart: the only services that may be restarted (same list as the platform's SystemToolCatalog).</summary>
    public static readonly IReadOnlySet<string> RestartableServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WlanSvc", "bthserv", "BTAGService", "wuauserv", "BITS", "CryptSvc", "msiserver", "Audiosrv", "AudioEndpointBuilder",
    };

    /// <summary>winget.install: Microsoft's runtimes only, by exact winget-pkgs id (same list as the platform's SystemToolCatalog).</summary>
    public static readonly IReadOnlySet<string> WingetRuntimeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DirectX",
        "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.9",
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
        // A leftover safe-boot flag: removing it starts Windows normally again (the platform uses {current}).
        "safeboot", "safebootalternateshell",
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
            // v3.1 fix-it tools: exact settings only, admin where Windows needs it.
            ToolIds.ServiceRestart => !OnlyArgs(args, "name") || !RestartableServices.Contains(Arg("name") ?? "") ? Reasons.ToolArgument("restarting that service")
                : !item.NeedsAdmin ? Reasons.ToolArgument("restarting a service without admin rights") : null,
            // The one Windows-folder exception (SoftwareDistribution, catroot2 renamed, never deleted): its paths are fixed in
            // the platform's code, so any setting at all is refused, and a restore point is made first.
            ToolIds.WindowsUpdateReset => args.Count > 0 ? Reasons.ToolArgument("settings for the Windows Update reset")
                : !item.NeedsAdmin || !item.IsSystemChange ? Reasons.ToolArgument("the Windows Update reset without admin rights and a restore point") : null,
            ToolIds.MsiReregister => args.Count > 0 ? Reasons.ToolArgument("settings for Windows Installer")
                : !item.NeedsAdmin ? Reasons.ToolArgument("Windows Installer without admin rights") : null,
            // Runs as the user, like winget.upgrade (the admin helper refuses winget).
            ToolIds.WingetInstall => !OnlyArgs(args, "id") || !WingetRuntimeIds.Contains(Arg("id") ?? "") ? Reasons.ToolArgument("installing that package")
                : item.NeedsAdmin ? Reasons.ToolArgument("winget with admin rights") : null,
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

    /// <summary>Exactly these argument keys (case-insensitive), each present once.</summary>
    private static bool OnlyArgs(Dictionary<string, string> args, params string[] keys)
        => args.Count == keys.Length && args.Keys.All(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
}
