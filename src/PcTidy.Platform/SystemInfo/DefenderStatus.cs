using System.Management;

namespace PcTidy.Platform;

/// <summary>Microsoft Defender's state (null fields = not reported, e.g. another antivirus is active).</summary>
public sealed record DefenderStatus(
    bool? AntivirusEnabled,
    bool? RealTimeProtectionEnabled,
    DateTime? SignaturesUpdatedUtc,
    DateTime? LastQuickScanUtc,
    DateTime? LastFullScanUtc);

/// <summary>Read-only Defender status from WMI <c>root\Microsoft\Windows\Defender:MSFT_MpComputerStatus</c>.</summary>
public static class DefenderStatusReader
{
    /// <summary>Null when Defender's WMI provider isn't there (other antivirus, Defender removed) or can't be read.</summary>
    public static Task<DefenderStatus?> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    private static DefenderStatus? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus"));
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    return new DefenderStatus(
                        mo["AntivirusEnabled"] as bool?,
                        mo["RealTimeProtectionEnabled"] as bool?,
                        Time(mo["AntivirusSignatureLastUpdated"]),
                        Time(mo["QuickScanEndTime"]),
                        Time(mo["FullScanEndTime"]));
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? Time(object? cim)
    {
        if (cim is not string s || s.Length == 0) return null;
        try
        {
            var t = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime();
            return t.Year < 1980 ? null : t; // "never" is reported as a 1601/1970 placeholder
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
