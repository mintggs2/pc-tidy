using System.ServiceProcess;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed;

/// <summary>Where shell classes are read from (HKCU\Software\Classes and HKLM\SOFTWARE\Classes on a real PC).</summary>
/// <param name="Scope">"you" or "all users", shown in the details panel.</param>
public sealed record ClassRoot(RegistryHive Hive, string Path, string Scope);

/// <summary>
/// Every read-only source the Speed scanners use. <see cref="ForRealPc"/> reads this PC through APIs, WMI and COM;
/// <see cref="ForSandbox"/> reads nothing real (a fake registry under HKCU\Software\PCTidyTest\speed and empty
/// lists). Tests build their own with fakes.
/// </summary>
public sealed class SpeedEnvironment
{
    public const string ShellExtensionsBlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    public required IRegistryView Registry { get; init; }
    public required IFileFactsProvider Files { get; init; }
    public required ITaskSource Tasks { get; init; }
    public required IProcessSource Processes { get; init; }
    public required ISessionClock Clock { get; init; }
    public required IPowerSource Power { get; init; }
    public required IGpuSchedulingSource Gpu { get; init; }
    public required IDiskSource Disks { get; init; }
    public required IStoreAppNames StoreApps { get; init; }
    public required ILockFinder Locks { get; init; }

    /// <summary>Is a service running right now? Null = unknown.</summary>
    public Func<string, bool?> ServiceRunning { get; init; } = _ => null;
    /// <summary>Resolves "@file.dll,-123" display strings; returns null when it can't.</summary>
    public Func<string?, string?> IndirectString { get; init; } = s => string.IsNullOrWhiteSpace(s) || s.StartsWith('@') ? null : s.Trim();
    /// <summary>The processor's brand string (the maker's own text, the same in every language); null = unknown. Sandbox: unknown.</summary>
    public Func<string?> CpuBrand { get; init; } = () => null;

    /// <summary>HKCU\Software\Classes then HKLM\SOFTWARE\Classes (what Explorer merges into HKCR).</summary>
    public static readonly IReadOnlyList<ClassRoot> DefaultClassRoots = new[]
    {
        new ClassRoot(RegistryHive.CurrentUser, @"Software\Classes", "you"),
        new ClassRoot(RegistryHive.LocalMachine, @"SOFTWARE\Classes", "all users"),
    };

    public static readonly IReadOnlyList<(RegistryHive Hive, string Path)> DefaultBlockedKeys = new[]
    {
        (RegistryHive.CurrentUser, ShellExtensionsBlockedKey),
        (RegistryHive.LocalMachine, ShellExtensionsBlockedKey),
    };

    /// <summary>Shell class roots for the right-click menu, in precedence order (per-user first).</summary>
    public IReadOnlyList<ClassRoot> ClassRoots { get; init; } = DefaultClassRoots;

    /// <summary>Where blocked shell extensions are listed.</summary>
    public IReadOnlyList<(RegistryHive Hive, string Path)> BlockedExtensionKeys { get; init; } = DefaultBlockedKeys;

    /// <summary>Folders Windows searches for bare program names after System32 (the PATH). Empty in the sandbox.</summary>
    public IReadOnlyList<string> SearchPath { get; init; } = Array.Empty<string>();

    /// <summary>How long "Running now" watches CPU use.</summary>
    public TimeSpan CpuSampleInterval { get; init; } = TimeSpan.FromSeconds(2);

    private static readonly Lazy<SpeedEnvironment> RealPc = new(CreateReal);

    /// <summary>The environment for a scan: the real PC, or a sandbox that reads nothing real.</summary>
    public static SpeedEnvironment For(ScanContext ctx) => ctx.Locations.IsSandbox ? ForSandbox() : RealPc.Value;

    public static SpeedEnvironment ForRealPc() => RealPc.Value;

    public static SpeedEnvironment ForSandbox() => new()
    {
        Registry = new RedirectedRegistryView(),
        Files = new WindowsFileFacts(),
        Tasks = new FixedTaskSource(),
        Processes = new FixedProcessSource(),
        Clock = new FixedSessionClock(DateTime.UtcNow, null),
        Power = new FixedPowerSource(),
        Gpu = new FixedGpuScheduling(null),
        Disks = new FixedDiskSource(),
        StoreApps = new NoStoreAppNames(),
        Locks = new RestartManagerLockFinder(),
        CpuSampleInterval = TimeSpan.FromMilliseconds(50),
    };

    private static SpeedEnvironment CreateReal() => new()
    {
        Registry = WindowsRegistryView.Instance,
        Files = new WindowsFileFacts(),
        Tasks = new ComTaskSource(),
        Processes = new WindowsProcessSource(),
        Clock = new WindowsSessionClock(),
        Power = new WindowsPowerSource(),
        Gpu = new D3dkmtGpuScheduling(),
        Disks = new WmiDiskSource(),
        StoreApps = new PackageStoreAppNames(),
        Locks = new RestartManagerLockFinder(),
        ServiceRunning = ServiceIsRunning,
        IndirectString = IndirectStrings.Resolve,
        CpuBrand = ReadCpuBrand,
        SearchPath = (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };

    /// <summary>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0 → ProcessorNameString (read-only).</summary>
    private static string? ReadCpuBrand()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool? ServiceIsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return null;
        }
    }
}
