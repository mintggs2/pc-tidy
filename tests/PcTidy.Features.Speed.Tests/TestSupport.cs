using Microsoft.Win32;
using PcTidy.Features.Speed;
using RegistryHive = PcTidy.Core.Model.RegistryHive;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;
using PcTidy.Hosting;

namespace PcTidy.Features.Speed.Tests;

/// <summary>A fresh sandbox PC under testbed\speed\&lt;id&gt; with the real SafetyGuard, Executor and Undo; removed afterwards.</summary>
public sealed class SpeedSandbox : IDisposable
{
    public static readonly string TestbedRoot = FindTestbed();

    /// <param name="generate">Fill the sandbox with the standard generated testbed (TestbedGen) first.</param>
    public SpeedSandbox(SandboxProfile? profile = null, string? simJson = null, bool generate = false)
    {
        Root = Path.Combine(TestbedRoot, Guid.NewGuid().ToString("N")[..10]);
        _generated = generate;
        if (generate) PcTidy.TestbedGen.TestbedBuilder.Generate(Root, PcTidy.TestbedGen.TestbedSize.Small, profile ?? SandboxProfile.Minimal);
        Directory.CreateDirectory(Root);
        if (simJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Sim"));
            File.WriteAllText(Path.Combine(Root, "Sim", "sim.json"), simJson);
        }
        Host = PcTidyHost.CreateSandbox(Root, profile ?? SandboxProfile.Minimal);
        Locations = (SandboxKnownLocations)Host.Locations;
    }

    private readonly bool _generated;

    public string Root { get; }
    public PcTidyHost Host { get; }
    public SandboxKnownLocations Locations { get; }

    /// <summary>A scan context with the real guard, optional preset, roots and options.</summary>
    public ScanContext Context(string? preset = null, IReadOnlyList<string>? roots = null, bool hideMicrosoft = true, ISystemInfo? system = null)
    {
        var b = Host.CreateScanContext(ScanDepth.Quick, roots, preset);
        return new ScanContext
        {
            Locations = b.Locations, Safety = b.Safety, InUse = b.InUse, Settings = b.Settings, Walker = b.Walker,
            Depth = b.Depth, Preset = preset, Roots = roots, RunningProcesses = b.RunningProcesses, System = system ?? b.System,
            Options = new Dictionary<string, object> { [SpeedOptions.HideMicrosoft] = hideMicrosoft },
        };
    }

    /// <summary>Creates a file (and its folder) inside the sandbox.</summary>
    public string MakeFile(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Host.Dispose();
        try
        {
            // A generated testbed has read-only files, junctions and odd names on purpose: its own cleaner removes it.
            if (_generated) PcTidy.TestbedGen.TestbedBuilder.Delete(Root);
            else Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FindTestbed()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d is null) throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
        var root = Path.Combine(d.FullName, "testbed", "speed");
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>
/// A throw-away registry tree under HKCU\Software\PCTidyTest\speed\&lt;id&gt; (the only place tests may write),
/// deleted afterwards. <see cref="View"/> reads it as if it were the PC's HKCU/HKLM.
/// </summary>
public sealed class FakeRegistry : IDisposable
{
    public FakeRegistry()
    {
        Root = $@"{RedirectedRegistryView.DefaultRoot}\{Guid.NewGuid():N}";
        View = new RedirectedRegistryView(Root);
    }

    /// <summary>Path under HKCU, e.g. Software\PCTidyTest\speed\abc.</summary>
    public string Root { get; }
    public RedirectedRegistryView View { get; }

    /// <summary>Writes a value into the fake HKCU or HKLM.</summary>
    public void Set(RegistryHive hive, string key, string? name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
        => SetRaw(View.Map(hive, key), name, value, kind);

    /// <summary>Writes a value at a raw path under HKCU (must be inside the test root).</summary>
    public static void SetRaw(string hkcuPath, string? name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
    {
        Assert.StartsWith(RedirectedRegistryView.DefaultRoot + "\\", hkcuPath, StringComparison.OrdinalIgnoreCase);
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(hkcuPath, writable: true);
        if (kind == RegistryValueKind.Unknown) key.SetValue(name ?? "", value);
        else key.SetValue(name ?? "", value, kind);
    }

    public static void CreateRaw(string hkcuPath)
    {
        Assert.StartsWith(RedirectedRegistryView.DefaultRoot + "\\", hkcuPath, StringComparison.OrdinalIgnoreCase);
        using var _ = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(hkcuPath, writable: true);
    }

    public void Dispose()
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        RemoveEmptyTestRoot();
    }

    /// <summary>Removes HKCU\Software\PCTidyTest\speed once no test is using it (Windows refuses while it has subkeys).</summary>
    public static void RemoveEmptyTestRoot()
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(RedirectedRegistryView.DefaultRoot, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Builds environments out of fakes: nothing real is read except the fake registry tree.</summary>
public static class Env
{
    public static SpeedEnvironment Fake(
        IRegistryView? registry = null,
        IFileFactsProvider? files = null,
        ITaskSource? tasks = null,
        IProcessSource? processes = null,
        ISessionClock? clock = null,
        IPowerSource? power = null,
        IGpuSchedulingSource? gpu = null,
        IDiskSource? disks = null,
        ILockFinder? locks = null,
        IReadOnlyList<ClassRoot>? classRoots = null,
        IReadOnlyList<(RegistryHive, string)>? blocked = null)
    {
        var env = new SpeedEnvironment
        {
            Registry = registry ?? new RedirectedRegistryView(RedirectedRegistryView.DefaultRoot + @"\does-not-exist"),
            Files = files ?? new FixedFileFacts(),
            Tasks = tasks ?? new FixedTaskSource(),
            Processes = processes ?? new FixedProcessSource(),
            Clock = clock ?? new FixedSessionClock(DateTime.UtcNow, null),
            Power = power ?? new FixedPowerSource(),
            Gpu = gpu ?? new FixedGpuScheduling(null),
            Disks = disks ?? new FixedDiskSource(),
            StoreApps = new NoStoreAppNames(),
            Locks = locks ?? new FixedLockFinder(),
            CpuSampleInterval = TimeSpan.FromMilliseconds(20),
            ClassRoots = classRoots ?? SpeedEnvironment.DefaultClassRoots,
            BlockedExtensionKeys = blocked ?? SpeedEnvironment.DefaultBlockedKeys,
        };
        return env;
    }
}

/// <summary>An ISystemInfo with chosen drives and facts (for drive and battery rules).</summary>
public sealed class TestSystemInfo : ISystemInfo
{
    public GpuVendor PrimaryGpu { get; init; } = GpuVendor.Unknown;
    public IReadOnlyList<string> GpuNames { get; init; } = Array.Empty<string>();
    public bool IsLaptop { get; init; }
    public bool HasBattery { get; init; }
    public bool IsAdmin { get; init; }
    public int WindowsBuild { get; init; } = 26100;
    public bool RebootPending { get; init; }
    public IReadOnlyList<DriveDetails> Drives { get; init; } = Array.Empty<DriveDetails>();
    public IReadOnlySet<string> RunningProcesses() => new HashSet<string>();
}
