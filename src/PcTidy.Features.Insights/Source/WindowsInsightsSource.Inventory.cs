using System.Management;
using Microsoft.Win32;

namespace PcTidy.Features.Insights.Source;

/// <summary>
/// The System info inventory: read-only WMI and registry reads. The queries name their columns, and none of them asks for
/// a serial number, UUID, asset tag, MAC or IP address, product key, computer name or user name.
/// </summary>
public sealed partial class WindowsInsightsSource
{
    public InventoryFacts ReadInventory() => new()
    {
        Cpu = Try(ReadCpuInventory),
        Board = Try(ReadBoard),
        Memory = Try(ReadMemoryInventory) ?? Array.Empty<MemoryInventory>(),
        MemorySlots = Try(() => (int?)ReadSlots()) ?? 0,
        Gpus = Try(ReadGpus) ?? Array.Empty<GpuInventory>(),
        Disks = Try(ReadDisks) ?? Array.Empty<DiskInventory>(),
        Network = Try(ReadNetwork) ?? Array.Empty<NetworkInventory>(),
    };

    private CpuInventory? ReadCpuInventory()
    {
        foreach (var o in Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"))
            using (o)
                return new CpuInventory(
                    (o["Name"] as string ?? ReadCpuName() ?? "").Trim(),
                    (int)ToLong(o["NumberOfCores"]), (int)ToLong(o["NumberOfLogicalProcessors"]), (int)ToLong(o["MaxClockSpeed"]),
                    (int)ToLong(o["L2CacheSize"]), (int)ToLong(o["L3CacheSize"]), (o["SocketDesignation"] as string ?? "").Trim());
        return ReadCpuName() is { } name ? new CpuInventory(name, 0, Environment.ProcessorCount, 0, 0, 0, "") : null;
    }

    private static BoardInventory? ReadBoard()
    {
        string sysMaker = "", sysModel = "", boardMaker = "", boardModel = "", biosVendor = "", biosVersion = "";
        DateTime? biosDate = null;
        foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            using (o) { sysMaker = Text(o["Manufacturer"]); sysModel = Text(o["Model"]); }
        foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            using (o) { boardMaker = Text(o["Manufacturer"]); boardModel = Text(o["Product"]); }
        foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            using (o)
            {
                biosVendor = Text(o["Manufacturer"]);
                biosVersion = Text(o["SMBIOSBIOSVersion"]);
                biosDate = WmiDate(o["ReleaseDate"]);
            }
        return sysMaker.Length + sysModel.Length + boardMaker.Length + boardModel.Length + biosVersion.Length == 0
            ? null
            : new BoardInventory(sysMaker, sysModel, boardMaker, boardModel, biosVendor, biosVersion, biosDate);
    }

    private static IReadOnlyList<MemoryInventory> ReadMemoryInventory()
    {
        var list = new List<MemoryInventory>();
        foreach (var o in Query(@"root\cimv2", "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
            using (o)
                list.Add(new MemoryInventory(
                    (o["DeviceLocator"] as string ?? o["BankLabel"] as string ?? "").Trim(), ToLong(o["Capacity"]),
                    (int)ToLong(o["ConfiguredClockSpeed"]), (int)ToLong(o["Speed"]), Text(o["Manufacturer"]), Text(o["PartNumber"])));
        return list;
    }

    private static int ReadSlots()
    {
        int slots = 0;
        foreach (var o in Query(@"root\cimv2", "SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray"))
            using (o)
                if (ToLong(o["Use"]) is 3 or 0) slots += (int)ToLong(o["MemoryDevices"]);
        return slots;
    }

    private static IReadOnlyList<GpuInventory> ReadGpus()
    {
        var sizes = GpuMemoryFromRegistry();
        var list = new List<GpuInventory>();
        foreach (var o in Query(@"root\cimv2", "SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
            using (o)
            {
                var name = Text(o["Name"]);
                if (name.Length == 0) continue;
                // AdapterRAM is 32-bit (tops out just under 4 GB); the display driver's registry value holds the real size.
                long memory = sizes.TryGetValue(name, out var reg) && reg > 0 ? reg : ToLong(o["AdapterRAM"]);
                list.Add(new GpuInventory(name, Text(o["DriverVersion"]), WmiDate(o["DriverDate"]), memory));
            }
        return list;
    }

    /// <summary>
    /// Display adapters' memory from the display class key (DriverDesc + HardwareInformation.qwMemorySize).
    /// Verify: the value name is widely used but not in Microsoft's docs; read-only, information only.
    /// </summary>
    private static Dictionary<string, long> GpuMemoryFromRegistry()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = TryOpen(lm, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var sub in Safe(() => cls?.GetSubKeyNames()) ?? Array.Empty<string>())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            using var key = TryOpen(cls, sub);
            var desc = Str(key, "DriverDesc");
            if (desc.Length == 0) continue;
            var size = Safe(() => key?.GetValue("HardwareInformation.qwMemorySize")) switch
            {
                long l => l,
                byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
                _ => 0L,
            };
            if (size > 0) map[desc] = size;
        }
        return map;
    }

    /// <summary>MSFT_PhysicalDisk (documented MediaType 3 = HDD, 4 = SSD; BusType 17 = NVMe, 11 = SATA, 7 = USB).</summary>
    private static IReadOnlyList<DiskInventory> ReadDisks()
    {
        var list = new List<DiskInventory>();
        foreach (var o in Query(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk"))
            using (o)
            {
                var media = ToLong(o["MediaType"]) switch { 3 => DiskMedia.Hdd, 4 => DiskMedia.Ssd, _ => DiskMedia.Unknown };
                var bus = ToLong(o["BusType"]) switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD card", 13 => "MMC", 3 => "ATA", 1 => "SCSI", 15 => "File-backed virtual", 14 => "Virtual",
                    _ => "",
                };
                list.Add(new DiskInventory(Text(o["FriendlyName"]), ToLong(o["Size"]), media, bus));
            }
        return list;
    }

    /// <summary>Physical adapters only (Win32_NetworkAdapter.PhysicalAdapter); NetConnectionStatus 2 = connected.</summary>
    private static IReadOnlyList<NetworkInventory> ReadNetwork()
    {
        var list = new List<NetworkInventory>();
        foreach (var o in Query(@"root\cimv2", "SELECT Name, Speed, NetConnectionStatus FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
            using (o)
            {
                var name = Text(o["Name"]);
                if (name.Length == 0) continue;
                list.Add(new NetworkInventory(name, KindOf(name), ToLong(o["Speed"]), ToLong(o["NetConnectionStatus"]) == 2));
            }
        return list;
    }

    internal static NetworkKind KindOf(string adapterName)
    {
        if (adapterName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return NetworkKind.Bluetooth;
        if (adapterName.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || adapterName.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || adapterName.Contains("802.11", StringComparison.OrdinalIgnoreCase))
            return NetworkKind.WiFi;
        if (adapterName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || adapterName.Contains("GbE", StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("LAN", StringComparison.OrdinalIgnoreCase))
            return NetworkKind.Ethernet;
        return NetworkKind.Other;
    }

    private static string Text(object? o) => (o as string ?? "").Trim();

    private static DateTime? WmiDate(object? o)
    {
        if (o is not string s || s.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException) { return null; }
    }

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException
                                       or IOException or System.Security.SecurityException or InvalidCastException or FormatException) { return null; }
    }

    private static int? Try(Func<int?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException) { return null; }
    }
}
