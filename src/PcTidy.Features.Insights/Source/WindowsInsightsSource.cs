using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using Microsoft.Win32;
using PcTidy.Catalog.Insights;
using PcTidy.Core;

namespace PcTidy.Features.Insights.Source;

/// <summary>
/// The real PC, read-only: registry reads, WMI queries, the event log API and display APIs. Nothing here writes,
/// runs a program or parses localized text. Every read catches its own failure and reports "not available".
/// </summary>
public sealed partial class WindowsInsightsSource : IInsightsSource
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(20);
    private readonly IKnownLocations _locations;

    public WindowsInsightsSource(IKnownLocations locations) => _locations = locations;

    // ------------------------------------------------------------------ Windows identity

    public WindowsIdentity ReadWindowsIdentity()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cv = TryOpen(lm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var oem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var o = TryOpen(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation"))
            foreach (var name in Safe(() => o?.GetValueNames()) ?? Array.Empty<string>())
                if (Safe(() => o!.GetValue(name)?.ToString()) is { Length: > 0 } v) oem[name] = v;

        var missing = new List<string>();
        foreach (var (service, _) in ModifiedWindowsMarkers.CoreServices)
        {
            using var key = TryOpen(lm, @"SYSTEM\CurrentControlSet\Services\" + service);
            if (key is null) missing.Add(service);
        }

        return new WindowsIdentity
        {
            Build = int.TryParse(Str(cv, "CurrentBuildNumber"), out var b) ? b : Environment.OSVersion.Version.Build,
            Ubr = Safe(() => cv?.GetValue("UBR")) is int ubr ? ubr : 0,
            ProductName = Str(cv, "ProductName"),
            EditionId = Str(cv, "EditionID"),
            RegisteredOwner = Str(cv, "RegisteredOwner"),
            RegisteredOrganization = Str(cv, "RegisteredOrganization"),
            OemInformation = oem,
            PresentWindowsFolders = PresentFolders(_locations.WindowsDir, MarkerKind.WindowsFolder),
            PresentProgramFilesFolders = PresentFolders(_locations.ProgramFiles, MarkerKind.ProgramFilesFolder),
            MissingCoreServices = missing,
            InstalledApps = InstalledApps(lm),
        };
    }

    private static IReadOnlySet<string> PresentFolders(string root, MarkerKind kind)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ModifiedWindowsMarkers.Names.Where(m => m.Kind == kind))
            if (Safe(() => Directory.Exists(Path.Combine(root, m.Value)))) present.Add(m.Value);
        return present;
    }

    private static IReadOnlyList<string> InstalledApps(RegistryKey lm)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void From(RegistryKey? root, string path)
        {
            using var key = TryOpen(root, path);
            foreach (var sub in Safe(() => key?.GetSubKeyNames()) ?? Array.Empty<string>())
            {
                using var app = TryOpen(key, sub);
                if (Str(app, "DisplayName") is { Length: > 0 } n) names.Add(n);
            }
        }
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        From(lm, uninstall);
        From(lm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        using var cu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        From(cu, uninstall);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ reliability and memory tests

    public ReadOutcome<IReadOnlyList<StabilitySample>> ReadStability(DateTime sinceUtc)
    {
        try
        {
            var since = ManagementDateTimeConverter.ToDmtfDateTime(sinceUtc.ToLocalTime());
            var samples = new List<StabilitySample>();
            foreach (var o in Query(@"root\cimv2", $"SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '{since}'"))
                using (o)
                {
                    if (o["TimeGenerated"] is string t && o["SystemStabilityIndex"] is { } idx)
                        samples.Add(new StabilitySample(ManagementDateTimeConverter.ToDateTime(t).ToUniversalTime(), Convert.ToDouble(idx, System.Globalization.CultureInfo.InvariantCulture)));
                }
            return samples.Count == 0
                ? ReadOutcome<IReadOnlyList<StabilitySample>>.Missing("Windows hasn't recorded reliability data on this PC yet.")
                : ReadOutcome<IReadOnlyList<StabilitySample>>.Ok(samples.OrderBy(s => s.TimeUtc).ToList());
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return ReadOutcome<IReadOnlyList<StabilitySample>>.Admin();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return ReadOutcome<IReadOnlyList<StabilitySample>>.Missing();
        }
    }

    public ReadOutcome<IReadOnlyList<MemoryTestResult>> ReadMemoryTests()
    {
        // Event ids from the provider manifest (locale independent): 1101/1201 no errors, 1102/1202 errors, 1103 cancelled, 1104 incomplete.
        const string xpath = "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and " +
                             "(EventID=1101 or EventID=1102 or EventID=1103 or EventID=1104 or EventID=1201 or EventID=1202)]]";
        try
        {
            var results = new List<MemoryTestResult>();
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
            for (int i = 0; i < 10 && reader.ReadEvent() is { } e; i++)
                using (e)
                {
                    var outcome = e.Id switch
                    {
                        1101 or 1201 => MemoryTestOutcome.NoErrors,
                        1102 or 1202 => MemoryTestOutcome.ErrorsFound,
                        1103 => MemoryTestOutcome.Cancelled,
                        _ => MemoryTestOutcome.CouldNotComplete,
                    };
                    results.Add(new MemoryTestResult((e.TimeCreated ?? DateTime.Now).ToUniversalTime(), outcome, e.Id));
                }
            return ReadOutcome<IReadOnlyList<MemoryTestResult>>.Ok(results);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReadOutcome<IReadOnlyList<MemoryTestResult>>.Missing();
        }
    }

    // ------------------------------------------------------------------ backup

    public BackupFacts ReadBackup()
    {
        var config = Path.Combine(_locations.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration");
        bool configured = Safe(() => Directory.Exists(config) && Directory.EnumerateFiles(config, "Config*.xml").Any());
        DateTime? last = null;
        using (var cu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var fh = TryOpen(cu, @"Software\Microsoft\Windows\CurrentVersion\FileHistory"))
        {
            // Best effort (undocumented): ProtectedUpToTime is a FILETIME of the last finished backup.
            if (Safe(() => fh?.GetValue("ProtectedUpToTime")) is long ft && ft > 0)
                last = Safe(() => (DateTime?)DateTime.FromFileTimeUtc(ft));
        }
        return new BackupFacts { FileHistoryConfigured = configured, FileHistoryLastBackupUtc = last };
    }

    // ------------------------------------------------------------------ hardware

    public ReadOutcome<MemoryFacts> ReadMemory()
    {
        try
        {
            var modules = new List<MemoryModule>();
            foreach (var o in Query(@"root\cimv2", "SELECT Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel FROM Win32_PhysicalMemory"))
                using (o)
                    modules.Add(new MemoryModule(
                        Slot: (o["DeviceLocator"] as string ?? o["BankLabel"] as string ?? "").Trim(),
                        CapacityBytes: ToLong(o["Capacity"]),
                        RatedMts: (int)ToLong(o["Speed"]),
                        ConfiguredMts: (int)ToLong(o["ConfiguredClockSpeed"])));
            int slots = 0;
            foreach (var o in Query(@"root\cimv2", "SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray"))
                using (o)
                    if (ToLong(o["Use"]) is 3 or 0) slots += (int)ToLong(o["MemoryDevices"]);
            return modules.Count == 0 ? ReadOutcome<MemoryFacts>.Missing() : ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(modules, slots));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return ReadOutcome<MemoryFacts>.Missing();
        }
    }

    public IReadOnlyDictionary<string, string> ReadFileSystems()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _locations.FixedDrives)
            if (Safe(() => new DriveInfo(root) is { IsReady: true } d ? d.DriveFormat : null) is { } fs) map[root] = fs;
        return map;
    }

    public TimerFacts ReadTimer() => new(Stopwatch.Frequency, Stopwatch.IsHighResolution);

    public SecurityFacts ReadSecurity()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        TriState secureBoot;
        using (var sb = TryOpen(lm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            secureBoot = sb is null ? TriState.No : Safe(() => sb.GetValue("UEFISecureBootEnabled")) is int v ? (v == 1 ? TriState.Yes : TriState.No) : TriState.Unknown;

        // Win32_Tpm needs an administrator; a standard user gets "access denied", which is reported as such.
        TriState tpm = TriState.Unknown;
        string? version = null;
        bool tpmNeedsAdmin = false;
        try
        {
            var found = false;
            foreach (var o in Query(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm"))
                using (o)
                {
                    found = true;
                    version = (o["SpecVersion"] as string)?.Split(',')[0].Trim();
                }
            tpm = found ? TriState.Yes : TriState.No;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied) { tpmNeedsAdmin = true; }
        catch (UnauthorizedAccessException) { tpmNeedsAdmin = true; }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or TimeoutException) { }

        var (bitLocker, bitLockerNeedsAdmin) = ReadBitLocker();
        int? smartAppControl;
        using (var ci = TryOpen(lm, @"SYSTEM\CurrentControlSet\Control\CI\Policy"))
            smartAppControl = Safe(() => ci?.GetValue("VerifiedAndReputablePolicyState")) is int sac ? sac : null;

        return new SecurityFacts
        {
            SecureBoot = secureBoot, TpmPresent = tpm, TpmVersion = version, TpmNeedsAdmin = tpmNeedsAdmin,
            MemoryIntegrity = ReadMemoryIntegrity(lm),
            SmartAppControl = smartAppControl,
            BitLocker = bitLocker, BitLockerNeedsAdmin = bitLockerNeedsAdmin,
        };
    }

    /// <summary>
    /// BitLocker on the Windows drive: Win32_EncryptableVolume.ProtectionStatus (documented: 0 off, 1 on, 2 unknown).
    /// The provider answers administrators only and needs an encrypted (packet privacy) connection.
    /// </summary>
    private (TriState State, bool NeedsAdmin) ReadBitLocker()
    {
        var letter = _locations.SystemDrive.Length >= 2 ? _locations.SystemDrive[..2] : "C:";
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
                new ConnectionOptions { Authentication = AuthenticationLevel.PacketPrivacy, Timeout = WmiTimeout });
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'"),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });
            using var rows = searcher.Get();
            foreach (var o in rows)
                using (o)
                    return (ToLong(o["ProtectionStatus"]) switch { 1 => TriState.Yes, 0 => TriState.No, _ => TriState.Unknown }, false);
            return (TriState.Unknown, false);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.AccessDenied or ManagementStatus.InvalidNamespace)
        {
            // Home editions have no BitLocker provider (device encryption only); either way only an admin can tell.
            return (TriState.Unknown, ex.ErrorCode == ManagementStatus.AccessDenied);
        }
        catch (UnauthorizedAccessException) { return (TriState.Unknown, true); }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or TimeoutException) { return (TriState.Unknown, false); }
    }

    // ------------------------------------------------------------------ runtimes

    public RuntimeFacts ReadRuntimes()
    {
        var system32 = Path.Combine(_locations.WindowsDir, "System32");
        var wow64 = Path.Combine(_locations.WindowsDir, "SysWOW64");
        TriState All(string folder, params string[] files)
            => Safe(() => (bool?)files.All(f => File.Exists(Path.Combine(folder, f)))) switch { true => TriState.Yes, false => TriState.No, _ => TriState.Unknown };

        var desktop = Path.Combine(_locations.ProgramFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        var versions = Safe(() => Directory.Exists(desktop)
            ? Directory.EnumerateDirectories(desktop).Select(Path.GetFileName)
                .Select(n => int.TryParse(n?.Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : 0)
                .Where(m => m > 0).Distinct().OrderBy(m => m).ToList()
            : new List<int>());

        return new RuntimeFacts
        {
            VisualCpp64 = All(system32, "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"),
            VisualCpp32 = Directory.Exists(wow64) ? All(wow64, "vcruntime140.dll", "msvcp140.dll") : TriState.Unknown,
            DirectX9Extras = All(system32, "d3dx9_43.dll"),
            DotNetDesktopVersions = versions ?? new List<int>(),
            DotNetKnown = versions is not null,
            // winget is an app execution alias in the user's WindowsApps folder (the same check PC care uses).
            WingetPresent = Safe(() => File.Exists(Path.Combine(_locations.LocalAppData, "Microsoft", "WindowsApps", "winget.exe"))),
        };
    }

    // ------------------------------------------------------------------ processor and power plan

    public string? ReadCpuName()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cpu = TryOpen(lm, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = Str(cpu, "ProcessorNameString").Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The active power scheme GUID. Verify: read from HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes
    /// "ActivePowerScheme" (widely used, not in Microsoft's docs); read-only, and only used for an information line.
    /// </summary>
    public string? ReadActivePowerPlan()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var schemes = TryOpen(lm, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
        var guid = Str(schemes, "ActivePowerScheme").Trim();
        return Guid.TryParse(guid, out var g) ? g.ToString("D") : null;
    }

    private static TriState ReadMemoryIntegrity(RegistryKey lm)
    {
        try
        {
            foreach (var o in Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard"))
                using (o)
                    if (o["SecurityServicesRunning"] is uint[] running) return running.Contains(2u) ? TriState.Yes : TriState.No;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        using var key = TryOpen(lm, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return Safe(() => key?.GetValue("Enabled")) is int e ? (e == 1 ? TriState.Yes : TriState.No) : TriState.Unknown;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<ManagementBaseObject> Query(string scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });
        using var results = searcher.Get();
        foreach (var o in results) yield return o;
    }

    private static RegistryKey? TryOpen(RegistryKey? root, string path)
    {
        try { return root?.OpenSubKey(path, writable: false); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string Str(RegistryKey? key, string name) => Safe(() => key?.GetValue(name)?.ToString()) ?? "";

    private static long ToLong(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static T? Safe<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or ObjectDisposedException) { return default; }
    }
}
