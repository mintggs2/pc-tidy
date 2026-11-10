namespace PcTidy.Catalog.Drivers;

/// <summary>Plain-English names for device setup classes (the same words Device Manager uses).</summary>
public static class DeviceClassNames
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Display"] = "Display adapters",
        ["MEDIA"] = "Sound, video and game controllers",
        ["AudioEndpoint"] = "Audio inputs and outputs",
        ["AudioProcessingObject"] = "Audio effects",
        ["Net"] = "Network adapters",
        ["Bluetooth"] = "Bluetooth",
        ["USB"] = "USB controllers",
        ["USBDevice"] = "USB devices",
        ["HIDClass"] = "Human interface devices",
        ["Keyboard"] = "Keyboards",
        ["Mouse"] = "Mice and other pointing devices",
        ["Monitor"] = "Monitors",
        ["DiskDrive"] = "Disk drives",
        ["SCSIAdapter"] = "Storage controllers",
        ["HDC"] = "Storage controllers",
        ["Volume"] = "Storage volumes",
        ["System"] = "System devices",
        ["Processor"] = "Processors",
        ["Computer"] = "Computer",
        ["Firmware"] = "Firmware",
        ["Ports"] = "Ports (COM and LPT)",
        ["Printer"] = "Printers",
        ["PrintQueue"] = "Print queues",
        ["Camera"] = "Cameras",
        ["Image"] = "Cameras and scanners",
        ["Biometric"] = "Biometric devices",
        ["SecurityDevices"] = "Security devices",
        ["SoftwareDevice"] = "Software devices",
        ["SoftwareComponent"] = "Software components",
        ["Extension"] = "Extensions",
        ["Battery"] = "Batteries",
        ["SmartCardReader"] = "Smart card readers",
        ["WPD"] = "Portable devices",
        ["HSM"] = "Hardware security",
        ["MTD"] = "Memory technology devices",
        ["VolumeSnapshot"] = "Volume shadow copies",
        ["Unknown"] = "Other devices",
        ["MidiEndpoint"] = "MIDI devices",
        ["MediaEndpoint"] = "Media endpoints",
        ["Sensor"] = "Sensors",
        ["SmartCard"] = "Smart cards",
        ["Infrared"] = "Infrared devices",
    };

    /// <summary>Friendly name for a class, or the class name itself ("Other devices" when empty).</summary>
    public static string For(string? className)
        => string.IsNullOrWhiteSpace(className) ? "Other devices"
            : Names.TryGetValue(className.Trim(), out var n) ? n : className.Trim();
}
