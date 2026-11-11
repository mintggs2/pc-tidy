namespace PcTidy.Features.Tweaks.Reading;

/// <summary>A service's start settings as stored in the registry (read-only).</summary>
public sealed record ServiceState(int Start, bool DelayedAutostart, int Type, string? ImagePath)
{
    public ServiceStart StartType => Start switch
    {
        0 => ServiceStart.Boot,
        1 => ServiceStart.System,
        2 => DelayedAutostart ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
        3 => ServiceStart.Manual,
        _ => ServiceStart.Disabled,
    };

    public bool IsDisabled => Start == 4;

    /// <summary>Reads <c>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;</c> through the (possibly redirected) reader. Null = not installed.</summary>
    public static ServiceState? Read(IRegistryReader registry, string serviceName)
    {
        var key = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
        if (registry.Read(RegistryHive.LocalMachine, key, "Start") is not { Kind: RegistryKind.DWord } start
            || !RegData.TryNumber(start.Data, out var s)) return null;
        var delayed = registry.Read(RegistryHive.LocalMachine, key, "DelayedAutostart") is { Kind: RegistryKind.DWord } d && RegData.TryNumber(d.Data, out var dv) && dv != 0;
        var type = registry.Read(RegistryHive.LocalMachine, key, "Type") is { Kind: RegistryKind.DWord } t && RegData.TryNumber(t.Data, out var tv) ? (int)tv : 0;
        var image = registry.Read(RegistryHive.LocalMachine, key, "ImagePath")?.Data;
        return new ServiceState((int)Math.Min(s, 4), delayed, type, image);
    }
}

/// <summary>
/// One value of the current Windows boot entry: a boolean or an integer element.
/// Read through the WMI BCD provider (or a fake in tests); bcdedit's localized text is never parsed.
/// </summary>
public sealed record BootElement(uint Type, bool? Boolean, ulong? Integer);

/// <summary>Snapshot of the current boot entry's elements.</summary>
public sealed class BootConfigSnapshot
{
    private readonly Dictionary<uint, BootElement> _elements;

    public BootConfigSnapshot(IEnumerable<BootElement> elements) => _elements = elements.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First());

    public static readonly BootConfigSnapshot Empty = new(Array.Empty<BootElement>());

    public BootElement? Get(uint type) => _elements.TryGetValue(type, out var e) ? e : null;
    public int Count => _elements.Count;
}

public interface IBootConfigReader
{
    /// <summary>Null when the boot settings can't be read (Windows only lets administrators read them).</summary>
    BootConfigSnapshot? ReadCurrentEntry();
}

/// <summary>Facts about this PC that some checks need (CPU family, domain, antivirus, TCP tuning…). All read-only.</summary>
public interface ISystemProbe
{
    CpuKind CpuKind { get; }
    /// <summary>Joined to a work domain (policies may be set on purpose by IT).</summary>
    bool IsDomainJoined { get; }
    /// <summary>"Professional", "Core" (Home), "Enterprise"…</summary>
    string? EditionId { get; }
    /// <summary>Antivirus products other than Microsoft Defender registered with Windows Security Center.</summary>
    IReadOnlyList<string> ThirdPartyAntivirus { get; }
    /// <summary>True when the hidden built-in Administrator account is enabled; null = couldn't read.</summary>
    bool? BuiltInAdministratorEnabled { get; }
    /// <summary>TCP receive autotuning (0 Disabled, 1 HighlyRestricted, 2 Restricted, 3 Normal, 4 Experimental); null = unknown.</summary>
    int? TcpAutotuningLevel { get; }
    /// <summary>Optional feature state (1 enabled, 2 disabled, 3 absent); null = unknown.</summary>
    int? OptionalFeatureState(string featureName);
    /// <summary>Core parking minimum cores (percent, plugged in) of the active power plan; null = unknown.</summary>
    int? CoreParkingMinCoresAc { get; }
    /// <summary>The built-in Administrator account's name on this PC (it differs by language); null = unknown.</summary>
    string? BuiltInAdministratorName { get; }
    /// <summary>Microsoft Defender's own status, read from its WMI provider; null = Defender's provider isn't there.</summary>
    DefenderProtection? Defender { get; }
    /// <summary>System Protection (restore points) for the Windows drive: true on, false off, null unknown.</summary>
    bool? SystemProtectionOn { get; }
    /// <summary>Restore points that exist now; null = couldn't be listed (it needs admin).</summary>
    int? RestorePointCount { get; }
    /// <summary>Windows' Memory Compression process is running; null = couldn't tell.</summary>
    bool? MemoryCompressionRunning { get; }
    /// <summary>Installed memory in bytes; null = unknown.</summary>
    long? TotalMemoryBytes { get; }
    /// <summary>Protocols unticked on physical network adapters; null = couldn't be read.</summary>
    IReadOnlyList<UnboundProtocol>? UnboundProtocols { get; }
    /// <summary>System-class devices disabled in Device Manager (CM_PROB_DISABLED); null = couldn't be read.</summary>
    IReadOnlyList<DisabledDevice>? DisabledSystemDevices { get; }
    /// <summary>The active power plan and the plans on this PC; null = couldn't be read.</summary>
    PowerPlanInfo? PowerPlans { get; }

    // ---- v3.1 ----
    /// <summary>Each TCP setting template and its congestion provider by name ("CUBIC", "BBR2"...); null = couldn't be read.</summary>
    IReadOnlyList<CongestionSetting>? CongestionProviders { get; }
    /// <summary>The active power plan's "processor idle disable" (plugged in): 1 = idle states off, 0 = on; null = unknown.</summary>
    int? ProcessorIdleDisableAc { get; }
    /// <summary>The same on battery; null = unknown.</summary>
    int? ProcessorIdleDisableDc { get; }
    /// <summary>The WebView2 runtime is registered (Microsoft's documented "pv" check); null = couldn't tell.</summary>
    bool? WebView2Installed { get; }
}

/// <summary>One TCP setting template (MSFT_NetTCPSetting) and its congestion provider's name; null name = not reported or unknown code.</summary>
public sealed record CongestionSetting(string SettingName, string? Provider);

/// <summary>Defender's state (MSFT_MpComputerStatus / MSFT_MpPreference, documented WMI classes). Null parts = not reported.</summary>
/// <param name="AntivirusEnabled">Defender's antivirus is the one protecting the PC (false when another antivirus took over).</param>
/// <param name="TamperProtected">IsTamperProtected.</param>
/// <param name="CloudReporting">MAPSReporting: 0 off, 1 basic, 2 advanced.</param>
public sealed record DefenderProtection(bool? AntivirusEnabled, bool? TamperProtected, int? CloudReporting)
{
    /// <summary>MSFT_MpComputerStatus.RealTimeProtectionEnabled.</summary>
    public bool? RealTimeProtection { get; init; }
    /// <summary>MSFT_MpComputerStatus.BehaviorMonitorEnabled.</summary>
    public bool? BehaviorMonitoring { get; init; }
    /// <summary>MSFT_MpComputerStatus.IoavProtectionEnabled ("scan all downloaded files and attachments").</summary>
    public bool? DownloadScanning { get; init; }
    /// <summary>MSFT_MpPreference.DisableScriptScanning.</summary>
    public bool? ScriptScanningDisabled { get; init; }
    /// <summary>MSFT_MpPreference.SubmitSamplesConsent: 0 always prompt, 1 safe samples, 2 never send, 3 all samples.</summary>
    public int? SampleSubmission { get; init; }
}

/// <summary>A device Device Manager shows as disabled (problem code 22). <paramref name="InstanceId"/> is the language-independent id.</summary>
public sealed record DisabledDevice(string Name, string InstanceId)
{
    /// <summary>The device's setup class (devguid.h); null = unknown. Shown only: the finder never turns devices on (System class stays protected).</summary>
    public Guid? ClassGuid { get; init; }
}

/// <summary>The power plans on this PC (power API, read-only). Names are only shown, never compared.</summary>
public sealed record PowerPlanInfo(Guid Active, string? ActiveName, IReadOnlyList<Guid> Installed);

/// <summary>A protocol unticked on one adapter. <paramref name="ComponentId"/> is the language-independent id (ms_tcpip6...).</summary>
public sealed record UnboundProtocol(string Adapter, string ComponentId)
{
    /// <summary>The protocols the finder checks, with plain names (Windows has all of them ticked by default).</summary>
    public static readonly IReadOnlyDictionary<string, string> Watched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ms_tcpip"] = "Internet Protocol Version 4 (IPv4)",
        ["ms_tcpip6"] = "Internet Protocol Version 6 (IPv6)",
        ["ms_msclient"] = "Client for Microsoft Networks",
    };

    public string Friendly => Watched.TryGetValue(ComponentId, out var n) ? n : ComponentId;
}

/// <summary>Installed Store apps for the current user.</summary>
public interface IAppxInventory
{
    /// <summary>Full name of the installed package with this name (e.g. "Microsoft.BingNews"), or null.</summary>
    string? FindPackageFullName(string packageName);
}

/// <summary>Scheduled task state (read-only).</summary>
public interface ITaskReader
{
    /// <summary>True/false = enabled/disabled; null = not on this PC or can't be read.</summary>
    bool? IsEnabled(string taskPath);
}

// ------------------------------------------------------------------ fixed (sandbox / test) implementations

/// <summary>A probe with fixed answers. The sandbox uses <see cref="Unknown"/>; tests set what they need.</summary>
public sealed record FixedSystemProbe : ISystemProbe
{
    public static readonly FixedSystemProbe Unknown = new();

    public CpuKind CpuKind { get; init; }
    public bool IsDomainJoined { get; init; }
    public string? EditionId { get; init; } = "Professional";
    public IReadOnlyList<string> ThirdPartyAntivirus { get; init; } = Array.Empty<string>();
    public bool? BuiltInAdministratorEnabled { get; init; }
    public int? TcpAutotuningLevel { get; init; }
    public int? CoreParkingMinCoresAc { get; init; }
    public string? BuiltInAdministratorName { get; init; }
    public DefenderProtection? Defender { get; init; }
    public bool? SystemProtectionOn { get; init; }
    public int? RestorePointCount { get; init; }
    public bool? MemoryCompressionRunning { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public IReadOnlyList<UnboundProtocol>? UnboundProtocols { get; init; }
    public IReadOnlyList<DisabledDevice>? DisabledSystemDevices { get; init; }
    public PowerPlanInfo? PowerPlans { get; init; }
    public IReadOnlyList<CongestionSetting>? CongestionProviders { get; init; }
    public int? ProcessorIdleDisableAc { get; init; }
    public int? ProcessorIdleDisableDc { get; init; }
    public bool? WebView2Installed { get; init; }
    public IReadOnlyDictionary<string, int> OptionalFeatures { get; init; } = new Dictionary<string, int>();
    public int? OptionalFeatureState(string featureName) => OptionalFeatures.TryGetValue(featureName, out var s) ? s : null;
}

public sealed class FixedBootConfigReader(BootConfigSnapshot? snapshot) : IBootConfigReader
{
    public BootConfigSnapshot? ReadCurrentEntry() => snapshot;
}

public sealed class FixedAppxInventory(IReadOnlyDictionary<string, string>? installed = null) : IAppxInventory
{
    private readonly IReadOnlyDictionary<string, string> _installed = installed ?? new Dictionary<string, string>();
    public string? FindPackageFullName(string packageName)
        => _installed.FirstOrDefault(kv => kv.Key.Equals(packageName, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class FixedTaskReader(IReadOnlyDictionary<string, bool>? tasks = null) : ITaskReader
{
    private readonly IReadOnlyDictionary<string, bool> _tasks = tasks ?? new Dictionary<string, bool>();
    public bool? IsEnabled(string taskPath)
        => _tasks.FirstOrDefault(kv => kv.Key.Equals(taskPath, StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit ? hit.Value : null;
}
