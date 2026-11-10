using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PcTidy.Features.Tweaks.Reading;

/// <summary>
/// Reads the current boot entry through WMI's BCD provider (<c>root\WMI</c> BcdStore → BcdObject.EnumerateElements).
/// Needs admin; without it Windows refuses and this returns null (the finder then says boot settings weren't checked).
/// </summary>
public sealed class WmiBootConfigReader : IBootConfigReader
{
    public BootConfigSnapshot? ReadCurrentEntry()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions { EnablePrivileges = true, Timeout = TimeSpan.FromSeconds(10) });
            scope.Connect();
            using var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=''"), null);
            using var openIn = store.GetMethodParameters("OpenObject");
            openIn["Id"] = BadTweakCatalog.CurrentBootEntry;
            using var opened = store.InvokeMethod("OpenObject", openIn, null);
            if (opened?["ReturnValue"] is not true || opened["Object"] is not ManagementBaseObject entryInfo) return null;
            using var openedEntry = entryInfo; // released with the rest (X-1: no WMI object waits for the finalizer)

            using var entry = new ManagementObject(scope,
                new ManagementPath($"BcdObject.Id='{BadTweakCatalog.CurrentBootEntry}',StoreFilePath=''"), null);
            using var listed = entry.InvokeMethod("EnumerateElements", null, null);
            if (listed?["ReturnValue"] is not true || listed["Elements"] is not ManagementBaseObject[] elements) return null;

            var values = new List<BootElement>();
            foreach (var e in elements)
                using (e)
                {
                    if (e["Type"] is not uint type) continue;
                    bool? boolean = HasProperty(e, "Boolean") && e["Boolean"] is bool b ? b : null;
                    ulong? integer = HasProperty(e, "Integer") && ulong.TryParse(Convert.ToString(e["Integer"], CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;
                    values.Add(new BootElement(type, boolean, integer));
                }
            return new BootConfigSnapshot(values);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasProperty(ManagementBaseObject o, string name)
        => o.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Real PC facts via WMI, the registry and the power API. Every read is cached and failure-tolerant.</summary>
public sealed partial class WindowsSystemProbe : ISystemProbe
{
    private readonly IRegistryReader _registry;
    private readonly Lazy<CpuKind> _cpu;
    private readonly Lazy<bool> _domain;
    private readonly Lazy<IReadOnlyList<string>> _av;
    private readonly Lazy<(bool? Enabled, string? Name)> _admin;
    private readonly Lazy<int?> _autotuning;
    private readonly Lazy<int?> _parking;
    private readonly Lazy<DefenderProtection?> _defender;
    private readonly Lazy<int?> _restorePoints;
    private readonly Lazy<bool?> _memoryCompression;
    private readonly Lazy<long?> _memory;
    private readonly Lazy<IReadOnlyList<UnboundProtocol>?> _unbound;
    private readonly Dictionary<string, int?> _features = new(StringComparer.OrdinalIgnoreCase);

    public WindowsSystemProbe(IRegistryReader registry)
    {
        _registry = registry;
        _cpu = new(() => CpuKinds.Classify(Query("root\\CIMV2", "SELECT Name FROM Win32_Processor", o => o["Name"] as string).FirstOrDefault()));
        _domain = new(() => Query("root\\CIMV2", "SELECT PartOfDomain FROM Win32_ComputerSystem", o => o["PartOfDomain"] as bool?).FirstOrDefault() ?? false);
        _av = new(() => Query("root\\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct", o => o["displayName"] as string)
            .OfType<string>().Where(n => !n.Contains("Defender", StringComparison.OrdinalIgnoreCase)).Distinct().ToList());
        _admin = new(() =>
        {
            // The built-in Administrator always has the RID 500; its name differs by language.
            var hit = Query("root\\CIMV2", "SELECT SID, Name, Disabled FROM Win32_UserAccount WHERE LocalAccount=TRUE",
                    o => (Sid: o["SID"] as string, Name: o["Name"] as string, Disabled: o["Disabled"] as bool?))
                .FirstOrDefault(a => a.Sid is { } sid && sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.EndsWith("-500", StringComparison.Ordinal));
            return (hit.Disabled is bool disabled ? !disabled : null, hit.Name);
        });
        _autotuning = new(() =>
        {
            var rows = Query("root\\StandardCimv2", "SELECT SettingName, AutoTuningLevelLocal FROM MSFT_NetTCPSetting",
                o => (Name: o["SettingName"] as string, Level: o["AutoTuningLevelLocal"] is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null));
            var internet = rows.FirstOrDefault(r => "Internet".Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            return internet.Level ?? rows.FirstOrDefault(r => r.Level is not null).Level;
        });
        _parking = new(ReadCoreParking);
        _defender = new(ReadDefender);
        // root\default SystemRestore lists restore points; Windows only answers administrators (unknown otherwise).
        _restorePoints = new(() => QueryOrNull("root\\default", "SELECT SequenceNumber FROM SystemRestore", o => o["SequenceNumber"])?.Count);
        _memoryCompression = new(ReadMemoryCompression);
        _memory = new(ReadTotalMemory);
        _unbound = new(ReadUnboundProtocols);
        _devices = new(DeviceReader.DisabledSystemDevices);
        _plans = new(PowerPlanReader.Read);
    }

    private readonly Lazy<IReadOnlyList<DisabledDevice>?> _devices;
    private readonly Lazy<PowerPlanInfo?> _plans;

    public IReadOnlyList<DisabledDevice>? DisabledSystemDevices => _devices.Value;
    public PowerPlanInfo? PowerPlans => _plans.Value;

    public CpuKind CpuKind => _cpu.Value;
    public bool IsDomainJoined => _domain.Value;
    public string? EditionId => _registry.Read(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID")?.Data;
    public IReadOnlyList<string> ThirdPartyAntivirus => _av.Value;
    public bool? BuiltInAdministratorEnabled => _admin.Value.Enabled;
    public string? BuiltInAdministratorName => _admin.Value.Name;
    public int? TcpAutotuningLevel => _autotuning.Value;
    public int? CoreParkingMinCoresAc => _parking.Value;
    public DefenderProtection? Defender => _defender.Value;
    public int? RestorePointCount => _restorePoints.Value;
    public bool? MemoryCompressionRunning => _memoryCompression.Value;
    public long? TotalMemoryBytes => _memory.Value;
    public IReadOnlyList<UnboundProtocol>? UnboundProtocols => _unbound.Value;

    /// <summary>
    /// System Protection for the Windows drive. The same read PC care uses: RPSessionInterval is 0 when protection is off
    /// for every drive. A policy that blocks System Restore is reported by its own finder signals.
    /// </summary>
    public bool? SystemProtectionOn
        => _registry.Read(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval") is { Kind: RegistryKind.DWord } v
           && RegData.TryNumber(v.Data, out var n) ? n != 0 : null;

    public int? OptionalFeatureState(string featureName)
    {
        lock (_features)
        {
            if (_features.TryGetValue(featureName, out var cached)) return cached;
            if (!FeatureNamePattern().IsMatch(featureName)) return null;
            var state = Query("root\\CIMV2", $"SELECT InstallState FROM Win32_OptionalFeature WHERE Name='{featureName}'",
                o => o["InstallState"] is { } s ? Convert.ToInt32(s, CultureInfo.InvariantCulture) : (int?)null).FirstOrDefault();
            _features[featureName] = state;
            return state;
        }
    }

    /// <summary>Kept for callers and tests; the rules live in <see cref="CpuKinds"/> so every section agrees.</summary>
    internal static CpuKind ClassifyCpu(string? name) => CpuKinds.Classify(name);

    [GeneratedRegex(@"^[A-Za-z0-9\-_.]{1,128}$")] private static partial Regex FeatureNamePattern();

    // ---- Defender (documented WMI classes MSFT_MpComputerStatus and MSFT_MpPreference) ----

    private static DefenderProtection? ReadDefender()
    {
        const string ns = @"root\Microsoft\Windows\Defender";
        // SELECT * so an older Defender that lacks one property (IsTamperProtected) still answers; Prop() returns null for it.
        var status = QueryOrNull(ns, "SELECT * FROM MSFT_MpComputerStatus",
            o => (Av: Prop(o, "AntivirusEnabled") as bool?, Tamper: Prop(o, "IsTamperProtected") as bool?,
                  RealTime: Prop(o, "RealTimeProtectionEnabled") as bool?, Behavior: Prop(o, "BehaviorMonitorEnabled") as bool?,
                  Ioav: Prop(o, "IoavProtectionEnabled") as bool?));
        if (status is not { Count: > 0 }) return null; // Defender's provider is gone (removed, or another antivirus replaced it)
        var prefs = QueryOrNull(ns, "SELECT * FROM MSFT_MpPreference",
            o => (Maps: Number(Prop(o, "MAPSReporting")), Samples: Number(Prop(o, "SubmitSamplesConsent")), NoScripts: Prop(o, "DisableScriptScanning") as bool?));
        var p = prefs is { Count: > 0 } ? prefs[0] : default;
        var s = status[0];
        return new DefenderProtection(s.Av, s.Tamper, p.Maps)
        {
            RealTimeProtection = s.RealTime, BehaviorMonitoring = s.Behavior, DownloadScanning = s.Ioav,
            ScriptScanningDisabled = p.NoScripts, SampleSubmission = p.Samples,
        };
    }

    private static int? Number(object? value)
    {
        try { return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static object? Prop(ManagementBaseObject o, string name)
    {
        try { return o[name]; }
        catch (ManagementException) { return null; } // older Defender versions lack a property
    }

    // ---- memory ----

    /// <summary>
    /// Windows runs memory compression in a small system process named "Memory Compression" (visible to standard users).
    /// No such process means compression is off. A best guess: Windows offers no documented way to read the setting.
    /// </summary>
    private static bool? ReadMemoryCompression()
    {
        try
        {
            var found = Process.GetProcessesByName("Memory Compression");
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static long? ReadTotalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try { return GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0 ? (long)Math.Min(status.TotalPhys, long.MaxValue) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // ---- network adapter bindings (documented MSFT_NetAdapter / MSFT_NetAdapterBindingSettingData) ----

    private static IReadOnlyList<UnboundProtocol>? ReadUnboundProtocols()
    {
        const string ns = @"root\StandardCimv2";
        // Physical adapters only: VPN, virtual-switch and Bluetooth adapters often have protocols unticked by design.
        var physical = QueryOrNull(ns, "SELECT Name, HardwareInterface, Virtual FROM MSFT_NetAdapter",
            o => (Name: o["Name"] as string, Hardware: o["HardwareInterface"] as bool?, Virtual: o["Virtual"] as bool?));
        if (physical is null) return null;
        var names = physical.Where(a => a.Hardware == true && a.Virtual != true && a.Name is { Length: > 0 })
            .Select(a => a.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindings = QueryOrNull(ns, "SELECT Name, ComponentID, Enabled FROM MSFT_NetAdapterBindingSettingData",
            o => (Name: o["Name"] as string, Component: o["ComponentID"] as string, Enabled: o["Enabled"] as bool?));
        if (bindings is null) return null;
        return bindings
            .Where(b => b.Enabled == false && b.Name is not null && names.Contains(b.Name) && b.Component is not null && UnboundProtocol.Watched.ContainsKey(b.Component))
            .Select(b => new UnboundProtocol(b.Name!, b.Component!))
            .Distinct().ToList();
    }

    // ---- WMI ----

    /// <summary>Runs a read-only query and maps each row inside <c>using</c>, so no WMI object outlives the call. Failures = empty.</summary>
    private static IReadOnlyList<T> Query<T>(string ns, string wql, Func<ManagementBaseObject, T> map)
        => QueryOrNull(ns, wql, map) ?? (IReadOnlyList<T>)Array.Empty<T>();

    /// <summary>Like <see cref="Query{T}"/>, but null when the namespace or class can't be read (so "unknown" differs from "none").</summary>
    private static List<T>? QueryOrNull<T>(string ns, string wql, Func<ManagementBaseObject, T> map)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\" + ns, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true });
            using var rows = searcher.Get();
            var results = new List<T>();
            foreach (var o in rows)
                using (o)
                    results.Add(map(o));
            return results;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            // Missing namespace (e.g. SecurityCenter2 on Server) or no access: unknown.
            return null;
        }
    }

    // ---- power API (read only) ----

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid CoreParkingMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    private static int? ReadCoreParking()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0 || scheme == IntPtr.Zero) return null;
            try
            {
                var scheme1 = Marshal.PtrToStructure<Guid>(scheme);
                var sub = ProcessorSubgroup;
                var setting = CoreParkingMinCores;
                return PowerReadACValueIndex(IntPtr.Zero, ref scheme1, ref sub, ref setting, out var value) == 0 ? (int)value : null;
            }
            finally { LocalFree(scheme); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>Installed Store apps for the current user (Windows' PackageManager, read-only).</summary>
public sealed class WindowsAppxInventory : IAppxInventory
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _packages = new(Load);

    public string? FindPackageFullName(string packageName) => _packages.Value.TryGetValue(packageName, out var full) ? full : null;

    private static IReadOnlyDictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var p in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage) continue;
                    map.TryAdd(p.Id.Name, p.Id.FullName);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException)
                {
                    // One broken package doesn't hide the rest.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or TypeLoadException or InvalidCastException)
        {
            // Store APIs unavailable (e.g. Server Core): no apps listed.
        }
        return map;
    }
}

/// <summary>Scheduled task state through the Task Scheduler COM API (read-only: Connect, GetFolder, GetTask, Enabled).</summary>
public sealed class WindowsTaskReader : ITaskReader
{
    private readonly Lazy<dynamic?> _service = new(Connect);

    public bool? IsEnabled(string taskPath)
    {
        if (_service.Value is not { } svc) return null;
        var cut = taskPath.LastIndexOf('\\');
        if (cut < 0) return null;
        var folderPath = cut == 0 ? "\\" : taskPath[..cut];
        var name = taskPath[(cut + 1)..];
        try
        {
            dynamic folder = svc.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            return (bool)task.Enabled;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return null;
        }
    }

    private static dynamic? Connect()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            return svc;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }
}
