namespace PcTidy.Features.Tweaks.Reading;

/// <summary>
/// Everything the tweak scanners read, bundled so tests (and the simulation sandbox) can swap in fakes.
/// Real PC: registry, WMI, power API, Task Scheduler, PackageManager. Sandbox: registry redirected into
/// HKCU\Software\PCTidyTest, fixed probes, and the sandbox's own hosts file and AppData.
/// </summary>
public sealed class TweakEnvironment
{
    /// <summary>Registry root the simulation sandbox reads and writes (empty unless a test put values there).</summary>
    public const string SandboxRegistryRoot = @"Software\PCTidyTest\tweaks\sandbox";

    public required IRegistryReader Registry { get; init; }
    public required ISystemProbe Probe { get; init; }
    public required IBootConfigReader Boot { get; init; }
    public required IAppxInventory Apps { get; init; }
    public required ITaskReader Tasks { get; init; }
    /// <summary>Full path of the hosts file to read.</summary>
    public required string HostsPath { get; init; }
    public required string LocalAppData { get; init; }
    public required string WindowsDir { get; init; }
    public int WindowsBuild { get; init; } = Environment.OSVersion.Version.Build;
    public bool IsLaptop { get; init; }
    public GpuVendor Gpu { get; init; } = GpuVendor.Unknown;
    /// <summary>True on the real PC (some notes, like "run as admin for boot settings", only make sense there).</summary>
    public bool IsRealPc { get; init; }
    /// <summary>
    /// Folders the finder looks through (two levels deep, names only, never file contents) for in-Windows BIOS tools left
    /// behind: Desktop, Downloads, Documents and the Windows drive's root. Empty in the sandbox and in tests unless set.
    /// </summary>
    public IReadOnlyList<string> LeftoverFolders { get; init; } = Array.Empty<string>();

    /// <summary>The folders <see cref="LeftoverFolders"/> uses on the real PC (missing ones are skipped).</summary>
    public static IReadOnlyList<string> LeftoverFoldersIn(IKnownLocations l)
        => new[] { l.Desktop, l.Downloads, l.Documents, l.SystemDrive }
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Real readers on the real PC; fixed readers + redirected registry in a sandbox.</summary>
    public static TweakEnvironment ForContext(ScanContext ctx)
        => ctx.Locations.IsSandbox ? Sandbox(ctx.Locations, ctx.System) : Real(ctx.Locations, ctx.System);

    public static TweakEnvironment Real(IKnownLocations locations, ISystemInfo? system)
    {
        var registry = new RegistryReader(RegistryRedirect.None);
        return new TweakEnvironment
        {
            Registry = registry,
            Probe = new WindowsSystemProbe(registry),
            Boot = new WmiBootConfigReader(),
            Apps = new WindowsAppxInventory(),
            Tasks = new WindowsTaskReader(),
            HostsPath = HostsFileIn(locations.WindowsDir),
            LocalAppData = locations.LocalAppData,
            WindowsDir = locations.WindowsDir,
            WindowsBuild = system?.WindowsBuild ?? Environment.OSVersion.Version.Build,
            IsLaptop = system?.IsLaptop ?? false,
            Gpu = system?.PrimaryGpu ?? GpuVendor.Unknown,
            IsRealPc = true,
            LeftoverFolders = LeftoverFoldersIn(locations),
        };
    }

    public static TweakEnvironment Sandbox(IKnownLocations locations, ISystemInfo? system, string registryRoot = SandboxRegistryRoot)
        => new()
        {
            Registry = new RegistryReader(RegistryRedirect.ToTestRoot(registryRoot)),
            Probe = FixedSystemProbe.Unknown,
            Boot = new FixedBootConfigReader(BootConfigSnapshot.Empty),
            Apps = new FixedAppxInventory(),
            Tasks = new FixedTaskReader(),
            HostsPath = HostsFileIn(locations.WindowsDir),
            LocalAppData = locations.LocalAppData,
            WindowsDir = locations.WindowsDir,
            WindowsBuild = system?.WindowsBuild ?? 26100,
            IsLaptop = system?.IsLaptop ?? false,
            Gpu = system?.PrimaryGpu ?? GpuVendor.Unknown,
        };

    public static string HostsFileIn(string windowsDir) => Path.Combine(windowsDir, "System32", "drivers", "etc", "hosts");
}
