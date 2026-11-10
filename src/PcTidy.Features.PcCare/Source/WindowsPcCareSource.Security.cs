using System.Diagnostics.Eventing.Reader;
using System.Management;
using Microsoft.Win32;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Source;

/// <summary>Security (Defender, Security Center, kernel drivers) and crash history readers. All read-only.</summary>
public sealed partial class WindowsPcCareSource
{
    private const string DefenderPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    private static SecurityFacts ReadSecurity()
    {
        DefenderStatus? defender = null;
        try
        {
            defender = Wmi(@"root\Microsoft\Windows\Defender",
                "SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime, IsTamperProtected, AMRunningMode FROM MSFT_MpComputerStatus",
                o => new DefenderStatus
                {
                    ServiceEnabled = Prop(o, "AMServiceEnabled") as bool?,
                    AntivirusEnabled = Prop(o, "AntivirusEnabled") as bool?,
                    RealTimeProtectionEnabled = Prop(o, "RealTimeProtectionEnabled") as bool?,
                    SignaturesUpdatedUtc = WmiDate(Prop(o, "AntivirusSignatureLastUpdated")),
                    LastQuickScanUtc = WmiDate(Prop(o, "QuickScanEndTime")),
                    TamperProtected = Prop(o, "IsTamperProtected") as bool?,
                    RunningMode = Str(o, "AMRunningMode"),
                })
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Defender's WMI provider is gone when its service is disabled: the policy check below explains why.
        }

        var policy = new DefenderPolicy(
            RegInt(RegistryHive.LocalMachine, DefenderPolicyKey, "DisableAntiSpyware") == 1,
            RegInt(RegistryHive.LocalMachine, DefenderPolicyKey, "DisableAntiVirus") == 1,
            RegInt(RegistryHive.LocalMachine, DefenderPolicyKey + @"\Real-Time Protection", "DisableRealtimeMonitoring") == 1,
            RegInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start") == 4);

        var products = new List<AntivirusProduct>();
        bool center = true;
        try
        {
            products.AddRange(Wmi(@"root\SecurityCenter2", "SELECT displayName, productState, pathToSignedReportingExe FROM AntiVirusProduct",
                o => new AntivirusProduct(Str(o, "displayName") ?? "Antivirus",
                    Prop(o, "productState") switch { uint u => u, int i => unchecked((uint)i), _ => 0u },
                    Str(o, "pathToSignedReportingExe"))));
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            center = false; // Windows Server and some editions have no Security Center
        }

        return new SecurityFacts(defender, policy, products, center, ReadKernelDrivers());
    }

    /// <summary>Kernel driver services (name, image path). WMI first, the Services registry key as a fallback.</summary>
    private static IReadOnlyList<DriverService> ReadKernelDrivers()
    {
        try
        {
            return Wmi(@"root\cimv2", "SELECT Name, DisplayName, PathName, State FROM Win32_SystemDriver",
                    o => new DriverService(Str(o, "Name") ?? "", Str(o, "PathName") ?? "", Str(o, "DisplayName"),
                        string.Equals(Str(o, "State"), "Running", StringComparison.OrdinalIgnoreCase)))
                .Where(d => d.Name.Length > 0 && d.ImagePath.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            var list = new List<DriverService>();
            using var services = OpenRead(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = services!.OpenSubKey(name, writable: false);
                if (k?.GetValue("Type") is int type && (type & 0x3) != 0 && k.GetValue("ImagePath") is string image)
                    list.Add(new DriverService(name, image, k.GetValue("DisplayName") as string, Running: false));
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ crashes

    private CrashFacts ReadCrashes(DateTime nowUtc, int days, CancellationToken ct)
    {
        var events = new List<CrashEvent>();
        bool logOk = false;
        long ms = (long)TimeSpan.FromDays(Math.Max(1, days)).TotalMilliseconds;
        foreach (var (log, provider, ids) in CrashEventMapper.Queries)
        {
            ct.ThrowIfCancellationRequested();
            var idFilter = string.Join(" or ", ids.Select(i => $"EventID={i}"));
            var xpath = $"*[System[Provider[@Name='{provider}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
                logOk = true;
                for (int n = 0; n < 500; n++)
                {
                    using var record = reader.ReadEvent();
                    if (record is null) break;
                    var raw = new RawEvent(record.ProviderName ?? provider, record.Id,
                        (record.TimeCreated ?? nowUtc.ToLocalTime()).ToUniversalTime(),
                        record.Properties.Select(p => p.Value).ToList());
                    if (CrashEventMapper.Map(raw) is { } e) events.Add(e);
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                // That log or provider isn't readable here; the others still count.
            }
        }

        bool fromReliability = false;
        if (!logOk)
        {
            events.AddRange(ReadReliability(nowUtc.AddDays(-Math.Max(1, days))));
            fromReliability = events.Count > 0;
        }

        var (dumps, access) = ReadMinidumps();
        return new CrashFacts(CrashEventMapper.Dedupe(events), dumps, access, logOk || fromReliability, fromReliability);
    }

    /// <summary>Reliability Monitor's records (Win32_ReliabilityRecords): same providers and ids, app name in ProductName.</summary>
    private static IEnumerable<CrashEvent> ReadReliability(DateTime sinceUtc)
    {
        List<RawEvent?> rows;
        try
        {
            var since = ManagementDateTimeConverter.ToDmtfDateTime(sinceUtc.ToLocalTime());
            rows = Wmi(@"root\cimv2", $"SELECT SourceName, EventIdentifier, ProductName, TimeGenerated FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{since}'",
                o =>
                {
                    int id = Prop(o, "EventIdentifier") switch { uint u => (int)u, int i => i, _ => -1 };
                    var when = WmiDate(Prop(o, "TimeGenerated"));
                    return id < 0 || when is null ? null : new RawEvent(Str(o, "SourceName") ?? "", id, when.Value, new object?[] { Str(o, "ProductName") });
                }, 60);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var raw in rows)
            if (raw is not null && CrashEventMapper.Map(raw) is { } e) yield return e;
    }

    /// <summary>Headers of the newest minidumps in Windows\Minidump (the folder needs admin to list).</summary>
    private (IReadOnlyList<MinidumpInfo>, ReadAccess) ReadMinidumps()
    {
        var folder = Path.Combine(_locations.WindowsDir, "Minidump");
        List<FileInfo> files;
        try
        {
            if (!Directory.Exists(folder)) return (Array.Empty<MinidumpInfo>(), ReadAccess.Ok);
            files = new DirectoryInfo(folder).EnumerateFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).Take(20).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (Array.Empty<MinidumpInfo>(), ReadAccess.NeedsAdmin);
        }
        catch (IOException)
        {
            return (Array.Empty<MinidumpInfo>(), ReadAccess.NotAvailable);
        }

        var list = new List<MinidumpInfo>();
        bool denied = false;
        foreach (var f in files)
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var buffer = new byte[MinidumpHeader.HeaderBytes];
                int read;
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                var header = MinidumpHeader.TryParse(buffer.AsSpan(0, read));
                list.Add(new MinidumpInfo(f.FullName, f.LastWriteTimeUtc, f.Length, header?.Code, header?.Parameters ?? Array.Empty<ulong>()));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { denied = true; }
            catch (IOException) { /* vanished or locked: skip this one */ }
        }
        return (list, denied && list.Count == 0 ? ReadAccess.NeedsAdmin : ReadAccess.Ok);
    }
}
