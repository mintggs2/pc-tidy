namespace PcTidy.Features.Insights.Source;

// =====================================================================================
//  Everything the Insights cards read from Windows, behind one interface so tests use fakes.
//  The real source (WindowsInsightsSource) only uses WMI, the registry, the event log API
//  and display APIs: never a command line, never localized text. Every member is read-only
//  and must never throw: "couldn't read" is a value (null / Unavailable), not an exception.
// =====================================================================================

/// <summary>Read-only facts for the Insights cards.</summary>
public interface IInsightsSource
{
    WindowsIdentity ReadWindowsIdentity();
    /// <summary>Hourly stability index samples (Reliability Monitor), oldest first. Null = not available (no data or WMI blocked).</summary>
    ReadOutcome<IReadOnlyList<StabilitySample>> ReadStability(DateTime sinceUtc);
    /// <summary>Windows Memory Diagnostic results from the System log, newest first.</summary>
    ReadOutcome<IReadOnlyList<MemoryTestResult>> ReadMemoryTests();
    BackupFacts ReadBackup();
    IReadOnlyList<DisplayFacts> ReadDisplays();
    ReadOutcome<MemoryFacts> ReadMemory();
    /// <summary>File system per fixed drive root ("C:\" → "NTFS").</summary>
    IReadOnlyDictionary<string, string> ReadFileSystems();
    TimerFacts ReadTimer();
    SecurityFacts ReadSecurity();
    /// <summary>Which shared runtimes games and apps ask for are present (file and folder checks only).</summary>
    RuntimeFacts ReadRuntimes();
}

/// <summary>Why a read gave nothing (shown as "Not available on this PC" or "Needs admin").</summary>
public enum ReadProblem { None, NotAvailable, NeedsAdmin }

public sealed record ReadOutcome<T>(T? Value, ReadProblem Problem = ReadProblem.None, string? Detail = null) where T : class
{
    public static ReadOutcome<T> Ok(T value) => new(value);
    public static ReadOutcome<T> Missing(string? detail = null) => new(null, ReadProblem.NotAvailable, detail);
    public static ReadOutcome<T> Admin() => new(null, ReadProblem.NeedsAdmin);
}

/// <summary>Who built this Windows: OEM text, registered names, marker folders and which core services exist.</summary>
public sealed record WindowsIdentity
{
    public int Build { get; init; }
    /// <summary>Update build revision (the number after the dot, e.g. 2338 in 22621.2338). 0 when unknown.</summary>
    public int Ubr { get; init; }
    public string ProductName { get; init; } = "";
    public string EditionId { get; init; } = "";
    public IReadOnlyDictionary<string, string> OemInformation { get; init; } = new Dictionary<string, string>();
    public string RegisteredOwner { get; init; } = "";
    public string RegisteredOrganization { get; init; } = "";
    /// <summary>Folder names that exist directly under %WINDIR% / Program Files, from the list the card asked about.</summary>
    public IReadOnlySet<string> PresentWindowsFolders { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> PresentProgramFilesFolders { get; init; } = new HashSet<string>();
    /// <summary>Core services whose registry key is missing (removed from the image, not just disabled).</summary>
    public IReadOnlyList<string> MissingCoreServices { get; init; } = Array.Empty<string>();
    /// <summary>Display names from the Uninstall registry (used by the backup card).</summary>
    public IReadOnlyList<string> InstalledApps { get; init; } = Array.Empty<string>();
}

public sealed record StabilitySample(DateTime TimeUtc, double Index);

public enum MemoryTestOutcome { NoErrors, ErrorsFound, Cancelled, CouldNotComplete }

public sealed record MemoryTestResult(DateTime TimeUtc, MemoryTestOutcome Outcome, int EventId);

public sealed record BackupFacts
{
    /// <summary>File History has a configuration for this user.</summary>
    public bool FileHistoryConfigured { get; init; }
    /// <summary>Best effort: when File History last finished (null = unknown).</summary>
    public DateTime? FileHistoryLastBackupUtc { get; init; }
}

public sealed record DisplayFacts(string Name, int CurrentHz, int MaxHzAtCurrentResolution, int Width, int Height);

public sealed record MemoryModule(string Slot, long CapacityBytes, int RatedMts, int ConfiguredMts);

public sealed record MemoryFacts(IReadOnlyList<MemoryModule> Modules, int TotalSlots);

public sealed record TimerFacts(long Frequency, bool IsHighResolution);

public enum TriState { Unknown, Yes, No }

public sealed record SecurityFacts
{
    public TriState SecureBoot { get; init; }
    /// <summary>TPM spec version ("2.0"); null when not read.</summary>
    public string? TpmVersion { get; init; }
    public TriState TpmPresent { get; init; }
    /// <summary>The TPM can only be read by an administrator.</summary>
    public bool TpmNeedsAdmin { get; init; }
    /// <summary>Memory integrity (HVCI) running.</summary>
    public TriState MemoryIntegrity { get; init; }
    /// <summary>Smart App Control (CI\Policy VerifiedAndReputablePolicyState): 0 off, 1 on, 2 evaluation; null = not on this PC or unreadable.</summary>
    public int? SmartAppControl { get; init; }
    /// <summary>BitLocker on the Windows drive (Win32_EncryptableVolume.ProtectionStatus 1 = on).</summary>
    public TriState BitLocker { get; init; }
    /// <summary>BitLocker status can only be read by an administrator.</summary>
    public bool BitLockerNeedsAdmin { get; init; }
}

/// <summary>Shared runtimes, found by their files (the same on every language).</summary>
public sealed record RuntimeFacts
{
    /// <summary>Visual C++ 2015-2022 (v14) runtime, 64-bit: vcruntime140.dll, vcruntime140_1.dll and msvcp140.dll in System32.</summary>
    public TriState VisualCpp64 { get; init; }
    /// <summary>Visual C++ v14 runtime, 32-bit: vcruntime140.dll and msvcp140.dll in SysWOW64.</summary>
    public TriState VisualCpp32 { get; init; }
    /// <summary>The DirectX 9 add-on files older games need (d3dx9_43.dll from the DirectX End-User Runtime).</summary>
    public TriState DirectX9Extras { get; init; }
    /// <summary>.NET Desktop Runtime major versions installed (folders under Program Files\dotnet\shared\Microsoft.WindowsDesktop.App).</summary>
    public IReadOnlyList<int> DotNetDesktopVersions { get; init; } = Array.Empty<int>();
}
