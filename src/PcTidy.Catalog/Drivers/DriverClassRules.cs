using System.Text.RegularExpressions;

namespace PcTidy.Catalog.Drivers;

/// <summary>
/// Which device setup classes PC Tidy treats carefully (Research: "What breaks", RAPR rule, Ghost devices).
/// Class names come from pnputil's ClassName element (setup class names are not localized).
/// </summary>
public static partial class DriverClassRules
{
    /// <summary>
    /// Driver-store cleanup leaves these classes alone unless the user picks a package one by one:
    /// system/chipset (USB and sleep problems), storage (boot failure), network, Bluetooth, display (basic display,
    /// broken GPU tools) and input (lost keyboard or mouse). Firmware is included because a bad pick can't be undone easily.
    /// </summary>
    public static readonly IReadOnlySet<string> ImportantStoreClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Computer", "Processor", "Firmware",
        "SCSIAdapter", "HDC", "DiskDrive", "Volume", "StorageVolume", "VolumeSnapshot", "SecurityDevices",
        "Net", "NetService", "NetTrans", "NetClient",
        "Bluetooth",
        "Display",
        "HIDClass", "Keyboard", "Mouse",
    };

    /// <summary>Ghost devices in these classes are never offered: software/system devices, disks, volumes and snapshots.</summary>
    public static readonly IReadOnlySet<string> GhostNeverClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Computer", "Processor", "Firmware", "SoftwareDevice", "SoftwareComponent",
        "DiskDrive", "Volume", "StorageVolume", "VolumeSnapshot", "SCSIAdapter", "HDC", "SecurityDevices",
    };

    /// <summary>Instance-id prefixes (enumerators) of software-created devices that Windows recreates by itself.</summary>
    public static readonly IReadOnlyList<string> GhostNeverEnumerators = new[]
    {
        @"ROOT\", @"SWD\", @"HTREE\", @"STORAGE\", @"SCSI\", @"UMB\", @"ACPI_HAL\",
    };

    /// <summary>A ghost device must be gone for at least this long before it is offered.</summary>
    public static readonly TimeSpan GhostQuietPeriod = TimeSpan.FromDays(30);

    /// <summary>No driver-store cleanup within this long after any driver change (it would remove the rollback copy).</summary>
    public static readonly TimeSpan StoreCleanupQuietPeriod = TimeSpan.FromDays(14);

    /// <summary>Only third-party published packages ("oem12.inf") may ever be removed from the driver store.</summary>
    public static bool IsOemInf(string? inf) => inf is not null && OemInf().IsMatch(inf.Trim());

    /// <summary>
    /// Device instance ids PC Tidy's safety rules accept for a remove/rollback step (same pattern as the SafetyGuard):
    /// a device with any other character in its id is shown, never offered.
    /// </summary>
    public static bool IsSafeInstanceId(string? instanceId) => instanceId is not null && SafeInstanceId().IsMatch(instanceId);

    public static bool IsImportantStoreClass(string? className) => className is not null && ImportantStoreClasses.Contains(className.Trim());

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemInf();

    [GeneratedRegex(@"^[A-Za-z0-9\\&_.\-{}#]{1,400}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeInstanceId();
}
