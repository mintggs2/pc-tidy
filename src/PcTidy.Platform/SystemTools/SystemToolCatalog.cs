using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>One exact command: an absolute executable path plus fixed arguments.</summary>
public sealed record ToolCommand(string ExePath, IReadOnlyList<string> Arguments)
{
    public override string ToString() => $"{ExePath} {string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
}

/// <summary>What a RunSystemTool item resolves to. Built only by <see cref="SystemToolCatalog"/>.</summary>
public sealed record SystemToolPlan
{
    public required string ToolId { get; init; }
    public required string Title { get; init; }
    /// <summary>The programs run, in order (empty for tools that are a documented API call, see <see cref="Api"/>).</summary>
    public required IReadOnlyList<ToolCommand> Commands { get; init; }
    /// <summary>For API tools: the call made, shown in simulations ("DnsFlushResolverCache()").</summary>
    public string? Api { get; init; }
    public bool Reversible { get; init; }
    public bool NeedsAdmin { get; init; } = true;
    /// <summary>Works on the signed-in user's own things (their apps, caches, OneDrive): never run from the admin helper.</summary>
    public bool PerUser { get; init; }
    /// <summary>Set when the program isn't on this PC (the step is skipped with this reason; simulations still show the command).</summary>
    public string? MissingProgram { get; init; }
    /// <summary>Start the program and don't wait (OneDrive /background).</summary>
    public bool Launch { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Exit codes that mean success; <see cref="RestartCodes"/> also mean "restart to finish".</summary>
    public IReadOnlyList<int> SuccessCodes { get; init; } = new[] { 0 };
    public IReadOnlyList<int> RestartCodes { get; init; } = Array.Empty<int>();
    /// <summary>What undo runs (compact /u, OneDrive start). Null = nothing to run.</summary>
    public IReadOnlyList<ToolCommand>? UndoCommands { get; init; }

    /// <summary>cleanmgr only: the VolumeCaches handler switched on for this run.</summary>
    public string? CleanmgrHandler { get; init; }
    /// <summary>powercfg.hibernate only: full | reduced | off | on.</summary>
    public string? HibernateTarget { get; init; }
    /// <summary>powercfg.hibernate only: Args["undoType"], the state to go back to on undo (same values).</summary>
    public string? HibernateUndoTarget { get; init; }
    /// <summary>bcdedit.deletevalue: the boot setting name, and its BCD element type when PC Tidy can read it back.</summary>
    public string? BcdName { get; init; }
    public uint? BcdElementType { get; init; }
    /// <summary>pnputil.export / pnputil.add / compact.lzx: the folder argument.</summary>
    public string? Folder { get; init; }
    /// <summary>cipher.wipefreespace / vssadmin.resize: the drive letter.</summary>
    public char? Drive { get; init; }
    /// <summary>vssadmin.resize: the new maximum, in percent of the drive.</summary>
    public int? Percent { get; init; }
    public string? RestorePointDescription { get; init; }
    public IReadOnlyList<string>? UpdateIds { get; init; }
    public string? InstanceId { get; init; }
    /// <summary>dev.cacheclean gradle: after "gradle --stop", quarantine caches of Gradle versions no longer used.</summary>
    public string? GradleCachesFolder { get; init; }

    /// <summary>How the step reads in a simulation.</summary>
    public string Display => Commands.Count > 0 ? string.Join(" then ", Commands) + (Api is { } a ? ", then " + a : "") : Api ?? Title;
}

/// <summary>
/// The hard whitelist of Windows tools PC Tidy may run. Tool ids map to exact executables (System32, or a program's own
/// known install folder for winget, Defender, Docker, OneDrive and developer tools) with fixed arguments; the only inputs
/// accepted are the validated values below. Unknown argument keys are refused. No shell is ever used.
/// DISM always gets /English so the one summary PC Tidy reads is the same on every PC language.
/// </summary>
public static partial class SystemToolCatalog
{
    /// <summary>The sageset slot PC Tidy uses for cleanmgr (an arbitrary number unlikely to clash with others).</summary>
    public const int CleanmgrSageSet = 6431;

    /// <summary>LZX compression of a folder (Core's ToolIds.CompactLzx).</summary>
    public const string CompactLzx = ToolIds.CompactLzx;

    /// <summary>Only these Disk Cleanup handlers may be run (VolumeCaches key names, the same on every language).</summary>
    public static readonly IReadOnlyList<string> CleanmgrHandlers = new[] { "Previous Installations", "Thumbnail Cache", "Update Cleanup" };

    public static readonly IReadOnlyList<string> HibernateTypes = new[] { "full", "reduced", "off", "on" };

    /// <summary>
    /// Boot settings that may be REMOVED (deleting restores Windows' default), with their BCD element type when PC Tidy can
    /// read the old value back for undo (null = removal is honestly not reversible). "nx" is deliberately not here:
    /// deleting it could lower DEP. x2apicpolicy and uselegacyapicmode have no element type PC Tidy can vouch for.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint?> BcdRemovable = new Dictionary<string, uint?>(StringComparer.OrdinalIgnoreCase)
    {
        ["useplatformclock"] = 0x260000A2, ["useplatformtick"] = 0x260000A4, ["disabledynamictick"] = 0x260000A5, ["tscsyncpolicy"] = 0x250000A6,
        ["testsigning"] = 0x16000049, ["nointegritychecks"] = 0x16000048, ["debug"] = 0x260000A0, ["bootdebug"] = 0x16000010,
        ["quietboot"] = 0x26000041, ["increaseuserva"] = 0x25000032, ["numproc"] = 0x25000061, ["onecpu"] = 0x26000060,
        ["removememory"] = 0x25000031, ["x2apicpolicy"] = null, ["uselegacyapicmode"] = null,
    };

    public static readonly IReadOnlyList<string> DockerTargets = new[] { "images", "builder", "containers" };
    public static readonly IReadOnlyList<string> DevTools = new[] { "npm", "pnpm", "yarn", "pip", "nuget-http", "gradle" };

    public static readonly IReadOnlyList<string> SupportedToolIds = new[]
    {
        ToolIds.DeliveryOptimizationClear, ToolIds.DismAnalyzeComponentStore, ToolIds.DismStartComponentCleanup, ToolIds.CleanmgrHandler,
        ToolIds.HibernateType, ToolIds.SfcScan, ToolIds.DismRestoreHealth, ToolIds.FlushDns, ToolIds.WinsockReset, ToolIds.DefenderQuickScan,
        ToolIds.WingetUpgrade, ToolIds.PnpExportDrivers, ToolIds.PnpDeleteDriver, ToolIds.PnpAddDrivers, ToolIds.PnpRemoveDevice,
        ToolIds.WindowsUpdateInstallDrivers, ToolIds.ShadowStorageResize, ToolIds.BcdDeleteValue, ToolIds.NetshAutotuningNormal,
        ToolIds.WslShutdown, ToolIds.WslSetSparse, ToolIds.DockerPrune, ToolIds.DevCacheClean, ToolIds.OneDriveShutdown, ToolIds.OneDriveStart,
        ToolIds.DriverRollback, ToolIds.RestorePointCreate, ToolIds.CipherWipeFreeSpace, ToolIds.CleanmgrStorageSense, CompactLzx,
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex PackageIdPattern();
    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex OemInfPattern();
    [GeneratedRegex(@"^[A-Za-z0-9\\&_.\-{}#]{1,400}$", RegexOptions.CultureInvariant)] private static partial Regex InstanceIdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex DistroPattern();
    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant)] private static partial Regex UpdateIdPattern();

    private static string System32 => Environment.SystemDirectory;
    private static string Sys(string exe) => Path.Combine(System32, exe);
    internal static string PowerShellExe => Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Windows' own Delivery Optimization module (System32, admin-only).</summary>
    internal static string DeliveryOptimizationModule => Path.Combine(System32, "WindowsPowerShell", "v1.0", "Modules", "DeliveryOptimization", "DeliveryOptimization.psd1");

    internal static string DeliveryOptimizationScript =>
        $"try {{ Import-Module -Name '{DeliveryOptimizationModule.Replace("'", "''")}' -ErrorAction Stop; " +
        "DeliveryOptimization\\Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0 } catch { exit 1 }";
    internal static string DismExe => Sys("Dism.exe");
    internal static string CleanmgrExe => Sys("cleanmgr.exe");
    internal static string PowercfgExe => Sys("powercfg.exe");

    public static bool TryResolve(PlanItem item, out SystemToolPlan plan, out string problem)
        => TryResolve(item, WindowsSystemToolBackend.Instance, out plan, out problem);

    public static bool TryResolve(PlanItem item, ISystemToolBackend env, out SystemToolPlan plan, out string problem)
    {
        plan = null!;
        problem = "";
        if (item.Kind != ActionKind.RunSystemTool) { problem = "This isn't a system tool step."; return false; }
        var toolId = item.ToolId ?? "";
        var args = item.Args ?? new Dictionary<string, string>();
        string A(string key) => args.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";
        SystemToolPlan Simple(string title, string exe, bool admin, params string[] arguments)
            => new() { ToolId = toolId, Title = title, Commands = new[] { new ToolCommand(exe, arguments) }, NeedsAdmin = admin };

        switch (toolId)
        {
            case ToolIds.DeliveryOptimizationClear:
                if (!OnlyKeys(args, out problem)) return false;
                // Exit code carries the result; the cmdlet's (localized) text is only logged. The module is imported
                // by its full System32 path and the cmdlet called by its module-qualified name, so a module of the same
                // name planted in a folder the user can change is never loaded (the helper's PSModulePath is System32 only too).
                plan = Simple("Clear the Delivery Optimization cache", PowerShellExe, true,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", DeliveryOptimizationScript);
                return true;

            case ToolIds.DismAnalyzeComponentStore:
                if (!OnlyKeys(args, out problem)) return false;
                plan = Simple("Measure the Windows component store", DismExe, true, "/Online", "/English", "/Cleanup-Image", "/AnalyzeComponentStore")
                    with { Reversible = true }; // read-only: nothing to undo
                return true;

            case ToolIds.DismStartComponentCleanup:
                if (!OnlyKeys(args, out problem)) return false;
                // Never /ResetBase: it makes installed updates impossible to uninstall.
                plan = Simple("Clean up old Windows components", DismExe, true, "/Online", "/English", "/Cleanup-Image", "/StartComponentCleanup")
                    with { Timeout = TimeSpan.FromHours(2), RestartCodes = new[] { 3010 } };
                return true;

            case ToolIds.CleanmgrHandler:
                if (!OnlyKeys(args, out problem, "handler")) return false;
                var handler = CleanmgrHandlers.FirstOrDefault(h => h.Equals(A("handler"), StringComparison.OrdinalIgnoreCase));
                if (handler is null) return Fail(out problem, "PC Tidy only runs the Previous Installations, Thumbnail Cache and Update Cleanup cleaners.");
                plan = Simple($"Run Windows' '{handler}' cleaner", CleanmgrExe, true, $"/sagerun:{CleanmgrSageSet}")
                    with { CleanmgrHandler = handler, Timeout = TimeSpan.FromHours(1) };
                return true;

            case ToolIds.HibernateType:
                if (!OnlyKeys(args, out problem, "type", "undoType")) return false;
                var type = HibernateType(A("type"));
                if (type is null) return Fail(out problem, "Hibernation can only be set to full, reduced, on or off.");
                // undoType: the state the feature saw at scan time, used to undo (validated like "type").
                var undoType = args.ContainsKey("undoType") ? HibernateType(A("undoType")) : null;
                if (args.ContainsKey("undoType") && undoType is null) return Fail(out problem, "The hibernation undo setting is not valid.");
                plan = new SystemToolPlan
                {
                    ToolId = toolId,
                    Title = type == "off" ? "Turn hibernation off" : type == "on" ? "Turn hibernation on" : $"Set the hibernation file to {type}",
                    Commands = HibernateCommands(type),
                    Reversible = true,
                    HibernateTarget = type,
                    HibernateUndoTarget = undoType,
                    Timeout = TimeSpan.FromMinutes(5),
                };
                return true;

            case ToolIds.SfcScan:
                if (!OnlyKeys(args, out problem)) return false;
                plan = Simple("Check and repair Windows system files", Sys("sfc.exe"), true, "/scannow") with { Timeout = TimeSpan.FromHours(2) };
                return true;

            case ToolIds.DismRestoreHealth:
                if (!OnlyKeys(args, out problem)) return false;
                plan = Simple("Repair the Windows component store", DismExe, true, "/Online", "/English", "/Cleanup-Image", "/RestoreHealth")
                    with { Timeout = TimeSpan.FromHours(2), RestartCodes = new[] { 3010 } };
                return true;

            case ToolIds.FlushDns:
                if (!OnlyKeys(args, out problem)) return false;
                plan = new SystemToolPlan { ToolId = toolId, Title = "Clear the DNS cache", Commands = Array.Empty<ToolCommand>(), Api = "DnsFlushResolverCache()", NeedsAdmin = false };
                return true;

            case ToolIds.WinsockReset:
                if (!OnlyKeys(args, out problem, "needsRestart")) return false;
                plan = Simple("Reset Winsock to Windows' defaults", Sys("netsh.exe"), true, "winsock", "reset");
                return true;

            case ToolIds.DefenderQuickScan:
                if (!OnlyKeys(args, out problem)) return false;
                var mp = DefenderMpCmdRun(env);
                plan = Simple("Run a Microsoft Defender quick scan", mp ?? Path.Combine(env.ProgramFiles, "Windows Defender", "MpCmdRun.exe"), false, "-Scan", "-ScanType", "1")
                    with
                    {
                        MissingProgram = mp is null ? "Microsoft Defender isn't on this PC (another antivirus may be in charge)." : null,
                        Timeout = TimeSpan.FromHours(1), SuccessCodes = new[] { 0, 2 },
                    };
                return true;

            case ToolIds.WingetUpgrade:
                if (!OnlyKeys(args, out problem, "id", "acceptAgreements")) return false;
                if (!PackageIdPattern().IsMatch(A("id"))) return Fail(out problem, "The app's winget id is not valid.");
                if (args.ContainsKey("acceptAgreements") && A("acceptAgreements") is not ("true" or "false")) return Fail(out problem, "The agreements setting is not valid.");
                var winget = Path.Combine(env.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
                var wargs = new List<string> { "upgrade", "--id", A("id"), "--exact", "--silent", "--disable-interactivity" };
                // Agreements are accepted only when the user ticked the box (the feature sets acceptAgreements=true then).
                if (A("acceptAgreements") == "true") wargs.AddRange(new[] { "--accept-source-agreements", "--accept-package-agreements" });
                plan = new SystemToolPlan
                {
                    ToolId = toolId, Title = $"Update {A("id")} with winget", Commands = new[] { new ToolCommand(winget, wargs) },
                    NeedsAdmin = false, PerUser = true, Timeout = TimeSpan.FromHours(1),
                    MissingProgram = env.FileExists(winget) ? null : "winget (App Installer) isn't installed on this PC.",
                };
                return true;

            case ToolIds.PnpExportDrivers:
            case ToolIds.PnpAddDrivers:
                if (!OnlyKeys(args, out problem, "folder")) return false;
                var folder = A("folder");
                if (!PathArgs.IsPlainLocal(folder)) return Fail(out problem, "The driver folder must be a folder on a drive of this PC.");
                plan = toolId == ToolIds.PnpExportDrivers
                    ? Simple("Back up all third-party drivers", Sys("pnputil.exe"), true, "/export-driver", "*", folder) with { Folder = folder, Reversible = true }
                    : Simple("Put drivers back from a backup", Sys("pnputil.exe"), true, "/add-driver", Path.Combine(folder, "*.inf"), "/subdirs", "/install")
                        with { Folder = folder, RestartCodes = new[] { 3010 }, SuccessCodes = new[] { 0, 259 } };
                return true;

            case ToolIds.PnpDeleteDriver:
                if (!OnlyKeys(args, out problem, "inf")) return false;
                if (!OemInfPattern().IsMatch(A("inf"))) return Fail(out problem, "PC Tidy only removes oemNN.inf driver packages.");
                // Never /force and never /uninstall: Windows refuses to delete a package a device still uses.
                plan = Simple("Remove an old driver package", Sys("pnputil.exe"), true, "/delete-driver", A("inf").ToLowerInvariant());
                return true;

            case ToolIds.PnpRemoveDevice:
                if (!OnlyKeys(args, out problem, "instanceId")) return false;
                if (!InstanceIdPattern().IsMatch(A("instanceId"))) return Fail(out problem, "The device id is not valid.");
                plan = Simple("Remove an old device entry", Sys("pnputil.exe"), true, "/remove-device", A("instanceId")) with { InstanceId = A("instanceId") };
                return true;

            case ToolIds.DriverRollback:
                if (!OnlyKeys(args, out problem, "instanceId")) return false;
                if (!InstanceIdPattern().IsMatch(A("instanceId"))) return Fail(out problem, "The device id is not valid.");
                plan = new SystemToolPlan { ToolId = toolId, Title = "Roll back a driver", Commands = Array.Empty<ToolCommand>(), Api = $"DiRollbackDriver({A("instanceId")})", InstanceId = A("instanceId") };
                return true;

            case ToolIds.WindowsUpdateInstallDrivers:
                if (!OnlyKeys(args, out problem, "updateIds")) return false;
                var ids = A("updateIds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ids.Length is 0 or > 100 || !ids.All(i => UpdateIdPattern().IsMatch(i))) return Fail(out problem, "The Windows Update ids are not valid.");
                plan = new SystemToolPlan
                {
                    ToolId = toolId, Title = "Install driver updates from Windows Update", Commands = Array.Empty<ToolCommand>(),
                    Api = $"Windows Update Agent: install {ids.Length} driver update(s)", UpdateIds = ids, Timeout = TimeSpan.FromHours(2),
                };
                return true;

            case ToolIds.ShadowStorageResize:
                if (!OnlyKeys(args, out problem, "percent", "drive")) return false;
                if (!int.TryParse(A("percent"), NumberStyles.None, CultureInfo.InvariantCulture, out var pct) || pct is < 5 or > 100)
                    return Fail(out problem, "Restore point space must be between 5% and 100%.");
                var vssDrive = args.ContainsKey("drive") ? PathArgs.DriveLetter(A("drive")) : PathArgs.DriveLetter(Path.GetPathRoot(System32));
                if (vssDrive is null) return Fail(out problem, "The drive is not valid.");
                // Permanent: shrinking the space can make Windows delete restore points, which can't be brought back.
                // The handler refuses any size below what restore points use now plus room for one more.
                plan = Simple($"Set restore point space on {vssDrive}: to {pct}%", Sys("vssadmin.exe"), true,
                    "Resize", "ShadowStorage", $"/For={vssDrive}:", $"/On={vssDrive}:", $"/MaxSize={pct}%")
                    with { Drive = vssDrive, Percent = pct, Reversible = false };
                return true;

            case ToolIds.BcdDeleteValue:
                if (!OnlyKeys(args, out problem, "name")) return false;
                var bcd = BcdRemovable.Keys.FirstOrDefault(k => k.Equals(A("name"), StringComparison.OrdinalIgnoreCase));
                if (bcd is null) return Fail(out problem, "PC Tidy only removes boot settings known to be left behind by old tweaks.");
                plan = Simple($"Remove the boot setting '{bcd}'", Sys("bcdedit.exe"), true, "/deletevalue", "{current}", bcd)
                    with { BcdName = bcd, BcdElementType = BcdRemovable[bcd], Reversible = BcdRemovable[bcd] is not null };
                return true;

            case ToolIds.NetshAutotuningNormal:
                if (!OnlyKeys(args, out problem)) return false;
                plan = Simple("Set network auto-tuning back to normal", Sys("netsh.exe"), true, "interface", "tcp", "set", "global", "autotuninglevel=normal")
                    with { Reversible = true };
                return true;

            case ToolIds.WslShutdown:
                if (!OnlyKeys(args, out problem)) return false;
                plan = Simple("Stop WSL", Sys("wsl.exe"), false, "--shutdown") with { PerUser = true, Timeout = TimeSpan.FromMinutes(5) };
                return true;

            case ToolIds.WslSetSparse:
                if (!OnlyKeys(args, out problem, "distro")) return false;
                if (!DistroPattern().IsMatch(A("distro"))) return Fail(out problem, "The Linux distribution name is not valid.");
                plan = Simple($"Let {A("distro")}'s disk give unused space back", Sys("wsl.exe"), false, "--manage", A("distro"), "--set-sparse", "true")
                    with { PerUser = true, Timeout = TimeSpan.FromMinutes(10) };
                return true;

            case ToolIds.DockerPrune:
                if (!OnlyKeys(args, out problem, "what")) return false;
                var what = DockerTargets.FirstOrDefault(t => t.Equals(A("what"), StringComparison.OrdinalIgnoreCase));
                if (what is null) return Fail(out problem, "PC Tidy only prunes Docker images, build cache and stopped containers (never volumes).");
                var dockerFolder = Path.Combine(env.ProgramFiles, "Docker", "Docker", "resources", "bin");
                var docker = FindProgram(env, new[] { dockerFolder }, "docker.exe");
                var noun = what switch { "images" => "image", "builder" => "builder", _ => "container" };
                plan = Simple($"Docker: prune {what}", docker ?? Path.Combine(dockerFolder, "docker.exe"), false, noun, "prune", "--force")
                    with { PerUser = true, MissingProgram = docker is null ? "Docker isn't installed on this PC." : null, Timeout = TimeSpan.FromHours(1) };
                return true;

            case ToolIds.DevCacheClean:
                if (!OnlyKeys(args, out problem, "tool")) return false;
                var tool = DevTools.FirstOrDefault(t => t.Equals(A("tool"), StringComparison.OrdinalIgnoreCase));
                if (tool is null) return Fail(out problem, "PC Tidy only cleans the npm, pnpm, Yarn, pip, NuGet and Gradle caches.");
                plan = DevToolPlan(toolId, tool, env);
                return true;

            case ToolIds.OneDriveShutdown:
            case ToolIds.OneDriveStart:
                if (!OnlyKeys(args, out problem)) return false;
                var oneDrive = FindProgram(env, new[] { Path.Combine(env.LocalAppData, "Microsoft", "OneDrive"), Path.Combine(env.ProgramFiles, "Microsoft OneDrive") }, "OneDrive.exe");
                var oneDriveExe = oneDrive ?? Path.Combine(env.LocalAppData, "Microsoft", "OneDrive", "OneDrive.exe");
                var start = new ToolCommand(oneDriveExe, new[] { "/background" });
                var missingOneDrive = oneDrive is null ? "OneDrive isn't installed on this PC." : null;
                plan = toolId == ToolIds.OneDriveShutdown
                    ? new SystemToolPlan
                    {
                        ToolId = toolId, Title = "Pause OneDrive (close it)", Commands = new[] { new ToolCommand(oneDriveExe, new[] { "/shutdown" }) },
                        NeedsAdmin = false, PerUser = true, Reversible = true, UndoCommands = new[] { start }, Timeout = TimeSpan.FromMinutes(2),
                        MissingProgram = missingOneDrive,
                    }
                    : new SystemToolPlan
                    {
                        ToolId = toolId, Title = "Start OneDrive again", Commands = new[] { start }, NeedsAdmin = false, PerUser = true, Launch = true,
                        MissingProgram = missingOneDrive,
                    };
                return true;

            case ToolIds.RestorePointCreate:
                if (!OnlyKeys(args, out problem, "description")) return false;
                var description = A("description");
                if (description.Length is 0 or > 256) return Fail(out problem, "The restore point needs a short description.");
                plan = new SystemToolPlan
                {
                    ToolId = toolId, Title = "Make a restore point", Commands = Array.Empty<ToolCommand>(),
                    Api = $"System Restore: create \"{description}\" (or reuse one from the last 24 hours)", RestorePointDescription = description,
                };
                return true;

            case ToolIds.CipherWipeFreeSpace:
                if (!OnlyKeys(args, out problem, "drive")) return false;
                if (PathArgs.DriveLetter(A("drive")) is not { } wipe) return Fail(out problem, "The drive is not valid.");
                plan = Simple($"Overwrite the free space on {wipe}:", Sys("cipher.exe"), false, $"/w:{wipe}:\\") with { Drive = wipe, Timeout = TimeSpan.FromHours(24) };
                return true;

            case ToolIds.CleanmgrStorageSense:
                if (!OnlyKeys(args, out problem)) return false;
                plan = new SystemToolPlan
                {
                    ToolId = toolId, Title = "Open Storage settings", Commands = Array.Empty<ToolCommand>(), Api = "open ms-settings:storagesense",
                    NeedsAdmin = false, PerUser = true, Reversible = true,
                };
                return true;

            case CompactLzx:
                if (!OnlyKeys(args, out problem, "folder")) return false;
                var target = A("folder");
                if (!PathArgs.IsPlainLocal(target)) return Fail(out problem, "The folder to compress must be a folder on a drive of this PC.");
                // Per-user, never from the admin helper: it compresses the user's own folders. Which folders are allowed
                // (no Windows, Program Files, cloud, game or anti-cheat folders) is checked by the handler, which knows them.
                plan = new SystemToolPlan
                {
                    ToolId = toolId, Title = $"Compress {target} with Windows' LZX compression", NeedsAdmin = false, PerUser = true, Reversible = true, Folder = target,
                    Commands = new[] { new ToolCommand(Sys("compact.exe"), new[] { "/c", $"/s:{target}", "/i", "/q", "/exe:lzx" }) },
                    UndoCommands = new[] { new ToolCommand(Sys("compact.exe"), new[] { "/u", $"/s:{target}", "/i", "/q", "/exe" }) },
                    Timeout = TimeSpan.FromHours(4),
                };
                return true;

            default:
                problem = "This tool isn't on PC Tidy's list of allowed Windows tools.";
                return false;
        }
    }

    private static bool Fail(out string problem, string why)
    {
        problem = why;
        return false;
    }

    private static string? HibernateType(string? value)
        => HibernateTypes.FirstOrDefault(h => h.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<ToolCommand> HibernateCommands(string type) => type switch
    {
        "off" => new[] { new ToolCommand(PowercfgExe, new[] { "/hibernate", "off" }) },
        "on" => new[] { new ToolCommand(PowercfgExe, new[] { "/hibernate", "on" }) },
        _ => new[]
        {
            new ToolCommand(PowercfgExe, new[] { "/hibernate", "on" }),
            new ToolCommand(PowercfgExe, new[] { "/hibernate", "/type", type }),
        },
    };

    /// <summary>bcdedit text for putting a removed boot setting back (bcdedit's input keywords, not its localized output).</summary>
    internal static string? BcdValueText(string name, BcdElementValue value)
    {
        if (value.IsBoolean) return value.Value != 0 ? "Yes" : "No";
        if (name.Equals("tscsyncpolicy", StringComparison.OrdinalIgnoreCase))
            return value.Value switch { 0 => "Default", 1 => "Legacy", 2 => "Enhanced", _ => null };
        return value.Value.ToString(CultureInfo.InvariantCulture);
    }

    internal static ToolCommand BcdSetCommand(string name, string value) => new(Sys("bcdedit.exe"), new[] { "/set", "{current}", name, value });

    internal static readonly IReadOnlyList<string> AutotuningLevels = new[] { "disabled", "highlyrestricted", "restricted", "normal", "experimental" };

    internal static ToolCommand AutotuningCommand(string level) => new(Sys("netsh.exe"), new[] { "interface", "tcp", "set", "global", $"autotuninglevel={level}" });

    /// <summary>MpCmdRun.exe from the newest Defender platform folder, else the one in Program Files.</summary>
    internal static string? DefenderMpCmdRun(ISystemToolBackend env)
    {
        var platform = Path.Combine(env.ProgramData, "Microsoft", "Windows Defender", "Platform");
        var newest = env.SubDirectories(platform)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
            .Where(x => x.Version is not null && env.FileExists(Path.Combine(x.Dir, "MpCmdRun.exe")))
            .OrderByDescending(x => x.Version).FirstOrDefault();
        if (newest.Dir is not null) return Path.Combine(newest.Dir, "MpCmdRun.exe");
        var classic = Path.Combine(env.ProgramFiles, "Windows Defender", "MpCmdRun.exe");
        return env.FileExists(classic) ? classic : null;
    }

    /// <summary>First existing file named <paramref name="exe"/> in the known folders, then on PATH.</summary>
    internal static string? FindProgram(ISystemToolBackend env, IEnumerable<string> knownFolders, string exe)
        => knownFolders.Concat(env.SearchPath()).Select(f => Path.Combine(f, exe)).FirstOrDefault(p => PathArgs.IsPlainLocal(p) && env.FileExists(p));

    /// <summary>
    /// The package manager's OWN cache command. npm / pnpm / Yarn installed as .cmd scripts are run through node.exe with
    /// their JavaScript entry point (no cmd.exe). Gradle has no program that runs without a shell (gradle.bat), so only a
    /// gradle.exe is asked to stop its daemons; old version caches are then quarantined (restorable) if not in use.
    /// </summary>
    internal static SystemToolPlan DevToolPlan(string toolId, string tool, ISystemToolBackend env)
    {
        var commands = new List<ToolCommand>();
        string? missing = null;
        string title;
        var nodeFolders = new[] { Path.Combine(env.ProgramFiles, "nodejs"), Path.Combine(env.RoamingAppData, "npm") };

        // npm.cmd / pnpm.cmd / yarn.cmd → node.exe <script> (the same thing the .cmd file does, without cmd.exe).
        bool NodeScript(string cmdName, string[] scriptPath, params string[] arguments)
        {
            var cmd = FindProgram(env, nodeFolders, cmdName);
            if (cmd is null) return false;
            var dir = Path.GetDirectoryName(cmd)!;
            var script = Path.Combine(new[] { dir }.Concat(scriptPath).ToArray());
            var node = env.FileExists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : FindProgram(env, nodeFolders, "node.exe");
            if (node is null || !env.FileExists(script)) return false;
            commands.Add(new ToolCommand(node, new[] { script }.Concat(arguments).ToList()));
            return true;
        }

        switch (tool)
        {
            case "npm":
                title = "Clean the npm cache (npm cache verify)";
                if (!NodeScript("npm.cmd", new[] { "node_modules", "npm", "bin", "npm-cli.js" }, "cache", "verify")) missing = "npm isn't installed on this PC.";
                break;
            case "pnpm":
                title = "Clean the pnpm store (pnpm store prune)";
                if (FindProgram(env, new[] { Path.Combine(env.LocalAppData, "pnpm") }, "pnpm.exe") is { } pnpm) commands.Add(new ToolCommand(pnpm, new[] { "store", "prune" }));
                else if (!NodeScript("pnpm.cmd", new[] { "node_modules", "pnpm", "bin", "pnpm.cjs" }, "store", "prune")) missing = "pnpm isn't installed on this PC.";
                break;
            case "yarn":
                title = "Clean the Yarn cache (yarn cache clean)";
                if (!NodeScript("yarn.cmd", new[] { "node_modules", "yarn", "bin", "yarn.js" }, "cache", "clean"))
                    missing = "Yarn isn't installed on this PC (or only in a form PC Tidy can't run without a command shell).";
                break;
            case "pip":
                title = "Clean the pip cache (pip cache purge)";
                if ((FindProgram(env, Array.Empty<string>(), "pip.exe") ?? FindProgram(env, Array.Empty<string>(), "pip3.exe")) is { } pip)
                    commands.Add(new ToolCommand(pip, new[] { "cache", "purge" }));
                else missing = "pip isn't installed on this PC.";
                break;
            case "nuget-http":
                title = "Clean NuGet's download caches";
                if (FindProgram(env, new[] { Path.Combine(env.ProgramFiles, "dotnet") }, "dotnet.exe") is { } dotnet)
                    foreach (var cache in new[] { "http-cache", "temp", "plugins-cache" }) commands.Add(new ToolCommand(dotnet, new[] { "nuget", "locals", cache, "--clear" }));
                else if (FindProgram(env, Array.Empty<string>(), "nuget.exe") is { } nuget)
                    foreach (var cache in new[] { "http-cache", "temp", "plugins-cache" }) commands.Add(new ToolCommand(nuget, new[] { "locals", cache, "-clear" }));
                else missing = "Neither dotnet nor nuget is installed on this PC.";
                break;
            default:
                title = "Stop Gradle and tidy caches of old Gradle versions";
                if (FindProgram(env, Array.Empty<string>(), "gradle.exe") is { } gradle) commands.Add(new ToolCommand(gradle, new[] { "--stop" }));
                var home = env.Variable("GRADLE_USER_HOME") is { } h && PathArgs.IsPlainLocal(h) ? h : Path.Combine(env.UserProfile, ".gradle");
                var caches = Path.Combine(home, "caches");
                return new SystemToolPlan
                {
                    ToolId = toolId, Title = title, Commands = commands, NeedsAdmin = false, PerUser = true, Timeout = TimeSpan.FromMinutes(10),
                    Api = $"quarantine caches of old, unused Gradle versions in {caches}", GradleCachesFolder = caches,
                };
        }
        return new SystemToolPlan
        {
            ToolId = toolId, Title = title, Commands = commands, NeedsAdmin = false, PerUser = true, MissingProgram = missing,
            Api = commands.Count == 0 ? title : null, Timeout = TimeSpan.FromHours(1),
        };
    }

    /// <summary>Rejects any argument the tool doesn't define: no free-form input reaches a command line.</summary>
    private static bool OnlyKeys(Dictionary<string, string> args, out string problem, params string[] allowed)
    {
        var bad = args.Keys.Any(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)) || args.Values.Any(v => v is null || v.Any(char.IsControl));
        problem = bad ? "This tool step has settings PC Tidy doesn't allow." : "";
        return !bad;
    }
}
