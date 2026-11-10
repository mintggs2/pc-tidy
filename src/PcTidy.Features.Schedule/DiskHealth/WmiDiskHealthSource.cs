using System.Management;
using System.Runtime.InteropServices;

namespace PcTidy.Features.Schedule.DiskHealth;

/// <summary>
/// Windows' Storage WMI provider (root\Microsoft\Windows\Storage), read-only: MSFT_PhysicalDisk.HealthStatus for every
/// disk, and its MSFT_StorageReliabilityCounter (temperature, wear, power-on hours) when Windows lets this account
/// read it (often admin only; left out otherwise). Never runs a method that changes anything.
/// </summary>
public sealed class WmiDiskHealthSource : IDiskHealthSource
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public IReadOnlyList<DiskReading> Read(CancellationToken ct)
    {
        var disks = new List<DiskReading>();
        try
        {
            var scope = new ManagementScope(Scope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT ObjectId, DeviceId, FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(20), ReturnImmediately = true });
            foreach (ManagementObject disk in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                using (disk)
                {
                    var id = disk["DeviceId"]?.ToString() ?? "?";
                    var reading = new DiskReading
                    {
                        Id = id,
                        Name = (disk["FriendlyName"] as string)?.Trim() is { Length: > 0 } n ? n : $"Disk {id}",
                        Media = Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => DriveMedia.Hdd, 4 => DriveMedia.Ssd, _ => DriveMedia.Unknown },
                        Health = Convert.ToInt32(disk["HealthStatus"] ?? 5) switch
                        {
                            0 => DiskHealthStatus.Healthy,
                            1 => DiskHealthStatus.Warning,
                            2 => DiskHealthStatus.Unhealthy,
                            _ => DiskHealthStatus.Unknown,
                        },
                    };
                    disks.Add(WithReliability(scope, disk["ObjectId"] as string, reading));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            // The Storage provider isn't available here: no disks reported ("not available on this PC").
        }
        return disks;
    }

    private static DiskReading WithReliability(ManagementScope scope, string? objectId, DiskReading reading)
    {
        if (string.IsNullOrEmpty(objectId)) return reading;
        try
        {
            var escaped = objectId.Replace("\\", "\\\\").Replace("\"", "\\\"");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{escaped}\"}} WHERE AssocClass = MSFT_PhysicalDiskToStorageReliabilityCounter"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
            foreach (ManagementObject counter in searcher.Get())
            {
                using (counter)
                {
                    return reading with
                    {
                        TemperatureC = Positive(counter["Temperature"]),
                        WearPercent = Number(counter["Wear"]),
                        PowerOnHours = Positive(counter["PowerOnHours"]),
                    };
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            // Reliability counters need admin on most PCs: temperature and wear are simply left out.
        }
        return reading;
    }

    private static double? Number(object? v) => v is null ? null : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
    private static double? Positive(object? v) => Number(v) is { } d && d > 0 ? d : null;
}
