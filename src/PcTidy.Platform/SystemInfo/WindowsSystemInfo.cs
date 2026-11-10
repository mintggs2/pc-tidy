using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using PcTidy.Core.Model;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>
/// Real <see cref="ISystemInfo"/>: facts about this PC from WMI, the registry and Win32 APIs (never localized text).
/// Read-only. Slow facts (GPU, chassis, disk media) are read once; drive space and reboot state are read fresh.
/// </summary>
public sealed class WindowsSystemInfo : ISystemInfo
{
    private readonly Lazy<(GpuVendor Primary, IReadOnlyList<string> Names)> _gpu = new(ReadGpus);
    private readonly Lazy<bool> _isLaptop = new(ReadIsLaptop);
    private readonly Lazy<bool> _isAdmin = new(ReadIsAdmin);
    // Read once, and again when a drive letter shows up that it doesn't know (a disk plugged in since).
    private Dictionary<char, (DriveMedia Media, bool External)>? _media;

    public GpuVendor PrimaryGpu => _gpu.Value.Primary;
    public IReadOnlyList<string> GpuNames => _gpu.Value.Names;
    public bool IsLaptop => _isLaptop.Value;
    public bool HasBattery => ReadHasBattery();
    public bool IsAdmin => _isAdmin.Value;
    public int WindowsBuild => Environment.OSVersion.Version.Build;
    public bool RebootPending => ReadRebootPending();

    public IReadOnlyList<DriveDetails> Drives
    {
        get
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            var list = new List<DriveDetails>();
            var known = _media ??= ReadDriveMedia();
            bool refreshed = false;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var letter = char.ToUpperInvariant(d.Name[0]);
                    if (!known.ContainsKey(letter) && !refreshed) { known = _media = ReadDriveMedia(); refreshed = true; }
                    var (media, external) = known.TryGetValue(letter, out var m) ? m : (DriveMedia.Unknown, false);
                    list.Add(new DriveDetails(d.Name, d.VolumeLabel, media, d.TotalSize, d.AvailableFreeSpace,
                        d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)) { IsExternal = external });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A drive that vanished or can't be read is simply not listed.
                }
            }
            return list;
        }
    }

    public IReadOnlySet<string> RunningProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { names.Add(p.ProcessName.ToLowerInvariant()); }
                catch (InvalidOperationException) { }
            }
        }
        return names;
    }

    // ------------------------------------------------------------------ readers

    internal static GpuVendor VendorFromPnpId(string? pnpDeviceId)
    {
        var id = pnpDeviceId?.ToUpperInvariant() ?? "";
        if (id.Contains("VEN_10DE")) return GpuVendor.Nvidia;
        if (id.Contains("VEN_1002") || id.Contains("VEN_1022")) return GpuVendor.Amd;
        if (id.Contains("VEN_8086")) return GpuVendor.Intel;
        if (id.Contains("VEN_1414")) return GpuVendor.Microsoft;
        return id.Length == 0 ? GpuVendor.Unknown : GpuVendor.Other;
    }

    private static (GpuVendor, IReadOnlyList<string>) ReadGpus()
    {
        try
        {
            var gpus = new List<(GpuVendor Vendor, string Name, ulong Memory)>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, AdapterRAM FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var vendor = VendorFromPnpId(mo["PNPDeviceID"] as string);
                    var name = (mo["Name"] as string ?? "").Trim();
                    var memory = mo["AdapterRAM"] is uint ram ? ram : 0u;
                    if (name.Length > 0) gpus.Add((vendor, name, memory));
                }
            }
            // The primary GPU is the real one with the most memory; Microsoft's basic/remote adapters come last.
            var primary = gpus.OrderBy(g => g.Vendor is GpuVendor.Microsoft or GpuVendor.Unknown ? 1 : 0)
                              .ThenByDescending(g => g.Memory).Select(g => g.Vendor).FirstOrDefault();
            return (primary, gpus.Select(g => g.Name).ToList());
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return (GpuVendor.Unknown, Array.Empty<string>());
        }
    }

    private static bool ReadIsLaptop()
    {
        // SMBIOS chassis types for portable PCs: laptop, notebook, sub-notebook, convertible, detachable, tablet...
        int[] portable = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    if (mo["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t))) return true;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // fall through to the battery check
        }
        return ReadHasBattery();
    }

    private static bool ReadHasBattery()
        => GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = no battery, 255 = unknown

    /// <summary>True for an admin account even when PC Tidy runs non-elevated (a "limited" UAC token).</summary>
    private static bool ReadIsAdmin()
    {
        if (ProcessElevation.IsElevated) return true;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (GetTokenInformation(id.Token, TokenElevationType, out var type, sizeof(int), out _))
                return type == 3; // TokenElevationTypeLimited: an admin running with UAC's filtered token
        }
        catch
        {
            // unknown: assume not admin
        }
        return false;
    }

    private static bool ReadRebootPending()
    {
        try
        {
            using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var cbs = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) return true;
            using (var wu = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) return true;
            using var sm = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 } ops && ops.Any(o => o.Length > 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Storage bus types (STORAGE_BUS_TYPE / MSFT_Disk.BusType) of disks that sit outside the PC and can be unplugged.</summary>
    internal static bool IsExternalBus(ushort busType) => busType is 4 /* 1394 */ or 7 /* USB */ or 12 /* SD */ or 13 /* MMC */;

    /// <summary>
    /// Drive letter → SSD/HDD (MSFT_PhysicalDisk.MediaType 3 = HDD, 4 = SSD) and whether its disk is on an external bus
    /// (MSFT_Disk.BusType / MSFT_PhysicalDisk.BusType), joined through MSFT_Partition.DiskNumber. Most USB disks report
    /// DriveType.Fixed, so the bus type is the only reliable "can be unplugged" signal.
    /// </summary>
    private static Dictionary<char, (DriveMedia Media, bool External)> ReadDriveMedia()
    {
        var map = new Dictionary<char, (DriveMedia, bool)>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var diskMedia = new Dictionary<uint, DriveMedia>();
            var external = new HashSet<uint>();
            using (var disks = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
            using (var results = disks.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        if (!uint.TryParse(mo["DeviceId"] as string, out var number)) continue;
                        diskMedia[number] = Convert.ToUInt16(mo["MediaType"] ?? 0) switch { 3 => DriveMedia.Hdd, 4 => DriveMedia.Ssd, _ => DriveMedia.Unknown };
                        if (IsExternalBus(Convert.ToUInt16(mo["BusType"] ?? 0))) external.Add(number);
                    }
                }
            }
            // MSFT_Disk also lists disks the physical-disk view leaves out (some USB sticks and card readers).
            using (var disks = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, BusType FROM MSFT_Disk")))
            using (var results = disks.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        if (mo["Number"] is null) continue;
                        if (IsExternalBus(Convert.ToUInt16(mo["BusType"] ?? 0))) external.Add(Convert.ToUInt32(mo["Number"]));
                    }
                }
            }
            using var parts = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition"));
            using var partResults = parts.Get();
            foreach (ManagementObject mo in partResults)
            {
                using (mo)
                {
                    var letter = mo["DriveLetter"] is char c ? c : Convert.ToChar(Convert.ToUInt16(mo["DriveLetter"] ?? 0));
                    if (!char.IsLetter(letter)) continue;
                    var disk = Convert.ToUInt32(mo["DiskNumber"] ?? uint.MaxValue);
                    var media = diskMedia.TryGetValue(disk, out var m) ? m : DriveMedia.Unknown;
                    map[char.ToUpperInvariant(letter)] = (media, external.Contains(disk));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            // Unknown media is fine: features then avoid SSD/HDD-specific advice.
        }
        return map;
    }
}
