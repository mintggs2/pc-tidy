namespace PcTidy.Features.PcCare.Model;

// Raw, read-only facts about this PC that the PC care page is built from. The real source reads them with WMI, the
// registry, the event log, the WLAN API and two fixed read-only tool listings (winget, nvidia-smi); the sandbox source
// reads the same records from <sandbox>\Sim\pccare\snapshot.json. Every area is optional: null = "not available on this PC".

/// <summary>Whether something could be read.</summary>
public enum ReadAccess { Ok, NeedsAdmin, NotAvailable }

// ------------------------------------------------------------------ app updates

public sealed record WingetUpgrade(string Name, string Id, string InstalledVersion, string AvailableVersion, string Source);

public enum WingetState
{
    /// <summary>winget isn't on this PC (Windows 10 without App Installer).</summary>
    NotInstalled,
    /// <summary>The upgrade list was read.</summary>
    Ready,
    /// <summary>winget printed a table PC Tidy couldn't read reliably (another language or format): open winget instead.</summary>
    Unreadable,
    /// <summary>winget ran but failed (for example its terms were never accepted).</summary>
    Failed,
}

/// <param name="UnreadableRows">Rows that looked like updates but couldn't be read safely (a cut-off id, odd columns).</param>
public sealed record WingetFacts(WingetState State, IReadOnlyList<WingetUpgrade> Upgrades, int UnreadableRows = 0);

// ------------------------------------------------------------------ security

/// <summary>Microsoft Defender status from MSFT_MpComputerStatus (null fields = not reported).</summary>
public sealed record DefenderStatus
{
    public bool? ServiceEnabled { get; init; }
    public bool? AntivirusEnabled { get; init; }
    public bool? RealTimeProtectionEnabled { get; init; }
    public DateTime? SignaturesUpdatedUtc { get; init; }
    public DateTime? LastQuickScanUtc { get; init; }
    public bool? TamperProtected { get; init; }
    /// <summary>"Normal", "Passive" (another antivirus is in charge), "EDR Block Mode", "Not running"...</summary>
    public string? RunningMode { get; init; }
}

/// <summary>Group Policy values that switch Defender off, and the WinDefend service start type.</summary>
public sealed record DefenderPolicy(bool DisableAntiSpyware, bool DisableAntiVirus, bool DisableRealtimeMonitoring, bool ServiceDisabled)
{
    public static readonly DefenderPolicy None = new(false, false, false, false);
    public bool TurnsDefenderOff => DisableAntiSpyware || DisableAntiVirus || DisableRealtimeMonitoring || ServiceDisabled;
}

/// <summary>An antivirus registered with Windows Security Center (root\SecurityCenter2 AntiVirusProduct).</summary>
public sealed record AntivirusProduct(string Name, uint ProductState, string? ReportingExe);

/// <summary>A kernel driver service (name and image path), used to spot known vulnerable drivers.</summary>
public sealed record DriverService(string Name, string ImagePath, string? DisplayName, bool Running);

public sealed record SecurityFacts(
    DefenderStatus? Defender,
    DefenderPolicy Policy,
    IReadOnlyList<AntivirusProduct> Products,
    bool SecurityCenterAvailable,
    IReadOnlyList<DriverService> Drivers);

// ------------------------------------------------------------------ crashes

public enum CrashKind
{
    /// <summary>Blue screen (BugCheck 1001).</summary>
    BlueScreen,
    /// <summary>Restarted without shutting down cleanly (Kernel-Power 41).</summary>
    UnexpectedShutdown,
    /// <summary>Fatal hardware error (WHEA 18).</summary>
    HardwareError,
    /// <summary>Corrected hardware or memory error (WHEA 19, 47).</summary>
    HardwareCorrected,
    /// <summary>An app crashed (Application Error 1000).</summary>
    AppCrash,
    /// <summary>An app stopped responding and was closed (Application Hang 1002).</summary>
    AppHang,
}

public sealed record CrashEvent(CrashKind Kind, DateTime TimeUtc, uint? BugcheckCode = null, string? AppName = null, string? ModuleName = null, string? ExceptionCode = null);

/// <summary>A minidump file and what its header says.</summary>
public sealed record MinidumpInfo(string Path, DateTime WrittenUtc, long SizeBytes, uint? BugcheckCode, IReadOnlyList<ulong> Parameters);

public sealed record CrashFacts(
    IReadOnlyList<CrashEvent> Events,
    IReadOnlyList<MinidumpInfo> Minidumps,
    ReadAccess MinidumpAccess,
    bool EventLogAvailable,
    bool FromReliabilityMonitor);

// ------------------------------------------------------------------ browsers

/// <param name="Location">Chromium install location: 1 store, 2/3/6 added by another program, 4 unpacked, 7/9 policy.</param>
public sealed record BrowserExtension(string Id, string Name, bool FromStore, int Location, bool Enabled, IReadOnlyList<string> Permissions);

public sealed record BrowserProfile(
    string Browser,
    string ProfileName,
    string ProfileDir,
    string? Homepage,
    bool HomepageIsNewTab,
    int? RestoreOnStartup,
    IReadOnlyList<string> StartupUrls,
    string? SearchName,
    string? SearchUrl,
    IReadOnlyList<BrowserExtension> Extensions);

/// <summary>An extension a policy installs and keeps installed (ExtensionInstallForcelist). Scope "machine" or "user".</summary>
public sealed record ForcedExtension(string Browser, string ExtensionId, string? UpdateUrl, string Scope);

/// <summary>A browser policy that pins the home page, startup pages or search engine.</summary>
public sealed record BrowserPolicy(string Browser, string Name, string Value);

/// <summary>An installed program (Uninstall registry), for the unwanted-program check.</summary>
public sealed record InstalledProgram(string DisplayName, string? Publisher);

public sealed record BrowserFacts(
    IReadOnlyList<BrowserProfile> Profiles,
    IReadOnlyList<ForcedExtension> Forced,
    IReadOnlyList<BrowserPolicy> Policies,
    IReadOnlyList<InstalledProgram> Programs);

// ------------------------------------------------------------------ restore points

public sealed record RestorePoint(uint SequenceNumber, string Description, DateTime CreatedUtc, int RestorePointType);

/// <summary>System Protection storage on the system drive (Win32_ShadowStorage).</summary>
public sealed record ShadowStorage(string Volume, long UsedBytes, long MaxBytes, long VolumeBytes)
{
    public double MaxPercent => VolumeBytes > 0 ? 100.0 * MaxBytes / VolumeBytes : 0;
    public double UsedPercent => VolumeBytes > 0 ? 100.0 * UsedBytes / VolumeBytes : 0;
}

public sealed record RestoreFacts(ReadAccess Access, IReadOnlyList<RestorePoint> Points, ShadowStorage? Storage, bool? ProtectionOn);

// ------------------------------------------------------------------ store

public sealed record StoreFacts(bool StoreInstalled, string? StorePackageFamily);

// ------------------------------------------------------------------ Wi-Fi and user profiles

public sealed record WlanProfile(string Name, Guid InterfaceGuid, string InterfaceName, bool IsConnected, DateTime? LastConnectedUtc);

public sealed record WlanFacts(bool Available, IReadOnlyList<WlanProfile> Profiles);

/// <param name="AccountExists">The Windows account behind the SID still exists.</param>
/// <param name="IsThisPcAccount">The SID belongs to this PC's own accounts (not a work/school domain).</param>
public sealed record UserProfileFacts(string Sid, string LocalPath, bool Loaded, bool Special, DateTime? LastUseUtc, bool AccountExists, bool IsThisPcAccount);

public sealed record ProfileFacts(string? CurrentSid, IReadOnlyList<UserProfileFacts> Profiles);

// ------------------------------------------------------------------ optional features

public sealed record OptionalFeatureState(string Name, bool Enabled);

/// <param name="InstalledCapabilities">Catalog capability names found installed (by their marker file).</param>
/// <param name="UiLanguages">Installed display languages (MUI), e.g. "en-US".</param>
public sealed record FeatureFacts(IReadOnlyList<OptionalFeatureState> Features, IReadOnlyList<string> InstalledCapabilities, IReadOnlyList<string> UiLanguages);

// ------------------------------------------------------------------ network

/// <param name="MedianMs">Median answer time; null when the server never answered.</param>
public sealed record DnsServerResult(string Server, string Label, bool IsCurrent, double? MedianMs, int Answered, int Asked);

public sealed record NetworkFacts(IReadOnlyList<string> CurrentDnsServers, IReadOnlyList<DnsServerResult>? DnsTest, bool HasVpnAdapter);

// ------------------------------------------------------------------ network adapter power saving

/// <summary>What kind of link an adapter is.</summary>
public enum NetAdapterMedium { Other, Ethernet, WiFi }

/// <summary>One subkey of the network adapter class key (HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-…}\NNNN), read only.</summary>
/// <param name="Subkey">The subkey name ("0007").</param>
/// <param name="NetCfgInstanceId">The adapter GUID this subkey belongs to ("{…}").</param>
/// <param name="Characteristics">NCF_* flags: 0x1 virtual, 0x4 physical, 0x8 hidden.</param>
/// <param name="IfType">*IfType: 6 Ethernet, 71 Wi-Fi (IEEE 802.11).</param>
/// <param name="PhysicalMediaType">*PhysicalMediaType: 14 Ethernet (802.3), 9 Native 802.11.</param>
/// <param name="PnPCapabilities">The DWORD value; null = not there (Windows' default).</param>
/// <param name="PnPCapabilitiesOtherType">The value exists but isn't a DWORD (PC Tidy leaves it alone).</param>
public sealed record NetClassEntry(
    string Subkey,
    string? NetCfgInstanceId,
    string? DriverDesc,
    string? ComponentId,
    int? Characteristics,
    int? IfType,
    int? PhysicalMediaType,
    int? PnPCapabilities,
    bool PnPCapabilitiesOtherType = false);

/// <summary>A network adapter Windows lists (NetworkInterface; WMI Win32_NetworkAdapter.PhysicalAdapter when it could be read).</summary>
/// <param name="Id">The adapter GUID ("{…}").</param>
public sealed record NetAdapter(string Id, string Name, string Description, NetAdapterMedium Medium, bool? WmiPhysical = null);

public sealed record AdapterPowerFacts(IReadOnlyList<NetClassEntry> ClassEntries, IReadOnlyList<NetAdapter> Adapters);

// ------------------------------------------------------------------ health report

public sealed record DiskHealth(string Name, string Media, string Health, long SizeBytes);

public sealed record GpuReading(string Name, int TemperatureC, int? FanPercent);

public sealed record HealthFacts
{
    public string? OsName { get; init; }
    public string? DisplayVersion { get; init; }
    public int Build { get; init; }
    public DateTime? LastBootUtc { get; init; }
    public DateTime? UpdatesPausedUntilUtc { get; init; }
    public DateTime? LastUpdateInstallUtc { get; init; }
    public bool RebootPending { get; init; }
    public IReadOnlyList<DiskHealth> Disks { get; init; } = Array.Empty<DiskHealth>();
    public IReadOnlyList<GpuReading> Gpus { get; init; } = Array.Empty<GpuReading>();
    public int? StartupItems { get; init; }
    /// <summary>OneDrive folder backup: number of folders (Desktop, Documents, Pictures...) protected, null = not set up.</summary>
    public int? OneDriveBackedUpFolders { get; init; }
    /// <summary>File History has a configuration on this PC (best effort).</summary>
    public bool? FileHistoryConfigured { get; init; }
}

// ------------------------------------------------------------------ snapshot

public sealed record PcCareSnapshot
{
    public DateTime TakenUtc { get; init; } = DateTime.UtcNow;
    public bool IsAdmin { get; init; }
    public WingetFacts? Winget { get; init; }
    public SecurityFacts? Security { get; init; }
    public CrashFacts? Crashes { get; init; }
    public BrowserFacts? Browsers { get; init; }
    public RestoreFacts? Restore { get; init; }
    public StoreFacts? Store { get; init; }
    public WlanFacts? Wlan { get; init; }
    public ProfileFacts? Profiles { get; init; }
    public FeatureFacts? Features { get; init; }
    public NetworkFacts? Network { get; init; }
    public AdapterPowerFacts? AdapterPower { get; init; }
    public HealthFacts? Health { get; init; }
}
