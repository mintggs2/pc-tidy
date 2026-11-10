using System.Management;

namespace PcTidy.Features.Speed.Sources;

/// <summary>
/// Physical disks from Windows' Storage WMI provider (<c>root\Microsoft\Windows\Storage</c>), read-only:
/// MSFT_PhysicalDisk for health and type, MSFT_Partition for drive letters, and MSFT_StorageReliabilityCounter
/// for temperature, wear and errors (that one needs admin; without it the counters are simply missing).
/// </summary>
public sealed class WmiDiskSource : IDiskSource
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

    public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks(bool withReliability, CancellationToken ct)
    {
        try
        {
            var scope = new ManagementScope(Scope);
            scope.Connect();
            var letters = Letters(scope);
            ct.ThrowIfCancellationRequested();
            var reliability = withReliability ? Reliability(scope) : new Dictionary<string, ReliabilityInfo>();
            ct.ThrowIfCancellationRequested();

            var disks = new List<PhysicalDiskInfo>();
            foreach (var o in Query(scope, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk"))
            {
                using (o)
                {
                    var id = o["DeviceId"]?.ToString() ?? "";
                    disks.Add(new PhysicalDiskInfo(
                        id,
                        o["FriendlyName"]?.ToString()?.Trim() is { Length: > 0 } n ? n : "Disk " + id,
                        Media(ToInt(o["MediaType"])),
                        BusName(ToInt(o["BusType"])),
                        Health(ToInt(o["HealthStatus"])),
                        ToLong(o["Size"]),
                        letters.TryGetValue(id, out var l) ? l.OrderBy(c => c).ToList() : new List<char>(),
                        reliability.GetValueOrDefault(id)));
                }
            }
            return disks.OrderBy(d => d.Letters.Count == 0 ? 'z' : d.Letters[0]).ThenBy(d => d.DeviceId, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return Array.Empty<PhysicalDiskInfo>();
        }
    }

    private static Dictionary<string, List<char>> Letters(ManagementScope scope)
    {
        var map = new Dictionary<string, List<char>>(StringComparer.Ordinal);
        foreach (var o in Query(scope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
        {
            using (o)
            {
                var letter = o["DriveLetter"] switch { char c => c, ushort u => (char)u, int i => (char)i, string s when s.Length > 0 => s[0], _ => '\0' };
                if (!char.IsAsciiLetter(letter)) continue;
                var disk = o["DiskNumber"]?.ToString() ?? "";
                if (!map.TryGetValue(disk, out var list)) map[disk] = list = new List<char>();
                list.Add(char.ToUpperInvariant(letter));
            }
        }
        return map;
    }

    private static Dictionary<string, ReliabilityInfo> Reliability(ManagementScope scope)
    {
        var map = new Dictionary<string, ReliabilityInfo>(StringComparer.Ordinal);
        try
        {
            foreach (var o in Query(scope, "SELECT DeviceId, Temperature, Wear, ReadErrorsTotal, WriteErrorsTotal, PowerOnHours FROM MSFT_StorageReliabilityCounter"))
            {
                using (o)
                {
                    map[o["DeviceId"]?.ToString() ?? ""] = new ReliabilityInfo(
                        NullableInt(o["Temperature"]), NullableInt(o["Wear"]),
                        NullableLong(o["ReadErrorsTotal"]), NullableLong(o["WriteErrorsTotal"]), NullableLong(o["PowerOnHours"]));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Needs admin: the page says "available with admin".
        }
        return map;
    }

    private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var o in results) yield return o;
    }

    internal static DriveMedia Media(int mediaType) => mediaType switch { 3 => DriveMedia.Hdd, 4 or 5 => DriveMedia.Ssd, _ => DriveMedia.Unknown };

    internal static DiskHealth Health(int status) => status switch { 0 => DiskHealth.Healthy, 1 => DiskHealth.Warning, 2 => DiskHealth.Unhealthy, _ => DiskHealth.Unknown };

    internal static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 3 => "ATA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
        12 => "SD card", 13 => "MMC", 15 => "Virtual (file-backed)", 16 => "Storage Spaces", 17 => "NVMe", _ => "Other",
    };

    private static int ToInt(object? o) => o is null ? -1 : Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture);
    private static long ToLong(object? o) => o is null ? 0 : Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture);
    private static int? NullableInt(object? o) => o is null ? null : Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture);
    private static long? NullableLong(object? o) => o is null ? null : Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture);
}
