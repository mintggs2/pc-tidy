using Microsoft.Win32;

namespace PcTidy.Safety.Rules;

/// <summary>What the guard could learn about a device's setup class.</summary>
public enum DeviceClassState
{
    /// <summary>The device's class was read.</summary>
    Found,
    /// <summary>Windows doesn't know this device.</summary>
    Missing,
    /// <summary>The device is known but its class couldn't be read.</summary>
    Unreadable,
}

public sealed record DeviceClassInfo(DeviceClassState State, string? ClassName = null, Guid? ClassGuid = null);

/// <summary>A device's setup class (read-only). The guard uses it to keep boot-critical devices out of DeviceSetEnabled.</summary>
public interface IDeviceClassSource
{
    DeviceClassInfo Find(string instanceId);
}

/// <summary>
/// Reads <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance id&gt;</c> values Class and ClassGUID (64-bit view,
/// read-only). Only called with an instance id the guard has already checked (no "." or ".." parts).
/// </summary>
public sealed class RegistryDeviceClasses : IDeviceClassSource
{
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";

    public static RegistryDeviceClasses Instance { get; } = new();

    public DeviceClassInfo Find(string instanceId)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(EnumKey + "\\" + instanceId, writable: false);
            if (key is null) return new DeviceClassInfo(DeviceClassState.Missing);
            var name = key.GetValue("Class") as string;
            var guid = Guid.TryParse(key.GetValue("ClassGUID") as string, out var g) ? g : (Guid?)null;
            return string.IsNullOrWhiteSpace(name) && guid is null
                ? new DeviceClassInfo(DeviceClassState.Unreadable)
                : new DeviceClassInfo(DeviceClassState.Found, name, guid);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return new DeviceClassInfo(DeviceClassState.Unreadable);
        }
    }
}

/// <summary>
/// Device classes PC Tidy never turns on or off: a change there could stop Windows from starting (disks, volumes,
/// storage controllers, firmware, CPU, the computer itself, TPM, security accelerators). Names and Microsoft's system-defined setup class
/// GUIDs (verify the GUIDs against Microsoft Learn, "System-Defined Device Setup Classes Available to Vendors").
/// </summary>
public static class ProtectedDeviceClasses
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DiskDrive", "Volume", "VolumeSnapshot", "SCSIAdapter", "HDC", "System", "Firmware", "Processor", "Computer", "SecurityDevices",
        "SecurityAccelerator",
    };

    public static readonly IReadOnlySet<Guid> Guids = new HashSet<Guid>
    {
        new("4d36e967-e325-11ce-bfc1-08002be10318"), // DiskDrive
        new("71a27cdd-812a-11d0-bec7-08002be2092f"), // Volume
        new("533c5b84-ec70-11d2-9505-00c04f79deaf"), // VolumeSnapshot
        new("4d36e97b-e325-11ce-bfc1-08002be10318"), // SCSIAdapter
        new("4d36e96a-e325-11ce-bfc1-08002be10318"), // HDC
        new("4d36e97d-e325-11ce-bfc1-08002be10318"), // System
        new("f2e7dd72-6468-4e36-b6f1-6488f42c1b52"), // Firmware
        new("50127dc3-0f36-415e-a6cc-4cb3be910b65"), // Processor
        new("4d36e966-e325-11ce-bfc1-08002be10318"), // Computer
        new("d94ee5d8-d189-4994-83d2-f68d7d41b0e6"), // SecurityDevices
        new("268c95a1-edfe-11d3-95c3-0010dc4050a5"), // SecurityAccelerator
    };

    public static bool IsProtected(string? className, Guid? classGuid)
        => (className is { } n && Names.Contains(n.Trim())) || (classGuid is { } g && Guids.Contains(g));
}
